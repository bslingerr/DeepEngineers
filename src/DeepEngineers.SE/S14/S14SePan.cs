using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using DeepEngineers.Wire;
using HarmonyLib;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using VRage.Audio;
using VRageMath;
using X3D = SharpDX.X3DAudio;
using XA = SharpDX.XAudio2;

namespace DeepEngineers.SE
{
    /// <summary>
 /// 4
 /// "S14 conventions", formats pan.stereo, pan.row, effect.chain). Every game member VERIFIED
 /// <list type="bullet">
 /// <item><b>The left ear.</b> SE's listener is the camera
 /// which
 /// X3DAudio's left-handed frame turns into the right ear on the camera's right: correct. Its 3D path for one voice,
 /// <c>MyXAudio2.Apply3D</c>, builds <c>new DspSettings(srcChannels, dstChannels)</c> from the
 /// emitter's <c>SourceChannels</c> (= the voice's buffer channels,; MySourceVoice.GetOutputChannels) but keeps the
 /// helper emitter at <c>ChannelCount = 1</c>, so a 2-channel
 /// voice is calculated outside X3DAudio's contract (SrcChannelCount must equal the emitter's ChannelCount; SharpDX passes
 /// both as given, SharpDX.XAudio2 X3DAudio.Calculate), then SE swaps coefficients [1] and [2] and writes the
 /// matrix. Which ear that lands in depends on X3DAudio's internal layout;
 /// a 2-channel wave plays in 3D wherever a cue has no 3D wave
 /// and a ship's sounds play 3D in third person (MyShipSoundComponent.UpdateShouldPlay2D:
 /// 2D only in a small ship's first-person cockpit or inside a large ship). The fix (<c>pan.stereo</c>): a postfix on
 /// <c>Apply3D</c> rebuilds such a voice's matrix from one valid 1-channel X3DAudio calculation with SE's own listener and
 /// emitter and SE's own distance factor; 1-channel voices keep SE's matrix. The measurement (<c>pan.row</c>,
 /// row <c>s14.pan</c>): every sampled SE voice's own output matrix (SharpDX <c>Voice.GetOutputMatrix</c>) against its
 /// direction from the camera, SE's listener read back (<c>m_listener</c>), the device channels (<c>m_inputChannelCount</c>).

 /// the grid itself for a large grid
 /// i.e. from the grid's AABB centre, and 3D
 /// unless the character is enclosed (<c>m_insideShip</c>), while the third-person camera looks at the
 /// cockpit's head. In the 10-18 save (401 of the session's 483 blocks) the grid's centre sits 3 large cells (7.5 m) to the
 /// cockpit's LEFT: SE's own geometry puts that ship's whole sound on the left in third person, in Arcade and Realistic alike.
 /// <c>pan.own</c>: the ship-level sounds of the grid whose ship controller the player controls play SE's own 2D path (the
 /// small-ship first-person cockpit rule) in both views: a prefix and postfix on the private
 /// <c>MyShipSoundComponent.UpdateShouldPlay2D</c> set <c>m_shouldPlay2D</c> and its changed flag
 /// so SE's own <c>UpdateSoundDimension</c> restarts the loops in 2D; every other ship keeps SE's 3D.</item>
 /// <item><b>The Realistic chain.</b> In a Realistic world SE plays a pair's <c>Real*</c> recording wherever
 /// <c>SelectCue</c> finds no air (or a small pressurized
 /// cockpit), installed for every emitter in a Realistic world and called for every loop each update; those
 /// recordings are made to sound heard through a suit, so S14's effect on top stacks two low-passes. Under water a postfix
 /// answers the pair's Arcade recording (and <c>m_realistic</c> false: RealisticVolumeChange 1, positional 3D) where SE
 /// picked the Real* one: both sound modes play the Arcade recording plus the one effect. Counted per modifiable play under
 /// water in <c>s14.muffle</c> (chainZero, chainOne, chainTwo).</item>
 /// </list>
 /// Never throws; a missing member logs <c>DE.S14 MISSING</c> once and switches off only its feature.
 /// </summary>
    internal static partial class S14Se
    {
        /// <summary>de.s14 pan on|off (pan.stereo and pan.own), cue on|off (effect.chain's Arcade recording under water).</summary>
        public static bool PanOn = true, CueOn = true;

