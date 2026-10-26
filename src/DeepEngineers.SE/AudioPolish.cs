using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using DeepEngineers.Wire;
using HarmonyLib;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Data.Audio;
using VRage.Utils;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S7 audio polish, SE side (ARCHITECTURE §5.3 "Audio polish (S7)" A1–A6; CONTRACT §3.2, §5.4 "S7 conventions"
 /// <c>format audio.muffle</c>; ROADMAP S7.N11, S7.M19). The decisions are <see cref="AudioPolishRules"/>; this is the
 /// game glue:
 /// <list type="bullet">
 /// <item>A1: a Harmony postfix on <c>Sandbox.Game.Entities.MyEntity3DSoundEmitter.SelectEffect</c>
 /// returns the chosen
 /// effect (<c>LowPassNoHelmetNoOxy</c> by default) while C5 <c>seMixActive</c> is set and the camera is not in an
 /// airtight room, for emitters whose <c>LastSoundData</c>
 /// is null or modifiable (SE's own test). The postfix runs on whatever
 /// thread plays the sound: it reads volatile state and counts with <see cref="Interlocked"/> only.</item>
 /// <item>Inputs: <c>seMixActive</c> from the S2 audio decision of this update (<see cref="Tick"/>, called by S2Host
 /// right after <c>AudioController.Tick</c>); the camera-in-room bit from <see cref="CameraInRoomSource"/> when the
 /// interiors build pass links it, else from the C1 record of this tick (<see cref="ObserveFrame"/>, called by
 /// CharacterExport.FillBones: C1 flags bit 3 or the interior extension's <c>InRoom</c>).</item>
 /// <item>Rows <c>s7.audio</c> {tick, mixActive, inRoom, effect, emitters, muffled} (each change, 1 Hz under water;
 /// <c>emitters</c> = SelectEffect calls since the last row, <c>muffled</c> = those answered with the effect);
 /// C11 <c>de.s7audio {effect: name | off | default}</c> or <c>{action: status}</c> (<see cref="Command"/>; routed by
 /// the S7 dispatcher, or <see cref="Execute"/> chained directly).</item>
 /// </list>
 /// A missing target logs <c>DE.Harmony MISSING</c> once and leaves SE's own effect selection (feature off).
 /// <para>Review F2: the postfix forces nothing unless the world plays realistic sound
 /// and
 /// <c>MyFakes.ENABLE_NEW_SOUNDS</c>: in Arcade worlds (every DE Lab world) SE's effect path is
 /// off and a forced answer would only restart looping sounds. Logged once per world
 /// (<see cref="AudioPolishRules.ArcadeLine"/>); <c>s7.audio</c> rows carry <c>realisticSound</c> and S7.N11.muffle is
 /// INFO in an Arcade world.</para>
 /// </summary>
    internal static class AudioPolish
    {
        public const string Cmd = "de.s7audio";
        public const string Target = "MyEntity3DSoundEmitter.SelectEffect()";

        private static volatile bool _enabled = true;
        // S14 hook (CONTRACT §5.4 "S14 conventions", format effect.name): the default effect is S14's LowPassHelmet (1800 Hz), not
 // S7's LowPassNoHelmetNoOxy (100 Hz, the vacuum muffle); de.s7audio {effect} still picks any of SE's four
        private static volatile string _effectName = S14Se.DefaultEffect;
        private static MyStringHash _effect = MyStringHash.NullOrEmpty;
        private static volatile bool _effectReady;
        private static volatile bool _mixActive, _inRoom;
        // review F2: the world's sound mode (read every update; the postfix reads the session itself)
        private static volatile bool _realistic;
        private static uint _arcadeLoggedEpoch = uint.MaxValue;
        private static long _arcadeSkips;
        private static bool _applyTried;
        private static string _missing;
        private static long _calls, _modifiable, _muffled, _rowCalls, _rowModifiable, _rowMuffled, _rows;
        private static int _lastKey = int.MinValue;
        private static ulong _lastRowMs;

        // the C1 fallback for the room bit (ObserveFrame): the tick it was read at
        private static volatile bool _frameInRoom;
        private static long _frameTick = -1;

        /// <summary>
 /// The interiors build pass answer (<c>S7Interior</c>: the camera's cell is in an airtight room, C1 flags bit 3), when
 /// linked by the integrator; null = read C1 (<see cref="ObserveFrame"/>).
 /// </summary>
        public static Func<bool> CameraInRoomSource = null;

        public static bool Applied { get; private set; }
        public static bool MixActive { get { return _mixActive; } }
        public static bool CameraInRoom { get { return _inRoom; } }
        /// <summary>S14 hook: the effect in use and the polish switch (de.s7audio).</summary>
        public static string EffectName { get { return _effectName; } }
        public static bool Enabled { get { return _enabled; } }

        // ============================================================================================== patch

 /// <summary>Installs the A1 postfix once (idempotent; never throws).</summary>
        public static void EnsureApplied()
        {
            if (_applyTried) return;
            _applyTried = true;
            try
            {
                MethodInfo m = AccessTools.Method(typeof(MyEntity3DSoundEmitter), "SelectEffect", Type.EmptyTypes);
                if (m == null || m.IsStatic || m.ReturnType != typeof(MyStringHash) || m.DeclaringType != typeof(MyEntity3DSoundEmitter) || m.GetParameters().Length != 0)
                {
                    _missing = m == null ? "not found" : "shape changed";
                    DeLog.Info("DE.Harmony MISSING " + Target + " for S7 audio (" + _missing + "): SE keeps its own effects under water (feature off)");
                    return;
                }
                PropertyInfo last = typeof(MyEntity3DSoundEmitter).GetProperty("LastSoundData", BindingFlags.Instance | BindingFlags.Public);
                FieldInfo flag = typeof(MySoundData).GetField("ModifiableByHelmetFilters", BindingFlags.Instance | BindingFlags.Public);
                if (last == null || last.PropertyType != typeof(MySoundData) || flag == null || flag.FieldType != typeof(bool))
                {
                    _missing = "LastSoundData/ModifiableByHelmetFilters changed";
                    DeLog.Info("DE.Harmony MISSING " + Target + " for S7 audio (" + _missing + "): feature off");
                    return;
                }
                var harmony = new Harmony(HarmonyPatches.HarmonyId);
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(AudioPolish).GetMethod(nameof(SelectEffectPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                Applied = true;
                DeLog.Info("DE.Harmony S7 OK " + Target + " postfix (A1: " + _effectName + " under water while seMixActive and not in an airtight room)");
            }
            catch (Exception e)
            {
                _missing = e.GetType().Name;
                DeLog.Info("DE.Harmony MISSING " + Target + " S7 audio patch failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Never throw: SE plays sounds from several threads.
        private static void SelectEffectPostfix(MyEntity3DSoundEmitter __instance, ref MyStringHash __result)
        {
            try
            {
                Interlocked.Increment(ref _calls);
                // review F2: never force an effect outside realistic sound (SE's own SelectEffect answers NullOrEmpty there and
 // its Update would restart every looping modifiable sound on our answer)

 // have, so PlaySoundInternal applies this answer instead of only restarting the loop
                if (!RealisticSoundNow() && !S14Se.ArcadeEffectReady(__instance)) { Interlocked.Increment(ref _arcadeSkips); return; }
                MySoundData d = __instance != null ? __instance.LastSoundData : null;
                bool modifiable = d == null || d.ModifiableByHelmetFilters;
                if (!modifiable) return;
                Interlocked.Increment(ref _modifiable);
                if (!_effectReady) return;
                if (!AudioPolishRules.ShouldMuffle(_enabled, S14Se.MuffleOn(_mixActive), _inRoom, true, S7Mutants.M19, true)) return;   // S14 hook (ext s14 audio 5): the eye under the sea, not the view's mix
                __result = _effect;
                Interlocked.Increment(ref _muffled);
            }
            catch (Exception) { }
        }

        /// <summary>Review F2: <c>MySession.Static.Settings.RealisticSound</c> ∧ <c>MyFakes.ENABLE_NEW_SOUNDS</c> (SE's own gate for effects).</summary>
        private static bool RealisticSoundNow()
        {
            MySession s = MySession.Static;
            return s != null && s.Settings != null && s.Settings.RealisticSound && MyFakes.ENABLE_NEW_SOUNDS;
        }

        // ============================================================================================== per update

 /// <summary>
 /// S2Host.Tick, right after the audio decision (update thread): this update's <c>seMixActive</c>, the room bit, the
 /// patch on first use, and the <c>s7.audio</c> row when due.
 /// </summary>
        public static void Tick(ulong nowMs, ref AudioDecision d)
        {
            try
            {
                if (!_applyTried) EnsureApplied();
                if (!_effectReady || _effect.String != _effectName)
                {
                    _effect = MyStringHash.GetOrCompute(_effectName);
                    _effectReady = true;
                }
                _mixActive = d.MixActive;
                _inRoom = ReadInRoom();
                _realistic = RealisticSoundNow();
                if (!_realistic && MySession.Static != null && _arcadeLoggedEpoch != Phases.WorldEpoch)
                {
                    _arcadeLoggedEpoch = Phases.WorldEpoch;
                    DeLog.Info(AudioPolishRules.ArcadeLine + " (RealisticSound=false or ENABLE_NEW_SOUNDS off; the A1 postfix forces nothing)");
                }
                S14Se.Tick(nowMs, _mixActive, _inRoom, _enabled && !S7Mutants.M19, _realistic); // S14 hook: jetpack, Arcade route, GUI, reverb, SE thrusters, rows (never throws)
                int key = AudioPolishRules.StateKey(_enabled, _mixActive, _inRoom, S7Mutants.M19, _effectName, _realistic);
                bool changed = key != _lastKey;
                if (AudioPolishRules.RowDue(changed, _mixActive, nowMs, _lastRowMs))
                {
                    _lastKey = key;
                    _lastRowMs = nowMs;
                    Row(changed ? "change" : "periodic");
                }
            }
            catch (Exception e)
            {
                if (!_tickErrorLogged) { _tickErrorLogged = true; DeLog.Info("DE.Error S7 audio tick " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private static bool _tickErrorLogged;

        private static bool ReadInRoom()
        {
            Func<bool> src = CameraInRoomSource;
            if (src != null)
            {
                try { return src(); }
                catch (Exception) { return false; }
            }
            // the C1 fallback: valid for the tick it was read at and the next one (no character: no C1 bones path, then false)
            long t = Interlocked.Read(ref _frameTick);
            return t >= 0 && (long)FrameWriter.LastTick - t <= 1 && _frameInRoom;
        }

        /// <summary>
 /// CharacterExport.FillBones (the C1 writer, every tick with a character): the camera-in-room bit of this C1 record
 /// (C1 flags bit 3, or the interior extension's <c>InRoom</c> when version 1). Allocation-free; never throws.
 /// </summary>
        public static void ObserveFrame(ref SeFrame f)
        {
            try
            {
                C1Interior ext;
                bool inRoom = (f.flags & SeFrameFlags.CameraInAirtightRoom) != 0 ||
                              (S7InteriorWire.TryRead(ref f, out ext) && (ext.Flags & S7Conventions.C1InteriorFlags.InRoom) != 0);
                _frameInRoom = inRoom;
                Interlocked.Exchange(ref _frameTick, (long)f.tick);
            }
            catch (Exception) { }
        }

        // ============================================================================================== rows and commands

        private static void Row(string why)
        {
            long calls = Interlocked.Read(ref _calls), modifiable = Interlocked.Read(ref _modifiable), muffled = Interlocked.Read(ref _muffled);
            var sb = new StringBuilder(320);
            sb.Append("{\"t\":");
            JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"ev\":\"s7.audio\",\"why\":");
            JsonText.AppendString(sb, why);
            sb.Append(",\"tick\":").Append(FrameWriter.LastTick.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"mixActive\":").Append(_mixActive ? "true" : "false");
            sb.Append(",\"inRoom\":").Append(_inRoom ? "true" : "false");
            sb.Append(",\"inRoomSource\":");
            JsonText.AppendString(sb, CameraInRoomSource != null ? "interior" : "c1");
            sb.Append(",\"effect\":");
            JsonText.AppendString(sb, _enabled ? _effectName : "off");
            sb.Append(",\"patched\":").Append(Applied ? "true" : "false");
            sb.Append(",\"emitters\":").Append((calls - _rowCalls).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"modifiable\":").Append((modifiable - _rowModifiable).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"muffled\":").Append((muffled - _rowMuffled).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"emittersTotal\":").Append(calls.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"muffledTotal\":").Append(muffled.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"expectMuffle\":").Append(AudioPolishRules.ShouldMuffle(_enabled, _mixActive, _inRoom, true, false, _realistic) ? "true" : "false");
            sb.Append(",\"realisticSound\":").Append(_realistic ? "true" : "false");
            sb.Append(",\"arcadeSkips\":").Append(Interlocked.Read(ref _arcadeSkips).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"mutantS7M19\":").Append(S7Mutants.M19 ? "true" : "false");
            sb.Append('}');
            _rowCalls = calls;
            _rowModifiable = modifiable;
            _rowMuffled = muffled;
            _rows++;
            Jsonl.WriteLine(sb.ToString());
        }

        public static Dictionary<string, object> Status()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "patched", Applied }, { "missing", _missing }, { "enabled", _enabled }, { "effect", _effectName },
                { "effects", string.Join(",", AudioPolishRules.Effects) }, { "seMixActive", _mixActive }, { "inRoom", _inRoom },
                { "inRoomSource", CameraInRoomSource != null ? "interior" : "c1" }, { "selectEffectCalls", Interlocked.Read(ref _calls) },
                { "modifiable", Interlocked.Read(ref _modifiable) }, { "muffled", Interlocked.Read(ref _muffled) }, { "rows", _rows }, { "mutantS7M19", S7Mutants.M19 },
                { "realisticSound", _realistic }, { "arcadeSkips", Interlocked.Read(ref _arcadeSkips) },
            };
        }

        /// <summary>A dispatcher entry: <c>de.s7audio</c> only (null for any other command).</summary>
        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            return cmd == Cmd ? Command(args) : S14Se.Execute(cmd, args, via); // S14 hook: de.s14 (null for other verbs)
        }

        /// <summary><c>de.s7audio {effect: LowPassNoHelmetNoOxy | LowPassHelmet | LowPassCockpit | LowPassCockpitNoOxy | default | off}</c> or <c>{action: status}</c>.</summary>
        public static Dictionary<string, object> Command(IDictionary<string, object> args)
        {
            Dictionary<string, object> d;
            object v;
            if (args != null && args.TryGetValue("effect", out v))
            {
                string effect;
                bool off;
                if (!AudioPolishRules.TryParseEffect(v as string, out effect, out off))
                {
                    d = Status();
                    d["ok"] = false;
                    d["error"] = "de.s7audio: effect " + string.Join(" | ", AudioPolishRules.Effects) + " | default | off";
                    d["line"] = d["error"];
                    return d;
                }
                _enabled = !off;
                if (effect != null) _effectName = effect;
                _effectReady = false;     // re-hashed on the next tick
                if (!_applyTried) EnsureApplied();
                DeLog.Info("DE.S7 audio effect=" + (_enabled ? _effectName : "off") + " (de.s7audio)");
                _lastKey = int.MinValue;  // a row on the next tick
            }
            d = Status();
            d["ok"] = true;
            d["line"] = AudioPolishRules.Line(Applied, _enabled, _effectName, _mixActive, _inRoom, Interlocked.Read(ref _calls), Interlocked.Read(ref _muffled), _missing);
            return d;
        }
    }
}
