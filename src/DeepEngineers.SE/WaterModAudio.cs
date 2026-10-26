using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The Water Mod's underwater sound (ARCHITECTURE §5.3 "Mechanism (Water Mod)", CONTRACT §1.1 first two rows, O10).
 /// Never <c>/wvolume</c> (it persists <c>WaterClientSettings.xml</c> at once and prints a chat line); the file is never
 /// written by us.
 /// <list type="bullet">
 /// <item>Preferred (HYPOTHESIS: Harmony patches a mod's in-memory script assembly like any other): a postfix on
 /// <c>Jakaria.SessionComponents.WaterSoundComponent.UpdateAfterSimulation</c>
 /// that scales the five
 /// emitter fields (<c>MyEntity3DSoundEmitter</c>) by 1 − mix: 0 underwater, 1 above water. Settings untouched.
 /// Names resolved at run time in the mod's assembly, only when <c>Jakaria.WaterData.Version</c> is the pinned "3.23".</item>
 /// <item>Fallback (the patch could not be applied): in-memory <c>WaterSettingsComponent.Settings.Volume</c>
 /// json</c> before the
 /// first change, (ii) a prefix on <c>MySession.
 /// before SE saves (the mod's <c>SaveData</c> writes the file then) and a finalizer re-applying the scaled
 /// value, (iii) a restore at the next session after an unclean end (Pulsar kills SE on exit).</item>
 /// <item>S2.M13 (seeded failure "Water Mod volume through /wvolume"): a logged refusal; nothing is sent, nothing muted.</item>
 /// </list>
 /// Update thread only (the mod's session component and our plugin Update both run there).
 /// </summary>
    internal static class WaterModAudio
    {
        public const string PinnedVersion = "3.23";
        public const string SoundComponentType = "Jakaria.SessionComponents.WaterSoundComponent";
        public const string SettingsComponentType = "Jakaria.SessionComponents.WaterSettingsComponent";
        public const string SessionType = "Jakaria.Session";
        public const string RestoreFileName = "restore.json";
        public static readonly string[] EmitterFields =
        {
            "_environmentUnderwaterSoundEmitter", "_environmentOceanSoundEmitter", "_environmentBeachSoundEmitter",
            "_ambientSoundEmitter", "_ambientBoatSoundEmitter",
        };

        private enum Mode { None, Emitters, Fallback, Unavailable }

        private static Mode _mode;
        private static Assembly _assembly;
        private static readonly HashSet<MethodBase> Patched = new HashSet<MethodBase>();
        private static FieldInfo[] _emitterFields;
        private static readonly float[] BaseValue = new float[5];
        private static readonly float[] Written = new float[5];
        private static readonly bool[] Touched = new bool[5];
        private static bool _scaledAny;
        private static long _postfixRuns, _scaledTicks;
        private static string _why = "not resolved yet";
        private static ulong _nextResolveMs;
        private static float _lastScale = 1f;
        private static bool _m13Logged;

        // fallback
        private static object _settingsComponent;
        private static FieldInfo _settingsField, _volumeField;
        private static float _ownerVolume = float.NaN;
        private static bool _fallbackChanged;
        private static float _fallbackApplied = float.NaN;
        private static bool _inSave;
        // crash recovery (restore.json from a session that did not end cleanly)
        private static float _recoverOriginal = float.NaN;
        private static bool _recoverChecked;

        public static Action<string> Log = s => DeLog.Info(s);

        public static string Describe()
        {
            return "mode=" + _mode + " why=" + _why + " assembly=" + (_assembly != null ? _assembly.GetName().Name : "none") +
                   " postfixRuns=" + _postfixRuns + " scaledTicks=" + _scaledTicks + " scale=" + _lastScale.ToString("0.###", CultureInfo.InvariantCulture) +
                   (_mode == Mode.Fallback ? " ownerVolume=" + _ownerVolume.ToString("R", CultureInfo.InvariantCulture) + " applied=" + _fallbackApplied.ToString("R", CultureInfo.InvariantCulture) : "") +
                   (S2Mutants.WaterVolumeByCommand ? " MUTANT S2.M13" : "");
        }

        // ======================================================================================== plugin Init

 /// <summary>Plugin Init: a <c>restore.json</c> left pending by a process that is gone is applied at the next session.</summary>
        public static void CheckRestoreAtInit()
        {
            try
            {
                IDictionary<string, object> root = ReadRestore();
                IDictionary<string, object> w = MiniJson.GetObject(root, "waterModVolume");
                if (w == null || MiniJson.GetBool(w, "pending") != true) return;
                double? original = MiniJson.GetNumber(w, "original");
                double? pid = MiniJson.GetNumber(w, "pid");
                if (!original.HasValue) return;
                _recoverOriginal = (float)original.Value;
                Log("DE.Audio restore.json: Water Mod volume restore pending from pid " + (pid.HasValue ? ((long)pid.Value).ToString(CultureInfo.InvariantCulture) : "?") +
                    " (owner's value " + _recoverOriginal.ToString("R", CultureInfo.InvariantCulture) + "); applied in memory when the Water Mod loads");
            }
            catch (Exception e) { Log("DE.Audio restore.json unreadable at Init: " + e.GetType().Name + ": " + e.Message); }
        }

        // ======================================================================================== plugin Update

 /// <summary>
 /// Plugin Update: resolves the mod (every 10 s until found, once the site's Water Mod API is pinned), keeps the
 /// M13 refusal.
 /// </summary>
        public static void Tick(ulong nowMs, float scale, bool inWorld, bool apiPinned)
        {
            _lastScale = scale;
            if (!inWorld) { if (_mode != Mode.None) Forget("world unloaded"); return; }
            if (S2Mutants.WaterVolumeByCommand)
            {
                if (scale < 1f && !_m13Logged)
                {
                    _m13Logged = true;
                    Log("DE.Audio MUTANT S2.M13: the Water Mod volume would be set with the chat command '/wvolume 0' (which writes " +
                        "WaterClientSettings.xml at once): REFUSED, never sent; the Water Mod underwater sound stays audible");
                }
                if (scale >= 1f) _m13Logged = false;
            }
            if (_mode == Mode.None && apiPinned && nowMs >= _nextResolveMs)
            {
                _nextResolveMs = nowMs + 10000;
                Resolve();
            }
            if (_mode == Mode.Fallback) FallbackApply(S2Mutants.WaterVolumeByCommand ? 1f : scale);
        }

        private static void Forget(string why)
        {
            _mode = Mode.None;
            _assembly = null;
            _emitterFields = null;
            _settingsComponent = null;
            _ownerVolume = float.NaN;
            _fallbackChanged = false;
            _fallbackApplied = float.NaN;
            _recoverChecked = false;
            for (int i = 0; i < Touched.Length; i++) Touched[i] = false;
            _why = why;
        }

        private static void Resolve()
        {
            Assembly asm = FindModAssembly(out _why);
            if (asm == null) return;
            _assembly = asm;
            RecoverIfPending(asm);
            Type sound = asm.GetType(SoundComponentType, false);
            MethodInfo update = sound != null ? sound.GetMethod("UpdateAfterSimulation", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null) : null;
            var fields = new FieldInfo[EmitterFields.Length];
            bool fieldsOk = sound != null;
            for (int i = 0; i < EmitterFields.Length && fieldsOk; i++)
            {
                fields[i] = sound.GetField(EmitterFields[i], BindingFlags.Instance | BindingFlags.NonPublic);
                if (fields[i] == null || !typeof(MyEntity3DSoundEmitter).IsAssignableFrom(fields[i].FieldType)) fieldsOk = false;
            }
            string patchWhy = null;
            bool patched = update != null && update.DeclaringType == sound && fieldsOk && TryPatch(update, out patchWhy);
            if (patched)
            {
                _emitterFields = fields;
                _mode = Mode.Emitters;
                _why = "emitter postfix on " + SoundComponentType + ".UpdateAfterSimulation (assembly " + asm.GetName().Name + ")";
                Log("DE.Audio Water Mod " + PinnedVersion + ": underwater emitters muted by " + _why + "; WaterClientSettings untouched");
                return;
            }
            if (patchWhy == null)
                patchWhy = sound == null ? SoundComponentType + " not found" : update == null || update.DeclaringType != sound
                    ? "UpdateAfterSimulation not declared on it" : "emitter fields not found";
            // fallback
            if (ResolveFallback(asm))
            {
                _mode = Mode.Fallback;
                _why = "fallback (in-memory Settings.Volume): preferred path unavailable: " + patchWhy;
                Log("DE.Audio Water Mod: " + _why + "; owner's volume " + _ownerVolume.ToString("R", CultureInfo.InvariantCulture) +
                    " kept in restore.json while changed, written back around every SE save" + (S2Patches.SessionSaveOk ? "" : " (WARNING: MySession.Save hook missing)"));
            }
            else
            {
                _mode = Mode.Unavailable;
                _why = "unavailable: " + patchWhy + "; fallback: " + _why;
                Log("DE.Audio Water Mod underwater sound NOT controlled (" + _why + "); it stays at the owner's volume underwater");
            }
        }

        private static Assembly FindModAssembly(out string why)
        {
            why = "Jakaria.WaterData not found in any loaded assembly";
            Assembly best = null;
            string wanted = SiteBinding.Api != null ? SiteBinding.Api.ModAssembly : null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type data;
                try { data = a.GetType("Jakaria.WaterData", false); } catch (Exception) { continue; }
                if (data == null) continue;
                string version = null;
                try
                {
                    FieldInfo f = data.GetField("Version", BindingFlags.Public | BindingFlags.Static);
                    version = f != null ? f.GetValue(null) as string : null;
                }
                catch (Exception) { }
                if (version != PinnedVersion) { why = "Jakaria.WaterData.Version=" + (version ?? "unreadable") + " (pinned " + PinnedVersion + ")"; continue; }
                if (wanted != null && a.GetName().Name != wanted) continue;
                best = a; // the last match: the most recently loaded scripts of this name
            }
            if (best != null) why = null;
            return best;
        }

        private static bool TryPatch(MethodInfo update, out string why)
        {
            why = null;
            if (Patched.Contains(update)) return true;
            try
            {
                MethodInfo post = typeof(WaterModAudio).GetMethod(nameof(SoundPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                (S2Patches.Harmony ?? new Harmony(HarmonyPatches.HarmonyId)).Patch(update, postfix: new HarmonyMethod(post));
                Patched.Add(update);
                DeLog.Info("DE.Harmony S2 patched " + SoundComponentType + ".UpdateAfterSimulation() postfix (Water Mod " + PinnedVersion + " in-memory script assembly)");
                return true;
            }
            catch (Exception e)
            {
                why = e.GetType().Name + ": " + e.Message;
                DeLog.Info("DE.Harmony MISSING Water Mod " + SoundComponentType + ".UpdateAfterSimulation patch failed: " + why + " (fallback path)");
                return false;
            }
        }

        // ============================================================================================ postfix

 /// <summary>The Water Mod sound component's UpdateAfterSimulation postfix (update thread).</summary>
        private static void SoundPostfix(object __instance)
        {
            try
            {
                _postfixRuns++;
                FieldInfo[] fields = _emitterFields;
                if (_mode != Mode.Emitters || fields == null || __instance == null) return;
                float scale = S2Mutants.WaterVolumeByCommand ? 1f : _lastScale;
                if (scale >= 1f && !_scaledAny) return;
                bool any = false;
                for (int i = 0; i < fields.Length; i++)
                {
                    var e = fields[i].GetValue(__instance) as MyEntity3DSoundEmitter;
                    if (e == null) continue;
                    float cur = e.VolumeMultiplier;
                    if (!Touched[i] || cur != Written[i]) BaseValue[i] = cur; // the mod (or the emitter's fade) set a fresh value
                    float v = BaseValue[i] * scale;
                    if (v != cur) e.VolumeMultiplier = v;
                    Written[i] = v;
                    Touched[i] = scale < 1f;
                    any |= scale < 1f;
                }
                _scaledAny = any;
                if (any) _scaledTicks++;
            }
            catch (Exception) { }
        }

        // =========================================================================================== fallback

        private static bool ResolveFallback(Assembly asm)
        {
            try
            {
                Type sessionType = asm.GetType(SessionType, false);
                Type settingsType = asm.GetType(SettingsComponentType, false);
                if (sessionType == null || settingsType == null) { _why = "Jakaria.Session / WaterSettingsComponent not found"; return false; }
                FieldInfo instance = sessionType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                FieldInfo components = sessionType.GetField("_sessionComponents", BindingFlags.Instance | BindingFlags.NonPublic);
                object session = instance != null ? instance.GetValue(null) : null;
                var dict = session != null && components != null ? components.GetValue(session) as IDictionary : null;
                object comp = null;
                if (dict != null) foreach (DictionaryEntry kv in dict) if (kv.Value != null && kv.Value.GetType() == settingsType) comp = kv.Value;
                if (comp == null) { _why = "WaterSettingsComponent instance not found"; return false; }
                FieldInfo settings = settingsType.GetField("Settings", BindingFlags.Public | BindingFlags.Instance);
                FieldInfo volume = settings != null ? settings.FieldType.GetField("Volume", BindingFlags.Public | BindingFlags.Instance) : null;
                if (volume == null || volume.FieldType != typeof(float)) { _why = "Settings.Volume (float) not found"; return false; }
                _settingsComponent = comp;
                _settingsField = settings;
                _volumeField = volume;
                _ownerVolume = ReadVolume();
                return !float.IsNaN(_ownerVolume);
            }
            catch (Exception e) { _why = "fallback reflection " + e.GetType().Name + ": " + e.Message; return false; }
        }

        private static float ReadVolume()
        {
            object boxed = _settingsField.GetValue(_settingsComponent);
            return (float)_volumeField.GetValue(boxed);
        }

        private static void WriteVolume(float v)
        {
            object boxed = _settingsField.GetValue(_settingsComponent);
            _volumeField.SetValue(boxed, v);
            _settingsField.SetValue(_settingsComponent, boxed);
            _fallbackApplied = v;
        }

        private static void FallbackApply(float scale)
        {
            if (_inSave || _settingsComponent == null || float.IsNaN(_ownerVolume)) return;
            try
            {
                if (scale < 1f)
                {
                    if (!_fallbackChanged)
                    {
                        float cur = ReadVolume();
                        if (cur != _fallbackApplied) _ownerVolume = cur;
                        WriteRestore(_ownerVolume, true, "Water Mod Settings.Volume scaled in memory underwater");
                        _fallbackChanged = true;
                    }
                    float v = _ownerVolume * scale;
                    if (v != _fallbackApplied) WriteVolume(v);
                }
                else if (_fallbackChanged)
                {
                    WriteVolume(_ownerVolume);
                    _fallbackChanged = false;
                    WriteRestore(_ownerVolume, false, "restored on surfacing");
                }
            }
            catch (Exception e)
            {
                Log("DE.Audio Water Mod fallback write FAILED " + e.GetType().Name + ": " + e.Message + " (fallback off)");
                _mode = Mode.Unavailable;
            }
        }

        /// <summary>MySession.</summary>
        public static void BeforeSessionSave()
        {
            if (_mode != Mode.Fallback || !_fallbackChanged || _settingsComponent == null) return;
            _inSave = true;
            float applied = _fallbackApplied;
            WriteVolume(_ownerVolume);
            _fallbackApplied = applied; // re-applied by the finalizer
            Log("DE.Audio SE save: Water Mod volume " + _ownerVolume.ToString("R", CultureInfo.InvariantCulture) + " (owner's) restored for the save");
        }

        /// <summary>MySession.Save finalizer: the scaled value again.</summary>
        public static void AfterSessionSave()
        {
            if (!_inSave) return;
            _inSave = false;
            if (_mode == Mode.Fallback && _fallbackChanged && _settingsComponent != null && !float.IsNaN(_fallbackApplied))
            {
                float v = _fallbackApplied;
                WriteVolume(v);
            }
        }

        private static void RecoverIfPending(Assembly asm)
        {
            if (_recoverChecked || float.IsNaN(_recoverOriginal)) return;
            _recoverChecked = true;
            if (!ResolveFallback(asm)) { Log("DE.Audio restore.json pending, but the Water Mod settings are not reachable (" + _why + ")"); return; }
            float cur = _ownerVolume;
            if (cur != _recoverOriginal)
            {
                WriteVolume(_recoverOriginal);
                _ownerVolume = _recoverOriginal;
                Log("DE.Audio restore.json: the Water Mod volume was " + cur.ToString("R", CultureInfo.InvariantCulture) + " after an unclean end; the owner's " +
                    _recoverOriginal.ToString("R", CultureInfo.InvariantCulture) + " set in memory (the mod persists it at the next SE save)");
            }
            else Log("DE.Audio restore.json: the Water Mod volume is already the owner's (" + cur.ToString("R", CultureInfo.InvariantCulture) + "); pending flag cleared");
            WriteRestore(_recoverOriginal, false, "recovered after an unclean end");
            _recoverOriginal = float.NaN;
            _settingsComponent = null; // the mode decides below whether the fallback is used
        }

        // ======================================================================================== restore.json

        public static string RestorePath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepEngineers", RestoreFileName);
        }

        private static IDictionary<string, object> ReadRestore()
        {
            string p = RestorePath();
            if (!File.Exists(p)) return null;
            return MiniJson.Parse(File.ReadAllText(p)) as IDictionary<string, object>;
        }

        /// <summary>Read-modify-write of restore.json (other keys kept), through a temp file and a replace.</summary>
        private static void WriteRestore(float original, bool pending, string note)
        {
            try
            {
                IDictionary<string, object> root = null;
                try { root = ReadRestore(); } catch (Exception) { }
                var d = root != null ? new Dictionary<string, object>(root, StringComparer.Ordinal) : new Dictionary<string, object>(StringComparer.Ordinal);
                d["version"] = 1;
                d["waterModVolume"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "original", (double)original },
                    { "pending", pending },
                    { "pid", (long)DeepEngineers.Wire.Native.Win32.CurrentProcessId() },
                    { "written", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                    { "note", note },
                };
                string path = RestorePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonText.Serialize(d));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception e) { Log("DE.Audio restore.json write FAILED " + e.GetType().Name + ": " + e.Message); }
        }
    }
}