        private static bool _panTried, _panPatched, _cuePatched, _ownPatched;
        private static AccessTools.FieldRef<MyShipSoundComponent, bool> _shipPlay2D, _shipPlay2DChanged;
        private static long _ownForced;
        private static AccessTools.FieldRef<object, X3D.X3DAudio> _x3d;
        private static AccessTools.FieldRef<object, X3D.Listener> _seListener;
        private static AccessTools.FieldRef<object, int> _deviceChannels;
        private static AccessTools.FieldRef<object, XA.SourceVoice> _voiceOf;
        private static AccessTools.FieldRef<MyEntity3DSoundEmitter, bool> _emitterRealistic;

        // pan.stereo counters (Apply3D runs on the update thread; Interlocked for the odd caller elsewhere)
        private static long _stereo3D, _panFixed, _panMismatch, _panRawOneSided, _panErrors;
        private static long _rowStereo3D, _rowPanFixed, _rowPanMismatch, _rowPanRawOneSided;

        private sealed class PanSample
        {
            public float[] Raw;     // SE's matrix as Apply3D left it (before pan.stereo)
            public int Channels, SeChannels, Dst;
            public bool Fixed;
        }
        private static readonly ConditionalWeakTable<object, PanSample> PanSamples = new ConditionalWeakTable<object, PanSample>();
        private static readonly ConditionalWeakTable<object, PanSample>.CreateValueCallback NewPan = k => new PanSample();

        // effect.chain counters (PlaySoundInternal postfix and SelectCue postfix, update thread)
        private static long _chainPlays, _chain0, _chain1, _chain2, _recordings, _cueSwaps, _cueForced;
        private static long _rowChainPlays, _rowChain0, _rowChain1, _rowChain2, _rowRecordings, _rowCueSwaps, _rowCueForced;

        // the s14.pan row
        private static readonly Dictionary<long, MyShipSoundComponent> ShipComps = new Dictionary<long, MyShipSoundComponent>();
        private static readonly List<long> ShipCompGone = new List<long>();
        private static ulong _nextPanRowMs;
        private static int _panKey = int.MinValue;

        // ============================================================================================== members and patches

