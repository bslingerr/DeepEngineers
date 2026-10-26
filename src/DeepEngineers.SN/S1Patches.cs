using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The Subnautica-side Harmony patches of S1 (bridge mode only; installed once, never removed in a session). Each
 /// target was read Assembly-CSharp of build 83031 and is looked up with its exact parameter types; a
 /// target that is missing or of another shape logs `DE.Harmony MISSING` and only its feature stays off.
 /// <list type="bullet">
 /// <item>Never save (ROADMAP S1, D7): prefix on <c>IngameMenu.SaveGame</c> (public void, the only caller of the
 /// private SaveGameAsync besides the permadeath quit path) and, as a second guard, on
 /// <c>SaveLoadManager.SaveToDeepStorageAsync</c> (public, returns <c>CoroutineTask&lt;SaveResult&gt;</c>; the copy of
 /// the temporary save into the slot folder). Each blocked attempt logs `DE.SaveBlocked`. The second guard answers
 /// with a completed task whose result is "success, nothing written": a failure result would make IngameMenu open a
 /// modal error dialog under `FreezeTime.Begin(IngameMenu)` that nobody can dismiss (Subnautica never has input).
 /// Seeded failure S1.M4 lets both originals run.</item>
 /// <item>UI suppression (ARCHITECTURE §5.11): <c>ErrorMessage._AddMessage(string)</c> (every Add* entry point ends
 /// there), <c>Subtitles.AddInternal(int, string, int, object[])</c> and <c>AddRawLongInternal(int, StringBuilder,
 /// float, float, float)</c>, the PDA voice queue <c>SoundQueue.Play(string, string)</c> (private; PDASounds.queue),
 /// <c>VoiceNotification.Play(object[])</c>, <c>PDANotification.Play(object[])</c>, popups
 /// <c>uGUI_PopupNotification.Show(Entry)</c>. Private sinks are patched rather than the small public wrappers, which
 /// Mono may have inlined before the patch.</item>
 /// <item>Audio telemetry for S1.N6 (observe only): postfixes on <c>FMOD_CustomEmitter.Play</c> and both
 /// <c>FMODUWE.PlayOneShot</c> overloads count event paths; a 60 s summary is logged.</item>
 /// <item>S1.N6 variant switch (dev, off by default; installed at bridge start, inert until `de.underwater`): a postfix on
 /// <c>Player.IsUnderwater</c> that answers "camera below the ocean level" (the "global" variant).</item>
 /// </list>
 /// </summary>
    internal static class S1Patches
    {
        public const string HarmonyId = "deepengineers.sn";
        private const int MaxSuppressedTextLines = 40;

        private static Harmony _harmony;
        private static bool _coreInstalled;
        private static bool _underwaterInstalled;

        public static bool SaveGuardMenu { get; private set; }
        public static bool SaveGuardDeepStorage { get; private set; }
        public static int UiSinksInstalled { get; private set; }
        public static int AudioHooksInstalled { get; private set; }
        public static readonly List<string> Missing = new List<string>();

        /// <summary>Set when the autoload starts (bridge mode, host session): the UI sinks swallow from then on.</summary>
        public static volatile bool SuppressUi;

        public static long SavesBlocked, SavesAllowedByMutant;
        /// <summary>
 /// S9: the host save is written ONCE, when the plugin itself created the Freedom game at a
 /// first start (HostSave.CreateSave): both guards let the next save through while this is set; the deep-storage guard
 /// clears it (one save, then never again).
 /// </summary>
        public static volatile bool OneSaveWindow;
        public static long SavesAllowedOnce;
        public static long ErrorMessages, SubtitleLines, PdaVoice, VoiceNotifications, PdaNotifications, Popups;
        /// <summary>S5a: PDA voice / voice notification / PDA notification plays that went through (SuppressUi off); the DE.PDA `played` counter.</summary>
        public static long PdaPassed;
        /// <summary>S5a: every FMOD_CustomEmitter.Play that left the emitter playing (the low-O2 alert and bubble emitters are recognised by the S5a host). Main thread; must not throw.</summary>
        public static Action<FMOD_CustomEmitter> EmitterPlayed;
        private static int _suppressedTextLines;

        // S1.N6 variant: 0 off (vanilla: false in locked mode without a sub), 1 global (camera below the ocean level)
        public static volatile int UnderwaterMode;
        public static float UnderwaterCameraY;
        public static float UnderwaterOceanLevel;

        private static readonly Dictionary<string, int> AudioCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private static long _audioTotal;

        /// <summary>Installs the save guards, the UI sinks and the audio telemetry once (main thread, bridge start).</summary>
        public static void InstallCore()
        {
            if (_coreInstalled) return;
            _coreInstalled = true;
            try
            {
                _harmony = new Harmony(HarmonyId);
            }
            catch (Exception e)
            {
                DeLog.Error("DE.Harmony FAILED creating the Harmony instance: " + e.GetType().Name + ": " + e.Message +
                            " (no save guard: the host save will not be loaded)");
                return;
            }

            SaveGuardMenu = Patch(typeof(IngameMenu), "SaveGame", Type.EmptyTypes, typeof(void), false,
                                  "Prefix_IngameMenu_SaveGame", null, "IngameMenu.SaveGame()");
            SaveGuardDeepStorage = Patch(typeof(SaveLoadManager), "SaveToDeepStorageAsync", Type.EmptyTypes,
                                         typeof(CoroutineTask<SaveLoadManager.SaveResult>), false,
                                         "Prefix_SaveToDeepStorageAsync", null, "SaveLoadManager.SaveToDeepStorageAsync()");

            int ui = 0;
            if (Patch(typeof(ErrorMessage), "_AddMessage", new[] { typeof(string) }, typeof(void), false,
                      "Prefix_ErrorMessage", null, "ErrorMessage._AddMessage(string)")) ui++;
            if (Patch(typeof(Subtitles), "AddInternal", new[] { typeof(int), typeof(string), typeof(int), typeof(object[]) }, typeof(void), false,
                      "Prefix_Subtitles", null, "Subtitles.AddInternal(int,string,int,object[])")) ui++;
            if (Patch(typeof(Subtitles), "AddRawLongInternal", new[] { typeof(int), typeof(StringBuilder), typeof(float), typeof(float), typeof(float) },
                      typeof(void), false, "Prefix_Subtitles", null, "Subtitles.AddRawLongInternal(int,StringBuilder,float,float,float)")) ui++;
            if (Patch(typeof(SoundQueue), "Play", new[] { typeof(string), typeof(string) }, typeof(void), false,
                      "Prefix_SoundQueue_Play", null, "SoundQueue.Play(string,string)")) ui++;
            if (Patch(typeof(VoiceNotification), "Play", new[] { typeof(object[]) }, typeof(bool), false,
                      "Prefix_VoiceNotification_Play", null, "VoiceNotification.Play(object[])")) ui++;
            if (Patch(typeof(PDANotification), "Play", new[] { typeof(object[]) }, typeof(void), false,
                      "Prefix_PDANotification_Play", null, "PDANotification.Play(object[])")) ui++;
            if (Patch(typeof(uGUI_PopupNotification), "Show", new[] { typeof(uGUI_PopupNotification.Entry) }, typeof(void), false,
                      "Prefix_Popup_Show", null, "uGUI_PopupNotification.Show(Entry)")) ui++;
            UiSinksInstalled = ui;

            int audio = 0;
            if (Patch(typeof(FMOD_CustomEmitter), "Play", Type.EmptyTypes, typeof(void), false,
                      null, "Postfix_Emitter_Play", "FMOD_CustomEmitter.Play()")) audio++;
            if (Patch(typeof(FMODUWE), "PlayOneShot", new[] { typeof(FMODAsset), typeof(Vector3), typeof(float) }, typeof(void), true,
                      null, "Postfix_OneShotAsset", "FMODUWE.PlayOneShot(FMODAsset,Vector3,float)")) audio++;
            if (Patch(typeof(FMODUWE), "PlayOneShot", new[] { typeof(string), typeof(Vector3), typeof(float) }, typeof(void), true,
                      null, "Postfix_OneShotPath", "FMODUWE.PlayOneShot(string,Vector3,float)")) audio++;
            AudioHooksInstalled = audio;

            // Installed now, before the Main scene's scripts are compiled, so that callers compiled later reach the patch
 // (a small method may be inlined into callers compiled earlier); the postfix is inert until de.underwater.
            EnsureUnderwaterPatch();

            DeLog.Info("DE.Harmony SN " + HarmonyId + " saveGuards=" + (SaveGuardMenu ? "IngameMenu.SaveGame" : "MISSING") + "," +
                       (SaveGuardDeepStorage ? "SaveToDeepStorageAsync" : "MISSING") + " uiSinks=" + ui + "/7 audioHooks=" + audio + "/3" +
                       " isUnderwaterVariant=" + (_underwaterInstalled ? "installed(off)" : "MISSING") +
                       (Missing.Count > 0 ? " missing=" + string.Join(";", Missing.ToArray()) : ""));
        }

        /// <summary>The S1.N6 "global" IsUnderwater variant (installed by InstallCore; inert while UnderwaterMode is 0).</summary>
        public static bool EnsureUnderwaterPatch()
        {
            if (_underwaterInstalled) return true;
            if (_harmony == null) return false;
            _underwaterInstalled = Patch(typeof(Player), "IsUnderwater", Type.EmptyTypes, typeof(bool), false,
                                         null, "Postfix_Player_IsUnderwater", "Player.IsUnderwater()");
            return _underwaterInstalled;
        }

        private static bool Patch(Type type, string name, Type[] args, Type returnType, bool isStatic, string prefix, string postfix, string label)
        {
            try
            {
                MethodInfo m = AccessTools.Method(type, name, args);
                if (m == null)
                {
                    Missing.Add(label);
                    DeLog.Warn("DE.Harmony MISSING " + label + " (feature off)");
                    return false;
                }
                if (m.IsStatic != isStatic || m.ReturnType != returnType)
                {
                    Missing.Add(label);
                    DeLog.Warn("DE.Harmony MISSING " + label + " shape: static=" + m.IsStatic + " returns " + m.ReturnType.Name + " (feature off)");
                    return false;
                }
                HarmonyMethod pre = prefix != null ? new HarmonyMethod(typeof(S1Patches).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)) : null;
                HarmonyMethod post = postfix != null ? new HarmonyMethod(typeof(S1Patches).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)) : null;
                _harmony.Patch(m, pre, post);
                return true;
            }
            catch (Exception e)
            {
                Missing.Add(label);
                DeLog.Warn("DE.Harmony MISSING " + label + ": " + e.GetType().Name + ": " + e.Message + " (feature off)");
                return false;
            }
        }

        // ------------------------------------------------------------------------------------------ never save

        private static bool Prefix_IngameMenu_SaveGame()
        {
            try
            {
                if (DevMutants.IsOn(DevMutants.S1SaveGuardsOff))
                {
                    SavesAllowedByMutant++;
                    DeLog.Warn("DE.SaveBlocked NOT blocked: IngameMenu.SaveGame runs (MUTANT S1.M4, throwaway slot only)");
                    return true;
                }
                if (OneSaveWindow)
                {
                    SavesAllowedOnce++;
                    DeLog.Info("DE.SaveBlocked NOT blocked once: IngameMenu.SaveGame writes the host save the plugin just created (S9)");
                    return true;
                }
                SavesBlocked++;
                DeLog.Info("DE.SaveBlocked IngameMenu.SaveGame (#" + SavesBlocked + "): the host save is never saved");
            }
            catch (Exception)
            {
                // a patch body never throws into the game; the original is still skipped
            }
            return false;
        }

        private static bool Prefix_SaveToDeepStorageAsync(ref CoroutineTask<SaveLoadManager.SaveResult> __result)
        {
            try
            {
                if (DevMutants.IsOn(DevMutants.S1SaveGuardsOff))
                {
                    SavesAllowedByMutant++;
                    DeLog.Warn("DE.SaveBlocked NOT blocked: SaveLoadManager.SaveToDeepStorageAsync runs (MUTANT S1.M4, throwaway slot only)");
                    return true;
                }
                if (OneSaveWindow)
                {
                    OneSaveWindow = false;
                    SavesAllowedOnce++;
                    DeLog.Info("DE.SaveBlocked NOT blocked once: SaveLoadManager.SaveToDeepStorageAsync writes the host save the plugin created (S9); the window is closed");
                    return true;
                }
                SavesBlocked++;
                DeLog.Info("DE.SaveBlocked SaveLoadManager.SaveToDeepStorageAsync (#" + SavesBlocked +
                           "): nothing copied into the slot (second guard; answered 'success' so no modal dialog freezes Subnautica)");
                var r = new TaskResult<SaveLoadManager.SaveResult>();
                r.Set(new SaveLoadManager.SaveResult(true, SaveLoadManager.Error.None, "DeepEngineers: the host save is never saved", 0));
                __result = new CoroutineTask<SaveLoadManager.SaveResult>(Nothing(), r);
                return false;
            }
            catch (Exception e)
            {
                // Never save, even when the stand-in task cannot be built: the original stays skipped.
                DeLog.Warn("DE.SaveBlocked deep-storage guard could not build its stand-in result (" + e.GetType().Name + ": " + e.Message +
                           "); the original is still skipped");
                return false;
            }
        }

        private static IEnumerator Nothing()
        {
            yield break;
        }

        // ------------------------------------------------------------------------------------------ UI sinks

        private static void NoteText(string kind, string text)
        {
            if (_suppressedTextLines >= MaxSuppressedTextLines) return;
            _suppressedTextLines++;
            DeLog.Info("DE.Ui suppressed " + kind + (text != null ? ": " + Clip(text) : "") +
                       (_suppressedTextLines == MaxSuppressedTextLines ? " (further texts only counted)" : ""));
        }

        private static bool Prefix_ErrorMessage(string messageText)
        {
            if (!SuppressUi) return true;
            ErrorMessages++;
            try { NoteText("ErrorMessage", messageText); } catch (Exception) { }
            return false;
        }

        private static bool Prefix_Subtitles()
        {
            if (!SuppressUi) return true;
            SubtitleLines++;
            return false;
        }

        private static bool Prefix_SoundQueue_Play(string sound)
        {
            if (!SuppressUi) { PdaPassed++; return true; }
            PdaVoice++;
            try { NoteText("PDA voice", sound); } catch (Exception) { }
            return false;
        }

        private static bool Prefix_VoiceNotification_Play(ref bool __result)
        {
            if (!SuppressUi) { PdaPassed++; return true; }
            VoiceNotifications++;
            __result = false;
            return false;
        }

        private static bool Prefix_PDANotification_Play()
        {
            if (!SuppressUi) { PdaPassed++; return true; }
            PdaNotifications++;
            return false;
        }

        private static bool Prefix_Popup_Show()
        {
            if (!SuppressUi) return true;
            Popups++;
            return false;
        }

        public static string DescribeUi()
        {
            return "errorMessages=" + ErrorMessages + " subtitles=" + SubtitleLines + " pdaVoice=" + PdaVoice +
                   " voiceNotifications=" + VoiceNotifications + " pdaNotifications=" + PdaNotifications + " popups=" + Popups;
        }

        // ------------------------------------------------------------------------------------------ audio telemetry

        private static void CountAudio(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) path = "?";
                int n;
                AudioCounts.TryGetValue(path, out n);
                AudioCounts[path] = n + 1;
                _audioTotal++;
            }
            catch (Exception)
            {
            }
        }

        private static void Postfix_Emitter_Play(FMOD_CustomEmitter __instance)
        {
            try
            {
                if (__instance != null && __instance.playing)
                {
                    CountAudio(__instance.asset != null ? __instance.asset.path : null);
                    Action<FMOD_CustomEmitter> hook = EmitterPlayed;
                    if (hook != null) hook(__instance);
                }
            }
            catch (Exception)
            {
            }
        }

        private static void Postfix_OneShotAsset(FMODAsset asset)
        {
            try { CountAudio(asset != null ? asset.path : null); } catch (Exception) { }
        }

        private static void Postfix_OneShotPath(string eventPath)
        {
            CountAudio(eventPath);
        }

        /// <summary>The summary since the last call (top 12 paths), then the counts restart.</summary>
        public static string TakeAudioSummary()
        {
            if (_audioTotal == 0) return "events=0";
            var list = new List<KeyValuePair<string, int>>(AudioCounts);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new StringBuilder();
            sb.Append("events=").Append(_audioTotal.ToString(CultureInfo.InvariantCulture)).Append(" distinct=").Append(list.Count);
            for (int i = 0; i < list.Count && i < 12; i++)
                sb.Append(i == 0 ? " top=" : ",").Append(list[i].Key).Append('x').Append(list[i].Value.ToString(CultureInfo.InvariantCulture));
            AudioCounts.Clear();
            _audioTotal = 0;
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------------------ IsUnderwater variant

        private static void Postfix_Player_IsUnderwater(ref bool __result)
        {
            if (UnderwaterMode == 1) __result = UnderwaterCameraY < UnderwaterOceanLevel;
        }

        private static string Clip(string s)
        {
            if (s == null) return "";
            s = s.Replace('\n', ' ').Replace('\r', ' ');
            return s.Length <= 160 ? s : s.Substring(0, 160) + "...";
        }
    }
}