 /// <summary>EnsureApplied (round 4): the Apply3D postfix (pan.stereo), the SelectCue postfix (effect.chain), the UpdateShouldPlay2D prefix and postfix (pan.own).</summary>
        private static void Round4Members(Harmony harmony)
        {
            if (_panTried) return;
            _panTried = true;
            try
            {
                Type xa = AccessTools.TypeByName("VRage.Audio.MyXAudio2");
                Type sv = AccessTools.TypeByName("VRage.Audio.MySourceVoice");
                MethodInfo m = xa != null ? AccessTools.Method(xa, "Apply3D") : null;
                ParameterInfo[] ps = m != null ? m.GetParameters() : null;
                if (xa == null || sv == null || m == null || m.ReturnType != typeof(float) || ps == null || ps.Length != 11 || ps[0].ParameterType != sv ||
                    ps[1].ParameterType != typeof(X3D.Listener) || ps[2].ParameterType != typeof(X3D.Emitter) || ps[5].ParameterType != typeof(X3D.CalculateFlags))
                    Miss("VRage.Audio.MyXAudio2.Apply3D(MySourceVoice, Listener, Emitter, int, int, CalculateFlags, float, float, bool, bool, bool)", "pan.stereo (the 2-channel 3D pan)");
                else
                {
                    _x3d = AccessTools.FieldRefAccess<X3D.X3DAudio>(xa, "m_x3dAudio");
                    _seListener = AccessTools.FieldRefAccess<X3D.Listener>(xa, "m_listener");
                    _deviceChannels = AccessTools.FieldRefAccess<int>(xa, "m_inputChannelCount");
                    _voiceOf = AccessTools.FieldRefAccess<XA.SourceVoice>(sv, "m_voice");
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(S14Se).GetMethod(nameof(Apply3DPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                    _panPatched = true;
                }
            }
            catch (Exception e) { Miss("MyXAudio2.Apply3D patch / m_x3dAudio / m_listener / m_inputChannelCount / MySourceVoice.m_voice (" + e.GetType().Name + ": " + e.Message + ")", "pan.stereo and the s14.pan matrices"); }
            try
            {
                MethodInfo c = AccessTools.Method(typeof(MyEntity3DSoundEmitter), "SelectCue", new[] { typeof(MySoundPair) });
                if (c == null || c.IsStatic || c.ReturnType != typeof(MyCueId)) Miss("MyEntity3DSoundEmitter.SelectCue(MySoundPair)", "effect.chain (the Arcade recording under water in a Realistic world)");
                else
                {
                    _emitterRealistic = AccessTools.FieldRefAccess<MyEntity3DSoundEmitter, bool>("m_realistic");
                    harmony.Patch(c, postfix: new HarmonyMethod(typeof(S14Se).GetMethod(nameof(SelectCuePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                    _cuePatched = true;
                }
            }
            catch (Exception e) { Miss("MyEntity3DSoundEmitter.SelectCue patch / m_realistic (" + e.GetType().Name + ": " + e.Message + ")", "effect.chain"); }
            try
            {
                MethodInfo u = AccessTools.Method(typeof(MyShipSoundComponent), "UpdateShouldPlay2D", Type.EmptyTypes);
                if (u == null || u.IsStatic || u.ReturnType != typeof(void)) Miss("MyShipSoundComponent.UpdateShouldPlay2D()", "pan.own (the own ship's sounds in 2D)");
                else
                {
                    _shipPlay2D = AccessTools.FieldRefAccess<MyShipSoundComponent, bool>("m_shouldPlay2D");
                    _shipPlay2DChanged = AccessTools.FieldRefAccess<MyShipSoundComponent, bool>("m_shouldPlay2DChanged");
                    harmony.Patch(u, prefix: new HarmonyMethod(typeof(S14Se).GetMethod(nameof(ShipPlay2DPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                                  postfix: new HarmonyMethod(typeof(S14Se).GetMethod(nameof(ShipPlay2DPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                    _ownPatched = true;
                }
            }
            catch (Exception e) { Miss("MyShipSoundComponent.UpdateShouldPlay2D patch / m_shouldPlay2D / m_shouldPlay2DChanged (" + e.GetType().Name + ": " + e.Message + ")", "pan.own"); }
            DeLog.Info("DE.Harmony S14 round 4 pan=" + (_panPatched ? "MyXAudio2.Apply3D postfix" : "MISSING") + " own=" + (_ownPatched ? "MyShipSoundComponent.UpdateShouldPlay2D prefix+postfix" : "MISSING") +
                       " cue=" + (_cuePatched ? "MyEntity3DSoundEmitter.SelectCue postfix" : "MISSING"));
        }

        /// <summary>
 /// pan.stereo (inside SE's 3D update of one voice, after SE wrote its matrix): a voice with 2 channels, or not the count SE
 /// used, gets the matrix of one valid 1-channel X3DAudio calculation (SE's listener and emitter, SE's distance factor).
 /// Never throws.
 /// </summary>
        private static void Apply3DPostfix(object __instance, object managedVoice, X3D.Listener listener, X3D.Emitter emitter, int srcChannels, int dstChannels,
                                           X3D.CalculateFlags flags, float maxDistance, bool silent, bool use3DCalculation)
        {
            try
            {
                if (!_enabled || !use3DCalculation || managedVoice == null || listener == null || emitter == null || _voiceOf == null || dstChannels < 1 || dstChannels > 8) return;
                XA.SourceVoice v = _voiceOf(managedVoice);
                if (v == null) return;
                int real = v.VoiceDetails.InputChannelCount;
                if (real < 1 || real > 2) return;
                if (real == 1 && srcChannels == 1) return; // SE's mono call follows X3DAudio's contract: SE's matrix stays
                Interlocked.Increment(ref _stereo3D);
                if (real != srcChannels) Interlocked.Increment(ref _panMismatch);
                PanSample ps = PanSamples.GetValue(managedVoice, NewPan);
                if (ps.Raw == null || ps.Raw.Length != real * dstChannels) ps.Raw = new float[real * dstChannels];
                ps.Channels = real;
                ps.SeChannels = srcChannels;
                ps.Dst = dstChannels;
                ps.Fixed = false;
                try { v.GetOutputMatrix(null, real, dstChannels, ps.Raw); } catch (Exception) { Array.Clear(ps.Raw, 0, ps.Raw.Length); }
                double rl, rr;
                S14Rules.MatrixLR(ps.Raw, real, dstChannels, out rl, out rr);
                if (S14Rules.OneSided(rl, rr)) Interlocked.Increment(ref _panRawOneSided);
                if (!PanOn || S14SeMutants.PanRaw || _x3d == null || __instance == null) return;
                X3D.X3DAudio x = _x3d(__instance);
                if (x == null) return;
                var dsp = new X3D.DspSettings(1, dstChannels);
                x.Calculate(listener, emitter, flags & (X3D.CalculateFlags.Matrix | X3D.CalculateFlags.RedirectToLfe), dsp);
                double k = S14Rules.DistanceFactor(dsp.EmitterToListenerDistance, maxDistance, silent, emitter.InnerRadius == 0f);
                float[] m = S14Rules.PanMatrix(dsp.MatrixCoefficients, real, k);
                if (m == null) return;
                v.SetOutputMatrix(null, real, dstChannels, m);
                ps.Fixed = true;
                Interlocked.Increment(ref _panFixed);
            }
            catch (Exception) { Interlocked.Increment(ref _panErrors); }
        }

        /// <summary>pan.own: SE's value before its own decision (update thread).</summary>
        private static void ShipPlay2DPrefix(MyShipSoundComponent __instance, out bool __state)
        {
            __state = false;
            try { if (_shipPlay2D != null && __instance != null) __state = _shipPlay2D(__instance); } catch (Exception) { }
        }

        /// <summary>
 /// pan.own (update thread, after SE decided 2D or 3D for a ship): the grid the player controls plays its ship sounds in
 /// 2D; the changed flag is SE's own comparison against the value before (so SE restarts its loops once). Never throws.
 /// </summary>
        private static void ShipPlay2DPostfix(MyShipSoundComponent __instance, bool __state)
        {
            try
            {
                if (!_enabled || !PanOn || S14SeMutants.PanRaw || __instance == null || _shipPlay2D == null || _shipPlay2DChanged == null || _shipGrid == null) return;
                MyCubeGrid g = _shipGrid(__instance);
                if (g == null || !IsOwnShip(g) || _shipPlay2D(__instance)) return;
                _shipPlay2D(__instance) = true;
                _shipPlay2DChanged(__instance) = !__state;
                Interlocked.Increment(ref _ownForced);
            }
            catch (Exception) { }
        }

        /// <summary>pan.own: the grid of the ship controller (cockpit, remote control) the player controls.</summary>
        private static bool IsOwnShip(MyCubeGrid g)
        {
            MySession s = MySession.Static;
            MyShipController c = s != null && s.ControlledEntity != null ? s.ControlledEntity.Entity as MyShipController : null;
            return c != null && ReferenceEquals(c.CubeGrid, g);
        }

        /// <summary>
 /// effect.chain (update thread, SE's own cue choice of an emitter): under water, where SE picked a pair's Real* recording,
 /// the Arcade one; S14.M9 the other way round (the stacked chain). Never throws.
 /// </summary>
        private static void SelectCuePostfix(MyEntity3DSoundEmitter __instance, MySoundPair sound, ref MyCueId __result)
        {
            try
            {
                if (!_enabled || __instance == null || sound == null || !_muffleWanted) return;
                MyCueId arc = sound.Arcade, real = sound.Realistic;
                if (S14SeMutants.RealisticRecording)
                {
                    if (real.IsNull || real == arc || __result == real) return;
                    __result = real;
                    if (_emitterRealistic != null) _emitterRealistic(__instance) = true;
                    Interlocked.Increment(ref _cueForced);
                    return;
                }
                if (!CueOn || arc.IsNull || __result == arc || __result != real) return;
                __result = arc;
                if (_emitterRealistic != null) _emitterRealistic(__instance) = false;
                Interlocked.Increment(ref _cueSwaps);
            }
            catch (Exception) { }
        }

        /// <summary>effect.chain: a modifiable SE sound that started under water (PlaySoundInternal postfix): the effect and the recording.</summary>
        private static void ChainCount(MyEntity3DSoundEmitter e, bool applied)
        {
            bool rec = false;
            try
            {
                MySoundPair sp = e.SoundPair;
                if (sp != null)
                {
                    MyCueId real = sp.Realistic, arc = sp.Arcade;
                    rec = !real.IsNull && real != arc && e.SoundId == real;
                }
            }
            catch (Exception) { }
            Interlocked.Increment(ref _chainPlays);
            int n = S14Rules.ChainLength(applied, rec);
            if (n <= 0) Interlocked.Increment(ref _chain0);
            else if (n == 1) Interlocked.Increment(ref _chain1);
            else Interlocked.Increment(ref _chain2);
            if (rec) Interlocked.Increment(ref _recordings);
        }

        /// <summary>The s14.muffle row's chain fields (since the last row).</summary>
        private static void ChainFields(Dictionary<string, object> d)
        {
            long p = Interlocked.Read(ref _chainPlays), c0 = Interlocked.Read(ref _chain0), c1 = Interlocked.Read(ref _chain1), c2 = Interlocked.Read(ref _chain2);
            long rec = Interlocked.Read(ref _recordings), sw = Interlocked.Read(ref _cueSwaps), fo = Interlocked.Read(ref _cueForced);
            d["mode"] = _realistic ? "realistic" : "arcade";
            d["chainPlays"] = p - _rowChainPlays;
            d["chainZero"] = c0 - _rowChain0;
            d["chainOne"] = c1 - _rowChain1;
            d["chainTwo"] = c2 - _rowChain2;
            d["recordings"] = rec - _rowRecordings;
            d["cueSwaps"] = sw - _rowCueSwaps;
            d["cueForced"] = fo - _rowCueForced;
            d["cuePatched"] = _cuePatched;
            d["cueOn"] = CueOn;
            d["mutantS14M9"] = S14SeMutants.RealisticRecording;
            _rowChainPlays = p; _rowChain0 = c0; _rowChain1 = c1; _rowChain2 = c2; _rowRecordings = rec; _rowCueSwaps = sw; _rowCueForced = fo;
        }

        /// <summary>ShipThrusterPostfix (round 4): the ship sound components seen, for the s14.pan voices.</summary>
        private static void SeenShip(MyShipSoundComponent c, MyCubeGrid g)
        {
            if (c == null || g == null) return;
            ShipComps[g.EntityId] = c;
        }

        // ============================================================================================== the s14.pan row

        private struct PanVoice
        {
            public string Src;
            public MyEntity3DSoundEmitter E;
        }
        private static readonly List<PanVoice> PanVoices = new List<PanVoice>(16);

        /// <summary>
 /// pan.row at 1 Hz while an SE voice of a thruster, a ship or the character plays: each one's output matrix against its
 /// direction from the camera, SE's listener against the camera, and pan.stereo's counters. Update thread; never throws.
 /// </summary>
        private static void PanRow(ulong nowMs)
        {
            if (nowMs < _nextPanRowMs) return;
            _nextPanRowMs = nowMs + (ulong)S14Conventions.Rows.PeriodMs;
            MySession s = MySession.Static;
            if (s == null || MySector.MainCamera == null) return;
            PanVoices.Clear();
            // the ship-level sounds (MyShipSoundComponent.m_emitters), the character's own, the unsilenced thruster blocks
            ShipCompGone.Clear();
            int own2D = 0, own3D = 0;
            long ownGrid = 0;
            foreach (KeyValuePair<long, MyShipSoundComponent> kv in ShipComps)
            {
                MyShipSoundComponent c = kv.Value;
                MyCubeGrid g = c != null && _shipGrid != null ? _shipGrid(c) : null;
                if (g == null || g.Closed || g.MarkedForClose) { ShipCompGone.Add(kv.Key); continue; }
                MyEntity3DSoundEmitter[] em = _shipEmitters != null ? _shipEmitters(c) : null;
                if (em == null) continue;
                bool own = IsOwnShip(g);
                if (own) ownGrid = g.EntityId;
                for (int i = 0; i < em.Length; i++)
                {
                    if (em[i] == null || !em[i].IsPlaying) continue;
                    if (own) { if (em[i].Plays2D) own2D++; else own3D++; }
                    if (PanVoices.Count < 8) PanVoices.Add(new PanVoice { Src = (own ? "own ship[" : "ship[") + i + "]", E = em[i] });
                }
            }
            foreach (long k in ShipCompGone) ShipComps.Remove(k);
            MyCharacter me = s.LocalCharacter;
            if (me != null && !me.Closed && _soundEmitters != null)
            {
                MyCharacterSoundComponent sc = me.SoundComp;
                List<MyEntity3DSoundEmitter> ce = sc != null ? _soundEmitters(sc) : null;
                if (ce != null)
                    for (int i = 0; i < ce.Count && PanVoices.Count < 10; i++)
                        if (ce[i] != null && ce[i].IsPlaying) PanVoices.Add(new PanVoice { Src = "character[" + i + "]", E = ce[i] });
            }
            foreach (ThrusterState st in Thrusters.Values)
            {
                if (PanVoices.Count >= 12) break;
                MyThrust t = st.Block;
                MyEntity3DSoundEmitter e = EmitterOf(t);
                if (e != null && e.IsPlaying && t != null && !t.IsSilenced) PanVoices.Add(new PanVoice { Src = "block:" + st.Cue, E = e });
            }
            bool first = false;
            try { first = s.CameraController != null && s.CameraController.IsInFirstPersonView; } catch (Exception) { }
            int key = (PanVoices.Count > 0 ? 1 : 0) | (first ? 2 : 0) | (_mixActive ? 4 : 0) | (ownGrid != 0 ? 8 : 0);
            bool changed = key != _panKey;
            _panKey = key;
            if (PanVoices.Count == 0 && !changed) return;
            MatrixD cam = MySector.MainCamera.WorldMatrix;
            Vector3D right = cam.Right;
            object audio = MyAudio.Static;
            int dst = 2;
            try { if (_deviceChannels != null && audio != null) dst = Math.Max(1, Math.Min(8, _deviceChannels(audio))); } catch (Exception) { }
            var list = new List<object>(PanVoices.Count);
            int d3 = 0, judged = 0, oneSidedCentred = 0, signFlips = 0, rawOneSidedCentred = 0;
            foreach (PanVoice pv in PanVoices)
            {
                var o = new Dictionary<string, object>(StringComparer.Ordinal);
                o["src"] = pv.Src;
                MyEntity3DSoundEmitter e = pv.E;
                IMySourceVoice sv = e.Sound;
                bool is3D = !e.Plays2D;
                o["d3"] = is3D;
                o["cue"] = sv != null ? sv.CueEnum.ToString() : "-";
                bool realistic = false;
                try { realistic = ((IMy3DSoundEmitter)e).Realistic; } catch (Exception) { }
                o["realistic"] = realistic;
                o["seCh"] = e.SourceChannels;
                Vector3D rel = e.SourcePosition;
                double dist = rel.Length();
                double geo = dist > 1e-3 ? Vector3D.Dot(rel / dist, right) : double.NaN;
                o["dist"] = Math.Round(dist, 2);
                o["geo"] = double.IsNaN(geo) ? null : (object)Math.Round(geo, 3);
                int ch = -1;
                double l = 0, r = 0;
                try
                {
                    XA.SourceVoice v = _voiceOf != null && sv != null ? _voiceOf(sv) : null;
                    if (v != null)
                    {
                        ch = v.VoiceDetails.InputChannelCount;
                        if (ch >= 1 && ch <= 8)
                        {
                            var m = new float[ch * dst];
                            v.GetOutputMatrix(null, ch, dst, m);
                            S14Rules.MatrixLR(m, ch, dst, out l, out r);
                        }
                    }
                }
                catch (Exception) { }
                double pan = S14Rules.MatrixPan(l, r);
                bool oneSided = S14Rules.OneSided(l, r);
                o["ch"] = ch;
                o["L"] = Math.Round(l, 4);
                o["R"] = Math.Round(r, 4);
                o["pan"] = double.IsNaN(pan) ? null : (object)Math.Round(pan, 3);
                o["oneSided"] = oneSided;
                PanSample ps;
                if (sv != null && PanSamples.TryGetValue(sv, out ps) && ps.Raw != null)
                {
                    double rl, rr;
                    S14Rules.MatrixLR(ps.Raw, ps.Channels, ps.Dst, out rl, out rr);
                    o["rawL"] = Math.Round(rl, 4);
                    o["rawR"] = Math.Round(rr, 4);
                    o["fixed"] = ps.Fixed;
                    if (is3D && S14Rules.PanCentred(geo) && S14Rules.OneSided(rl, rr)) rawOneSidedCentred++;
                }
                if (is3D && sv != null && sv.IsPlaying && (l + r) > 1e-4)
                {
                    d3++;
                    if (!double.IsNaN(geo))
                    {
                        judged++;
                        if (S14Rules.PanCentred(geo) && oneSided) oneSidedCentred++;
                        if (!S14Rules.PanSignAgrees(geo, pan)) signFlips++;
                    }
                }
                list.Add(o);
            }
            var d = Row("s14.pan", changed ? "change" : "periodic");
            d["firstPerson"] = first;
            d["mixActive"] = _mixActive;
            d["realisticSound"] = _realistic;
            d["dst"] = dst;
            d["voices"] = PanVoices.Count;
            d["voices3D"] = d3;
            d["judged"] = judged;
            d["oneSidedCentred"] = oneSidedCentred;
            d["signFlips"] = signFlips;
            d["rawOneSidedCentred"] = rawOneSidedCentred;
            d["ownShip"] = ownGrid;
            d["ownShip2D"] = own2D;
            d["ownShip3D"] = own3D;
            d["ownForced"] = Interlocked.Read(ref _ownForced);
            d["ownPatched"] = _ownPatched;
            long st3 = Interlocked.Read(ref _stereo3D), fx = Interlocked.Read(ref _panFixed), mm = Interlocked.Read(ref _panMismatch), ro = Interlocked.Read(ref _panRawOneSided);
            d["stereo3D"] = st3 - _rowStereo3D;
            d["fixed"] = fx - _rowPanFixed;
            d["mismatch"] = mm - _rowPanMismatch;
            d["rawOneSided"] = ro - _rowPanRawOneSided;
            d["errors"] = Interlocked.Read(ref _panErrors);
            _rowStereo3D = st3; _rowPanFixed = fx; _rowPanMismatch = mm; _rowPanRawOneSided = ro;
            ListenerFields(d, cam, audio);
            d["patched"] = _panPatched;
            d["on"] = PanOn;
            d["mutantS14M8"] = S14SeMutants.PanRaw;
            d["list"] = list;
            Write(d);
        }

        /// <summary>SE's X3DAudio listener read back against the camera (front = -forward, top = up, position 0).</summary>
        private static void ListenerFields(Dictionary<string, object> d, MatrixD cam, object audio)
        {
            try
            {
                X3D.Listener li = _seListener != null && audio != null ? _seListener(audio) : null;
                if (li == null) { d["listener"] = "unread"; return; }
                var front = new Vector3D(li.OrientFront.X, li.OrientFront.Y, li.OrientFront.Z);
                var top = new Vector3D(li.OrientTop.X, li.OrientTop.Y, li.OrientTop.Z);
                var pos = new Vector3D(li.Position.X, li.Position.Y, li.Position.Z);
                d["listenerFrontDeg"] = Math.Round(AngleDeg(front, -cam.Forward), 2);
                d["listenerTopDeg"] = Math.Round(AngleDeg(top, cam.Up), 2);
                d["listenerPosM"] = Math.Round(pos.Length(), 3);
                d["listener"] = "read";
            }
            catch (Exception) { d["listener"] = "error"; }
        }

        private static double AngleDeg(Vector3D a, Vector3D b)
        {
            double la = a.Length(), lb = b.Length();
            if (!(la > 1e-9) || !(lb > 1e-9)) return 180.0;
            double c = Vector3D.Dot(a, b) / (la * lb);
            if (c > 1) c = 1;
            if (c < -1) c = -1;
            return Math.Acos(c) * 180.0 / Math.PI;
        }

        private static void Round4WorldGone()
        {
            ShipComps.Clear();
            PanVoices.Clear();
            _panKey = int.MinValue;
            _nextPanRowMs = 0;
        }

        private static string Round4Status()
        {
            return "pan=" + On(PanOn) + (_panPatched ? "" : "(MISSING)") + (_ownPatched ? "" : " own(MISSING)") + " ownForced=" + Interlocked.Read(ref _ownForced) + " stereo3D=" + Interlocked.Read(ref _stereo3D) + " fixed=" + Interlocked.Read(ref _panFixed) +
                   " rawOneSided=" + Interlocked.Read(ref _panRawOneSided) + " cue=" + On(CueOn) + (_cuePatched ? "" : "(MISSING)") + " cueSwaps=" + Interlocked.Read(ref _cueSwaps) +
                   " chain 0/1/2=" + Interlocked.Read(ref _chain0) + "/" + Interlocked.Read(ref _chain1) + "/" + Interlocked.Read(ref _chain2);
        }
    }
}
