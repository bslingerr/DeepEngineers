using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using DeepEngineers.Wire;
using HarmonyLib;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GUI;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using VRage.Audio;
using VRage.Collections;
using VRage.Data.Audio;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.Utils;
using VRage.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S14 "Audio v2", the SE side (ROADMAP §14 "### S14"; docs/S14-BRIEF.md; CONTRACT §5.4 "S14 conventions"). Every game
 /// member VERIFIED
 /// <list type="bullet">
 /// <item><b>Point 1, the jetpack loop under water.</b> <c>MyCharacterSoundComponent.SelectSound</c>
 /// answers the jetpack idle pair
 /// (<c>CharacterSounds[1]</c>) in the Flying state whatever the jetpack; the S2b swim model holds Flying with
 /// the jetpack off under water (SwimController.EnsureProxyFlying), so the idle loop started in the air on
 /// <c>m_soundEmitters[4]</c> is never stopped: <c>PlayStateSound</c> stops looping emitters only
 /// for the private static <c>EmptySoundPair</c>
 /// A postfix answers that instance while the swim latch holds the character in Flying with
 /// the jetpack not running: SE's own "jetpack off" path stops the loops. SelectSound runs in
 /// <c>UpdateAfterSimulationParallel</c>: the postfix reads a cached id only.</item>
 /// <item><b>Point 2, every SE sound under water.audio row of
 /// says realisticSound false), where <c>MyEntity3DSoundEmitter.InstallDefaultEmitterMethods</c>
 /// installs no implicit-effect delegate, so
 /// <c>PlaySoundInternal</c> (the effect at) never filters and S7's SelectEffect postfix stood down. The Arcade route adds the realistic default (<c>EmitterMethods[3]</c> = the emitter's own <c>SelectEffect</c>,
 /// public field, VRage.Library ConcurrentCachingList Add/ApplyChanges /) in a prefix of PlaySoundInternal
 /// and from the S7 postfix (S14 hook in AudioPolish), so the S7 answer is applied by SE itself
 /// The building sounds go through
 /// <c>MyGuiAudio.PlaySound(MyGuiSounds)</c>
 /// a postfix applies the same effect with <c>MyAudio.Static.ApplyEffect</c>.
 /// The reverb: <c>MyAudio.Static.ApplyReverb</c> under the same gate when the player's EnableReverb is on
 /// the detector's preset back after
 /// The Water Mod adds no filter of its own (WaterSoundComponent.cs: ambience emitters only).</item>
 /// <item><b>Point 3, weapons.</b> The world mod's Data/WaterConfig.xml (BlockConfigs); this side only reads back the Water
 /// Mod's in-memory <c>Jakaria.WaterData.BlockConfigs</c> and counts the weapons under water
 /// </item>

 /// MyThrust's emitter plays SE's own cue of its definition
 /// muffled by point 2, its public <c>VolumeMultiplier</c> = the S14 envelope
 /// level x gain(strength) while the underwater mix is on and the block is below the surface; a type Subnautica plays
 /// (C7 S14 block, <c>/de sn s14 engine subnautica</c>: hydrogen, ion) fades to 0. Thrusters are registered from the
 /// effects producer's per-tick thruster poll (S14 hook) and from the PlaySoundInternal prefix.</item>
 /// </list>
 /// Round 4 (the left ear, the Realistic chain): S14SePan.cs. Rows: <c>s14.jetpack</c>, <c>s14.muffle</c>, <c>s14.thrusters</c>,
 /// <c>s14.weapons</c>, <c>s14.pan</c>. Command <c>de.s14</c> (dev). Off with
 /// <c>DEEPENGINEERS_S14=off</c>. A missing member logs <c>DE.S14 MISSING &lt;member&gt;</c> once and switches off only its
 /// feature. Every patch never throws.
 /// </summary>
    internal static partial class S14Se
    {
        public const string Verb = "de.s14";
        public const string EnvVar = "DEEPENGINEERS_S14";

        // ---------------------------------------------------------------------------------------------- switches
        private static bool _configured;
        private static volatile bool _enabled = true;
        public static bool JetpackOn = true, ArcadeOn = true, GuiOn = true, ReverbOn = true, ThrustersOn = true; // read by the patches on any thread (a stale read is harmless)

 // ---------------------------------------------------------------------------------------------- game members
        private static bool _applyTried;
        private static readonly List<string> Missing = new List<string>();
        private static object _emptyPair; // MySoundPair (MyCharacterSoundComponent.EmptySoundPair)
        private static AccessTools.FieldRef<MyCharacterSoundComponent, List<MyEntity3DSoundEmitter>> _soundEmitters;
        private static AccessTools.FieldRef<MyCharacterSoundComponent, Dictionary<int, MySoundPair>> _characterSounds;
        private static AccessTools.FieldRef<MyEntity3DSoundEmitter, MyStringHash> _activeEffect;
        private static MethodInfo _selectEffect;
        private static bool _jetpackPatched, _playPatched, _guiPatched;

        // ---------------------------------------------------------------------------------------------- per update (update thread writes)
        private static long _swimCharId;
        private static volatile bool _mixActive, _inRoom, _realistic, _muffleWanted, _arcadeWanted, _eyeUnder;

        // the s14.eye counts judge it against an independent camera read (MySector.MainCamera, this frame)
        private static double _eyeY = double.NaN, _camY = double.NaN;
        private static long _eyeFrames, _eyeJudged, _eyeMismatch, _eyeCrossings, _rowEyeFrames, _rowEyeJudged, _rowEyeMismatch, _rowEyeCrossings;
        private static int _eyeSide, _eyeKey = int.MinValue;
        private static ulong _nextEyeRowMs;
        private static volatile string _effectName = S14Conventions.UnderwaterEffect;
        private static MyStringHash _effectHash = MyStringHash.GetOrCompute(S14Conventions.UnderwaterEffect);
        private static ulong _lastTickMs;
        private static bool _inWorldLast;

        // counters (any thread)
        private static long _silenced, _plays, _modPlays, _applied, _delegates, _gui, _guiMuffled, _thrusterPlays;
        private static long _rowSilenced, _rowPlays, _rowModPlays, _rowApplied, _rowGui, _rowGuiMuffled;
        private static volatile MyEntity3DSoundEmitter _sample;

        // reverb
        private static bool _reverbOwned;
        private static ulong _nextReverbMs;
        private static long _reverbSets, _reverbRestores;

        // thrusters (point 4b)
        private sealed class ThrusterState
        {
            public MyThrust Block;
            public ushort Kind;
            public string Cue = "?";
            public readonly S14EngineEnv Env = new S14EngineEnv();
            public float Applied = 1f;     // the VolumeMultiplier S14 wrote (meaningful while Owned)
            public bool Owned;             // S14 wrote this emitter (under water, not silenced); restored once when that ends
            public bool Submerged, Covered, Silenced, VoiceQuiet;
            public double Strength;
        }
        private static readonly ConcurrentDictionary<MyThrust, ThrusterState> Thrusters = new ConcurrentDictionary<MyThrust, ThrusterState>();
        private static readonly List<MyThrust> ThrusterGone = new List<MyThrust>();
        private static AccessTools.FieldRef<MyCubeBlock, MyEntity3DSoundEmitter> _blockEmitter;
        private static Action<MyThrust> _updateSoundState;
        private static bool _thrusterMembersTried;
        private static long _seStarts;
        private static int _seRowsThisSecond;
        private static ulong _seRowWindowMs, _nextSeEngineRowMs;
        private static byte _c7Flags;
        private static bool _c7Fresh;

        //  a ship's thruster sound is SE's ship sound system
 // S14 scales that
 // grid-level sound after SE's own volume (a postfix on UpdateThrusterVolume) and never writes a silenced block
        private struct ShipKey : IEquatable<ShipKey>
        {
            public readonly long Grid;
            public readonly ushort Kind;
            public ShipKey(long g, ushort k) { Grid = g; Kind = k; }
            public bool Equals(ShipKey o) { return Grid == o.Grid && Kind == o.Kind; }
            public override bool Equals(object obj) { return obj is ShipKey && Equals((ShipKey)obj); }
            public override int GetHashCode() { return (Grid.GetHashCode() * 397) ^ Kind; }
        }

        private sealed class ShipSound
        {
            public long Grid;
            public ushort Kind;
            public readonly S14EngineEnv Env = new S14EngineEnv();
            public float Factor = 1f;      // applied after SE's own volume (1 = untouched)
            public bool Submerged, Covered, Seen, Audible;
            public double MaxStrength;
            public float LastBase = -1f, LastRead = -1f;  // SE's main-emitter volume and the factor read back from its voice
            public double ReadLevel = -1, ReadRpmS;       // round 4: the envelope state that produced the factor read back (one SE update behind the tick)
            public ulong LastPostfixMs;
        }
        private static readonly Dictionary<ShipKey, ShipSound> Ships = new Dictionary<ShipKey, ShipSound>();
        private static readonly List<ShipKey> ShipGone = new List<ShipKey>();
        private static AccessTools.FieldRef<MyShipSoundComponent, MyCubeGrid> _shipGrid;
        private static AccessTools.FieldRef<MyShipSoundComponent, MyEntity3DSoundEmitter[]> _shipEmitters;
        private static bool _shipPatched;

        // rows
        private static ulong _nextJetRowMs, _nextMuffleRowMs, _nextThrRowMs, _nextWeaponsMs;
        private static int _jetKey = int.MinValue, _muffleKey = int.MinValue, _thrKey = int.MinValue;
        private static long _rows;

        // weapons (Water Mod in memory)
        private static Type _waterDataType;
        private static string _waterWhy = "not looked up yet";
        private static readonly Dictionary<object, bool> WeaponOriginals = new Dictionary<object, bool>();
        private static readonly List<MyEntity> Near = new List<MyEntity>();
        private static readonly HashSet<long> WeaponGridsSeen = new HashSet<long>();
        private static bool _modOrderWarned;
        private static long _weaponsTurnedOn;
        private static readonly string[] WeaponTypes = { "LargeGatlingTurret", "LargeMissileTurret", "InteriorTurret", "SmallMissileLauncher", "SmallMissileLauncherReload", "SmallGatlingGun" };

        // GUI names by MyGuiSounds value (built once; no ToString per sound)
        private static bool[] _guiMap;

        public static bool Enabled { get { return _enabled; } }
        /// <summary>The S7 polish's starting effect: S14's LowPassHelmet (round 4; 1800 Hz), S7's own when S14 is off (DEEPENGINEERS_S14=off).</summary>
        public static string DefaultEffect { get { LoadConfig(); return _enabled ? S14Conventions.UnderwaterEffect : AudioPolishRules.DefaultEffect; } }
        public static string EffectName { get { return _effectName; } }

        // ============================================================================================== config and patches

        public static void LoadConfig()
        {
            if (_configured) return;
            _configured = true;
            string v = null;
            try { v = Environment.GetEnvironmentVariable(EnvVar); } catch (Exception) { }
            _enabled = !(v != null && (v.Equals("off", StringComparison.OrdinalIgnoreCase) || v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)));
            DeLog.Info("DE.S14 config: " + (_enabled ? "on" : "OFF (" + EnvVar + "=" + v + ")") + "; " + S14Conventions.Header + " " + Hex(S14Conventions.Hash()) +
                       (S14Conventions.Hash() == S14Conventions.PinnedHash ? " pinned" : " MISMATCH") + "; effect " + _effectName);
        }

        private static void Miss(string member, string feature)
        {
            string line = "DE.S14 MISSING " + member + " (" + feature + " off)";
            if (Missing.Contains(line)) return;
            Missing.Add(line);
            DeLog.Info(line);
        }

        /// <summary>Installs the S14 patches once (idempotent; never throws).</summary>
        public static void EnsureApplied()
        {
            if (_applyTried) return;
            _applyTried = true;
            LoadConfig();
            if (!_enabled) return;
            var harmony = new Harmony(HarmonyPatches.HarmonyId);
            // point 1
            try
            {
                MethodInfo select = AccessTools.Method(typeof(MyCharacterSoundComponent), "SelectSound", Type.EmptyTypes);
                FieldInfo empty = AccessTools.Field(typeof(MyCharacterSoundComponent), "EmptySoundPair");
                if (select == null || select.ReturnType != typeof(MySoundPair) || empty == null || !empty.IsStatic || empty.FieldType != typeof(MySoundPair))
                    Miss("MyCharacterSoundComponent.SelectSound() / EmptySoundPair", "the jetpack silence");
                else
                {
                    _emptyPair = empty.GetValue(null);
                    try
                    {
                        _soundEmitters = AccessTools.FieldRefAccess<MyCharacterSoundComponent, List<MyEntity3DSoundEmitter>>("m_soundEmitters");
                        _characterSounds = AccessTools.FieldRefAccess<MyCharacterSoundComponent, Dictionary<int, MySoundPair>>("CharacterSounds");
                    }
                    catch (Exception e) { Miss("MyCharacterSoundComponent.m_soundEmitters / CharacterSounds (" + e.GetType().Name + ")", "the s14.jetpack loop readback"); }
                    harmony.Patch(select, postfix: new HarmonyMethod(typeof(S14Se).GetMethod(nameof(SelectSoundPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                    _jetpackPatched = true;
                }
            }
            catch (Exception e) { Miss("MyCharacterSoundComponent.SelectSound patch (" + e.GetType().Name + ": " + e.Message + ")", "the jetpack silence"); }
            // point 2 (Arcade route, measurement) and point 4 (SE thrusters)
            try
            {
                MethodInfo play = AccessTools.Method(typeof(MyEntity3DSoundEmitter), "PlaySoundInternal", new[] { typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool?) });
                _selectEffect = AccessTools.Method(typeof(MyEntity3DSoundEmitter), "SelectEffect", Type.EmptyTypes);
                if (play == null || _selectEffect == null || _selectEffect.ReturnType != typeof(MyStringHash))
                    Miss("MyEntity3DSoundEmitter.PlaySoundInternal(bool, bool, bool, bool, bool?) / SelectEffect()", "the Arcade route and the SE thruster fade");
                else
                {
                    try { _activeEffect = AccessTools.FieldRefAccess<MyEntity3DSoundEmitter, MyStringHash>("m_activeEffect"); }
                    catch (Exception e) { Miss("MyEntity3DSoundEmitter.m_activeEffect (" + e.GetType().Name + ")", "the s14.muffle applied count"); }
                    harmony.Patch(play, prefix: new HarmonyMethod(typeof(S14Se).GetMethod(nameof(PlayPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                                  postfix: new HarmonyMethod(typeof(S14Se).GetMethod(nameof(PlayPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                    _playPatched = true;
                }
            }
            catch (Exception e) { Miss("MyEntity3DSoundEmitter.PlaySoundInternal patch (" + e.GetType().Name + ": " + e.Message + ")", "the Arcade route and the SE thruster fade"); }
            // point 4b (SE's own thruster sound)
            ThrusterMembers(harmony);
            // round 4: pan.stereo (MyXAudio2.Apply3D) and effect.chain (SelectCue), S14SePan.cs
            Round4Members(harmony);
            // point 2 (the building sounds)
            try
            {
                MethodInfo gui = AccessTools.Method(typeof(MyGuiAudio), "PlaySound", new[] { typeof(MyGuiSounds) });
                if (gui == null || !gui.IsStatic || gui.ReturnType != typeof(IMySourceVoice)) Miss("MyGuiAudio.PlaySound(MyGuiSounds)", "the building sounds' muffle");
                else
                {
                    Array values = Enum.GetValues(typeof(MyGuiSounds));
                    int max = 0;
                    foreach (object o in values) max = Math.Max(max, Convert.ToInt32(o, CultureInfo.InvariantCulture));
                    var map = new bool[max + 1];
                    foreach (object o in values) map[Convert.ToInt32(o, CultureInfo.InvariantCulture)] = S14Rules.IsMuffledGui(o.ToString());
                    _guiMap = map;
                    harmony.Patch(gui, postfix: new HarmonyMethod(typeof(S14Se).GetMethod(nameof(GuiPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                    _guiPatched = true;
                }
            }
            catch (Exception e) { Miss("MyGuiAudio.PlaySound patch (" + e.GetType().Name + ": " + e.Message + ")", "the building sounds' muffle"); }
            DeLog.Info("DE.Harmony S14 jetpack=" + (_jetpackPatched ? "SelectSound postfix" : "MISSING") + " play=" + (_playPatched ? "PlaySoundInternal prefix+postfix" : "MISSING") +
                       " gui=" + (_guiPatched ? "MyGuiAudio.PlaySound postfix" : "MISSING") + " (effect " + _effectName + ")");
        }

        // ============================================================================================== patches (never throw)

 /// <summary>Point 1 (parallel thread): the empty pair while the swim latch holds this character in Flying, jetpack off.</summary>
        private static void SelectSoundPostfix(MyCharacterSoundComponent __instance, ref MySoundPair __result)
        {
            try
            {
                long id = Interlocked.Read(ref _swimCharId);
                if (id == 0 || !_enabled || !JetpackOn || _emptyPair == null || __instance == null) return;
                MyCharacter c = __instance.Entity as MyCharacter;
                if (c == null || c.EntityId != id) return;
                bool flying = c.GetCurrentMovementState() == MyCharacterMovementEnum.Flying;
                if (!S14Rules.SilenceJetpack(true, true, c.JetpackRunning, flying, S14SeMutants.JetpackLoud)) return;
                if (ReferenceEquals(__result, _emptyPair)) return;
                __result = (MySoundPair)_emptyPair;
                Interlocked.Increment(ref _silenced);
            }
            catch (Exception) { }
        }

        /// <summary>Point 2 (Arcade) and point 4 (SE thrusters): before the sound starts.</summary>
        private static void PlayPrefix(MyEntity3DSoundEmitter __instance)
        {
            try
            {
                if (!_enabled || __instance == null) return;
                if (_arcadeWanted) EnsureDelegate(__instance);
                if (ThrustersOn)
                {
                    var t = __instance.Entity as MyThrust;
                    if (t != null)
                    {
                        ThrusterState st = Register(t);
                        // the voice about to start takes the emitter's multiplier (PlaySoundInternal): the level S14 last applied;
 // never on a block SE's ship sound system silenced
                        if (st != null && !t.IsSilenced && st.Applied < 1f) { __instance.VolumeMultiplier = st.Applied; st.Owned = true; }
                        Interlocked.Increment(ref _thrusterPlays);
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>Point 2 measurement: the effect SE applied to a modifiable sound that started.</summary>
        private static void PlayPostfix(MyEntity3DSoundEmitter __instance)
        {
            try
            {
                if (!_enabled || __instance == null) return;
                Interlocked.Increment(ref _plays);
                if (!__instance.IsPlaying) return;
                MySoundData d = __instance.LastSoundData;
                if (d != null && !d.ModifiableByHelmetFilters) return;
                Interlocked.Increment(ref _modPlays);
                bool applied = false;
                if (_activeEffect != null)
                {
                    MyStringHash a = _activeEffect(__instance);
                    applied = a != MyStringHash.NullOrEmpty && a == _effectHash;
                    if (applied) Interlocked.Increment(ref _applied);
                }
                if (_muffleWanted) ChainCount(__instance, applied); // round 4: effect.chain
                _sample = __instance;
            }
            catch (Exception) { }
        }

        /// <summary>Point 2: the building sounds (HUD voice) under water.</summary>
        private static void GuiPostfix(MyGuiSounds sound, IMySourceVoice __result)
        {
            try
            {
                if (!_enabled || !GuiOn || __result == null) return;
                bool[] map = _guiMap;
                int i = (int)sound;
                if (map == null || i < 0 || i >= map.Length || !map[i]) return;
                Interlocked.Increment(ref _gui);
                if (!_muffleWanted || MyAudio.Static == null) return;
                IMyAudioEffect fx = MyAudio.Static.ApplyEffect(__result, _effectHash, null, null, false);
                if (fx != null) Interlocked.Increment(ref _guiMuffled);
            }
            catch (Exception) { }
        }

        /// <summary>
 /// The S7 SelectEffect postfix's muffle gate (S14 hook in AudioPolish, format effect.name of ext s14 audio 5): the eye under
 /// the sea (S2's camera y), not the view's mix; S7's own rule (the mix) when S14 is off, and under S14.M10.
 /// </summary>
        public static bool MuffleOn(bool mixActive)
        {
            return _enabled && !S14SeMutants.MuffleByMix ? _eyeUnder : mixActive;
        }

        /// <summary>
 /// The eye rule judged every update against an independent camera read
 /// a frame with the camera more than Muffle.DeadbandMm from the surface is judged; the decision must be the
 /// camera's side (S2's camera y is the previous step's: one frame of lag at a crossing); crossings counted with that band.
 /// </summary>
        private static void EyeCount(bool polishEnabled, bool inRoom)
        {
            _eyeFrames++;
            SiteMap map = SiteBinding.Map;
            MyCamera cam = MySector.MainCamera;
            _camY = double.NaN;
            if (map == null || cam == null) return;
            Vector3D p = cam.WorldMatrix.Translation;
            _camY = map.SeToSn(new Vec3(p.X, p.Y, p.Z)).Y;
            double band = S14Conventions.Muffle.DeadbandMm / 1000.0;
            int side = _camY > band ? 1 : _camY < -band ? -1 : 0;
            if (side != 0 && _eyeSide != 0 && side != _eyeSide) _eyeCrossings++;
            if (side != 0) _eyeSide = side;
            if (side == 0) return;
            _eyeJudged++;
            bool expect = polishEnabled && !inRoom && side < 0;
            if (expect != _muffleWanted) _eyeMismatch++;
        }

        /// <summary>s14.eye: at each change of the eye, the decision or the mix, and every Rows.PeriodMs while either is under water.</summary>
        private static void EyeRow(ulong nowMs)
        {
            int key = (_eyeUnder ? 1 : 0) | (_muffleWanted ? 2 : 0) | (_mixActive ? 4 : 0) | (_inRoom ? 8 : 0) | (S14SeMutants.MuffleByMix ? 16 : 0);
            bool changed = key != _eyeKey;
            if (!changed && nowMs < _nextEyeRowMs) return;
            _eyeKey = key;
            _nextEyeRowMs = nowMs + (ulong)(_eyeUnder || _mixActive ? S14Conventions.Rows.PeriodMs : 5 * S14Conventions.Rows.PeriodMs);
            var d = Row("s14.eye", changed ? "change" : "periodic");
            d["eyeY"] = double.IsNaN(_eyeY) ? null : (object)Math.Round(_eyeY, 3);
            d["camY"] = double.IsNaN(_camY) ? null : (object)Math.Round(_camY, 3);
            d["eyeUnder"] = _eyeUnder;
            d["muffle"] = _muffleWanted;
            d["mixActive"] = _mixActive;
            d["inRoom"] = _inRoom;
            d["polish"] = AudioPolish.Enabled;
            d["frames"] = _eyeFrames - _rowEyeFrames;
            d["judged"] = _eyeJudged - _rowEyeJudged;
            d["mismatch"] = _eyeMismatch - _rowEyeMismatch;
            d["crossings"] = _eyeCrossings - _rowEyeCrossings;
            d["mutantS14M10"] = S14SeMutants.MuffleByMix;
            _rowEyeFrames = _eyeFrames; _rowEyeJudged = _eyeJudged; _rowEyeMismatch = _eyeMismatch; _rowEyeCrossings = _eyeCrossings;
            Write(d);
        }

        /// <summary>
 /// The S7 SelectEffect postfix's Arcade gate (S14 hook in AudioPolish): true when the emitter carries the
 /// implicit-effect delegate (installed here when missing), so a forced answer is applied by PlaySoundInternal and does
 /// not only restart a loop.
 /// </summary>
        public static bool ArcadeEffectReady(MyEntity3DSoundEmitter e)
        {
            try
            {
                if (!_enabled || !S14Rules.ArcadeRoute(ArcadeOn, false, S14SeMutants.ArcadeOff) || e == null) return false;
                return EnsureDelegate(e);
            }
            catch (Exception) { return false; }
        }

        /// <summary>EmitterMethods[3] (ImplicitEffect) = the emitter's own SelectEffect when empty; true when present.</summary>
        private static bool EnsureDelegate(MyEntity3DSoundEmitter e)
        {
            Dictionary<int, ConcurrentCachingList<Delegate>> methods = e.EmitterMethods;
            ConcurrentCachingList<Delegate> list;
            if (methods == null || !methods.TryGetValue(3, out list) || list == null) return false;
            if (list.Count > 0) return true;
            if (_selectEffect == null) return false;
            if (list.IsEmpty)
            {
                list.Add(Delegate.CreateDelegate(typeof(Func<MyStringHash>), e, _selectEffect));
                Interlocked.Increment(ref _delegates);
            }
            list.ApplyChanges();
            return list.Count > 0;
        }

        // ============================================================================================== per update

 /// <summary>
 /// AudioPolish.Tick (S14 hook; S2Host's audio decision of this update, update thread): the caches the patches read,
 /// the reverb, the SE thruster fade, the rows. Never throws.
 /// </summary>
        public static void Tick(ulong nowMs, bool mixActive, bool inRoom, bool polishEnabled, bool realistic)
        {
            try
            {
                if (!_applyTried) EnsureApplied();
                if (!_enabled) return;
                bool inWorld = MySession.Static != null && MySession.Static.Ready;
                double dtMs = _lastTickMs == 0 || nowMs < _lastTickMs ? 16.7 : Math.Min(250.0, nowMs - _lastTickMs);
                _lastTickMs = nowMs;
                if (!inWorld)
                {
                    if (_inWorldLast) WorldGone();
                    _inWorldLast = false;
                    return;
                }
                _inWorldLast = true;
                long swim = SwimGuard.SwimmingCharacterId;
                Interlocked.Exchange(ref _swimCharId, swim);
                _mixActive = mixActive;
                _inRoom = inRoom;
                _realistic = realistic;
                // the eye's own submersion (format effect.name, ext s14 audio 5): S2's last camera y below Muffle.EyeYMm, whatever the view
 // state, its waterline hysteresis (enter -0.20, leave -0.05 m), its dwell or its 300 ms ramp; S14.M10 = the mix again
                ViewStateDriver drv = S2Host.Driver;
                _eyeY = drv != null && drv.Active ? drv.Y : double.NaN;
                bool eyeUnder = S14Rules.EyeUnder(_eyeY);
                _eyeUnder = eyeUnder;
                _muffleWanted = polishEnabled && (S14SeMutants.MuffleByMix ? mixActive : eyeUnder) && !inRoom;
                EyeCount(polishEnabled, inRoom);
                _arcadeWanted = S14Rules.ArcadeRoute(ArcadeOn, realistic, S14SeMutants.ArcadeOff);
                string en = AudioPolish.EffectName;
                if (en != null && en != _effectName) { _effectName = en; _effectHash = MyStringHash.GetOrCompute(en); }
                Reverb();
                ThrusterTick(nowMs, dtMs, mixActive);
                JetRow(nowMs, swim);
                MuffleRow(nowMs);
                EyeRow(nowMs);
                ThrusterRow(nowMs);
                PanRow(nowMs); // round 4: pan.row
                if (nowMs >= _nextWeaponsMs) { _nextWeaponsMs = nowMs + 10000; WeaponsRow("periodic"); }
            }
            catch (Exception e)
            {
                if (!_tickErrorLogged) { _tickErrorLogged = true; DeLog.Info("DE.Error S14 tick " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private static bool _tickErrorLogged;

        private static void WorldGone()
        {
            Interlocked.Exchange(ref _swimCharId, 0);
            _mixActive = _muffleWanted = false;
            foreach (ThrusterState st in Thrusters.Values)
                try { MyEntity3DSoundEmitter em = EmitterOf(st.Block); if (em != null && st.Owned && !st.Block.IsSilenced) em.VolumeMultiplier = 1f; } catch (Exception) { }
            Thrusters.Clear();
            Ships.Clear();
            if (_reverbOwned) RestoreReverb();
            RestoreWeapons();
            WeaponGridsSeen.Clear();
            _modOrderWarned = false;
            _waterDataType = null;
            _sample = null;
            _nextWeaponsMs = 0;
            Round4WorldGone();
        }

        private static void Reverb()
        {
            IMyAudio a = MyAudio.Static;
            if (a == null) return;
            bool want = ReverbOn && _muffleWanted && a.EnableReverb;
            if (want)
            {
                // set on the dive, then re-asserted at most once a second (the detector writes ApplyReverb only on its own preset
 // change; a device without SE's reverb effect never reads back true, and is not written every update)
                if ((!_reverbOwned || _lastTickMs >= _nextReverbMs) && !a.ApplyReverb)
                {
                    a.ApplyReverb = true;
                    _reverbSets++;
                    _nextReverbMs = _lastTickMs + 1000;
                }
                _reverbOwned = true;
            }
            else if (_reverbOwned) RestoreReverb();
        }

        private static void RestoreReverb()
        {
            _reverbOwned = false;
            try
            {
                IMyAudio a = MyAudio.Static;
                if (a == null) return;
                bool cave = MyEntityReverbDetectorComponent.CurrentReverbPreset == "Cave";
                if (a.ApplyReverb != cave) a.ApplyReverb = cave;
                _reverbRestores++;
            }
            catch (Exception) { }
        }

        private static double SnY(Vector3D p)
        {
            SiteMap s = SiteBinding.Map;
            if (s == null) return 1.0;
            double dx = p.X - s.C.X, dy = p.Y - s.C.Y, dz = p.Z - s.C.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) - s.Rs + s.O.Y;
        }

        private static void ThrusterMembers(Harmony harmony)
        {
            if (_thrusterMembersTried) return;
            _thrusterMembersTried = true;
            try { _blockEmitter = AccessTools.FieldRefAccess<MyCubeBlock, MyEntity3DSoundEmitter>("m_soundEmitter"); }
            catch (Exception e) { Miss("MyCubeBlock.m_soundEmitter (" + e.GetType().Name + ")", "SE's own thruster sound envelope"); }
            try
            {
                MethodInfo m = AccessTools.Method(typeof(MyThrust), "UpdateSoundState", Type.EmptyTypes);
                if (m == null || m.IsStatic || m.ReturnType != typeof(void)) Miss("MyThrust.UpdateSoundState()", "the prompt start of SE's own thruster sound (SE's 100-tick cadence stays)");
                else _updateSoundState = (Action<MyThrust>)Delegate.CreateDelegate(typeof(Action<MyThrust>), m);
            }
            catch (Exception e) { Miss("MyThrust.UpdateSoundState() (" + e.GetType().Name + ")", "the prompt start of SE's own thruster sound"); }
            // the ship-level thruster sound: MyShipSoundComponent.UpdateThrusterVolume(ShipEmitters emitter, ShipEmitters idleEmitter,
 // ShipThrusters thruster, float, float, float, float, bool playPush, ShipEmitters pushEmitter, ShipSystemSoundsEnum)
 // m_shipGrid, m_emitters
            try
            {
                MethodInfo u = AccessTools.Method(typeof(MyShipSoundComponent), "UpdateThrusterVolume");
                ParameterInfo[] ps = u != null ? u.GetParameters() : null;
                _shipGrid = AccessTools.FieldRefAccess<MyShipSoundComponent, MyCubeGrid>("m_shipGrid");
                _shipEmitters = AccessTools.FieldRefAccess<MyShipSoundComponent, MyEntity3DSoundEmitter[]>("m_emitters");
                if (u == null || u.IsStatic || ps == null || ps.Length != 10 || !ps[0].ParameterType.IsEnum || !ps[2].ParameterType.IsEnum || !ps[8].ParameterType.IsEnum)
                    Miss("MyShipSoundComponent.UpdateThrusterVolume(ShipEmitters, ShipEmitters, ShipThrusters, …)", "the ship-level thruster sound under water");
                else
                {
                    harmony.Patch(u, postfix: new HarmonyMethod(typeof(S14Se).GetMethod(nameof(ShipThrusterPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                    _shipPatched = true;
                }
            }
            catch (Exception e) { Miss("MyShipSoundComponent.UpdateThrusterVolume / m_shipGrid / m_emitters (" + e.GetType().Name + ": " + e.Message + ")", "the ship-level thruster sound under water"); }
        }

        private static MyEntity3DSoundEmitter EmitterOf(MyThrust t)
        {
            return t != null && _blockEmitter != null ? _blockEmitter(t) : null;
        }

        private static ThrusterState Register(MyThrust t)
        {
            if (t == null) return null;
            ThrusterState st;
            if (Thrusters.TryGetValue(t, out st)) return st;
            st = new ThrusterState { Block = t };
            //  a thruster first seen under water starts silent (the envelope's start, or faded under Subnautica's loop):
 // no 300 ms of SE's hiss before the first tick (a silenced block is never written: PlayPrefix, ThrusterTick)
            try { if (_mixActive && SnY(t.PositionComp.GetPosition()) < 0.0 && !S14SeMutants.ThrusterLoud) st.Applied = 0f; } catch (Exception) { }
            try
            {
                string type = t.BlockDefinition != null ? t.BlockDefinition.ThrusterType.String : null;
                st.Kind = S7EventRules.ThrusterKindOf(type);
                if (t.BlockDefinition != null && t.BlockDefinition.PrimarySound != null) st.Cue = t.BlockDefinition.PrimarySound.ToString();
            }
            catch (Exception) { }
            return Thrusters.GetOrAdd(t, st);
        }

        /// <summary>
 /// EffectsProducer.PollThrusters (S14 hook, update thread, every tick): each thruster of a mirrored grid, so SE's own sound
 /// can start on the tick the thrust does. Never throws.
 /// </summary>
        public static void OnThruster(MyThrust t)
        {
            try
            {
                if (!_enabled || !ThrustersOn || t == null) return;
                Register(t);
            }
            catch (Exception) { }
        }

        /// <summary>MyShipSoundComponent's ShipThrusters (Ion 0, Hydrogen 1, Atmospheric 2, Prototech 3) as S7's ThrusterKind.</summary>
        private static ushort ShipKind(int shipThrusters)
        {
            switch (shipThrusters)
            {
                case 0: return S7Conventions.ThrusterKind.Ion;
                case 1: return S7Conventions.ThrusterKind.Hydrogen;
                case 2: return S7Conventions.ThrusterKind.Atmospheric;
                default: return S7Conventions.ThrusterKind.Other;
            }
        }

        private static MyEntity3DSoundEmitter At(MyEntity3DSoundEmitter[] em, int i)
        {
            return em != null && i >= 0 && i < em.Length ? em[i] : null;
        }

        private static bool Audible(MyEntity3DSoundEmitter e)
        {
            if (e == null || !e.IsPlaying) return false;
            IMySourceVoice v = e.Sound;
            return v != null && v.VolumeMultiplier > 0.05f;
        }

        /// <summary>
 /// Review HIGH 1 (update thread, inside SE's ship sound update): after SE set its own volume of one thruster type's
 /// emitters, under water S14 scales the main emitter by the type's envelope (S14's level x gain) and, when Subnautica's
 /// loop plays the type, the main, idle and push emitters toward 0; above water nothing is touched. SE writes the main and
 /// idle volumes again on every update, so the scale never compounds. Never throws.
 /// </summary>
        private static void ShipThrusterPostfix(MyShipSoundComponent __instance, object[] __args)
        {
            try
            {
                if (!_enabled || __instance == null || __args == null || __args.Length < 9 || _shipGrid == null || _shipEmitters == null) return;
                ushort kind = ShipKind(Convert.ToInt32(__args[2], CultureInfo.InvariantCulture));
                if (kind == S7Conventions.ThrusterKind.Other) return;
                MyCubeGrid g = _shipGrid(__instance);
                SeenShip(__instance, g); // round 4: the s14.pan voices
                ShipSound ss;
                if (g == null || !Ships.TryGetValue(new ShipKey(g.EntityId, kind), out ss) || !ss.Submerged) return;
                MyEntity3DSoundEmitter[] em = _shipEmitters(__instance);
                MyEntity3DSoundEmitter main = At(em, Convert.ToInt32(__args[0], CultureInfo.InvariantCulture));
                MyEntity3DSoundEmitter idle = At(em, Convert.ToInt32(__args[1], CultureInfo.InvariantCulture));
                MyEntity3DSoundEmitter push = At(em, Convert.ToInt32(__args[8], CultureInfo.InvariantCulture));
                float f = ss.Factor;
                ss.LastPostfixMs = _lastTickMs;
                if (main != null && main.IsPlaying)
                {
                    float b = main.VolumeMultiplier; // SE's own value of this update
                    ss.LastBase = b;
                    if (f < 1f) main.VolumeMultiplier = b * f;
                    IMySourceVoice v = main.Sound;
                    ss.LastRead = v != null && b > 1e-3f ? v.VolumeMultiplier / b : -1f;
                    ss.ReadLevel = ss.Env.Level; // round 4: the pair (state, factor) of this write; the tick steps the envelope after
                    ss.ReadRpmS = ss.Env.RpmS;
                }
                else { ss.LastBase = -1f; ss.LastRead = -1f; }
                if (ss.Covered && f < 1f)
                {
                    if (idle != null && idle.IsPlaying && !ReferenceEquals(idle, main)) idle.VolumeMultiplier = idle.VolumeMultiplier * f;
                    if (push != null && push.IsPlaying && !ReferenceEquals(push, idle) && !ReferenceEquals(push, main)) push.VolumeMultiplier = push.VolumeMultiplier * f;
                }
                ss.Audible = Audible(main) || Audible(idle) || Audible(push);
            }
            catch (Exception) { }
        }

        private static ShipSound ShipOf(long grid, ushort kind)
        {
            var k = new ShipKey(grid, kind);
            ShipSound ss;
            if (!Ships.TryGetValue(k, out ss)) { ss = new ShipSound { Grid = grid, Kind = kind }; Ships[k] = ss; }
            return ss;
        }

        /// <summary>
 /// Point 4b, every update. Per registered thruster: a block SE's ship sound system silenced is never written, and above
 /// water nothing is touched (a value S14 wrote is restored once); under water an unsilenced block plays SE's own cue with
 /// the envelope (level x gain(strength) on its emitter) or fades to 0 when Subnautica's loop plays the type (C7 S14 block).
 /// Per grid and type (the ship-level sound, scaled in <see cref="ShipThrusterPostfix"/>): the same envelope on the
 /// strongest thruster of that type under water, or the fade under Subnautica's loop.
 /// </summary>
        private static void ThrusterTick(ulong nowMs, double dtMs, bool mixActive)
        {
            ReadC7();
            if (Thrusters.Count == 0 && Ships.Count == 0) return;
            ThrusterGone.Clear();
            bool on = ThrustersOn, loud = S14SeMutants.ThrusterLoud;
            double nowS = nowMs / 1000.0;
            double start = S14Conventions.Engine.SeStartStrengthPermille / 1000.0;
            if (nowMs >= _seRowWindowMs + 1000) { _seRowWindowMs = nowMs; _seRowsThisSecond = 0; }
            bool rowDue = nowMs >= _nextSeEngineRowMs;
            int periodic = 0;
            foreach (ShipSound ss in Ships.Values) { ss.Seen = false; ss.Submerged = false; ss.Covered = false; ss.MaxStrength = 0; }
            foreach (KeyValuePair<MyThrust, ThrusterState> kv in Thrusters)
            {
                MyThrust t = kv.Key;
                ThrusterState st = kv.Value;
                if (t.Closed || t.MarkedForClose) { ThrusterGone.Add(t); continue; }
                MyEntity3DSoundEmitter em = EmitterOf(t);
                double y = SnY(t.PositionComp.GetPosition());
                bool submerged = on && S14Rules.ThrusterSubmerged(mixActive, y);
                bool covered = submerged && S14Rules.SnCovers(_c7Fresh, _c7Flags, st.Kind);
                double strength = 0;
                try { strength = t.CurrentStrength; } catch (Exception) { }
                bool silenced = t.IsSilenced;
                st.Strength = strength;
                st.Submerged = submerged;
                st.Covered = covered;
                st.Silenced = silenced;
                if (t.CubeGrid != null)
                {
                    ShipSound ss = ShipOf(t.CubeGrid.EntityId, st.Kind);
                    ss.Seen = true;
                    if (submerged)
                    {
                        ss.Submerged = true;
                        ss.Covered |= covered;
                        if (t.IsWorking && strength > ss.MaxStrength) ss.MaxStrength = strength;
                    }
                }
                if (em == null) continue;
                if (silenced || !submerged)
                {
                    //  SE's ship sound system owns a silenced block's volume,
 // and above water SE's own rules stand: only a value S14 wrote under water is given back, once
                    if (st.Owned)
                    {
                        if (!silenced) em.VolumeMultiplier = 1f;
                        st.Owned = false;
                    }
                    st.Applied = 1f;
                    if (st.Env.Playing || st.Env.Level > 0) st.Env.Reset();
                    st.VoiceQuiet = !em.IsPlaying || em.Sound == null || em.Sound.VolumeMultiplier < 0.05f;
                    continue;
                }
                float target;
                if (!covered)
                {
                    bool want = strength > start && t.IsWorking;
                    long attacks = st.Env.Attacks;
                    S14EnvEvent ev = st.Env.Step(want, S14Rules.Clamp01(strength), dtMs, nowS, loud);
                    target = (float)S14Rules.SeThrusterVolume(true, false, st.Env.Volume(loud), loud);
                    // the envelope's level first, then SE's own cue
 // starts on this tick
                    if (target != st.Applied || !st.Owned) { st.Applied = target; em.VolumeMultiplier = target; st.Owned = true; }
                    if (want && !em.IsPlaying && ev == S14EnvEvent.Start) _seStarts++;
                    if (want && _updateSoundState != null && t.IsWorking) _updateSoundState(t);
                    if (ev != S14EnvEvent.None || st.Env.Attacks != attacks)
                        SeEngineRow(st.Env.Attacks != attacks && ev != S14EnvEvent.Start ? "attack" : ev.ToString().ToLowerInvariant(), st.Block.EntityId, st.Kind, st.Cue, st.Env, st.Strength, em, -1f, loud, true, false);
                }
                else
                {
                    if (st.Env.Playing || st.Env.Level > 0) st.Env.Reset();
                    // covered by Subnautica's loop: fade out over SeThrusterFadeMs
                    float aim = (float)S14Rules.SeThrusterVolume(true, true, 0, loud);
                    target = (float)S14Rules.Step(st.Applied, aim, dtMs, S14Conventions.Envelope.SeThrusterFadeMs, S14Conventions.Envelope.SeThrusterFadeMs);
                }
                if (target != st.Applied || Math.Abs(em.VolumeMultiplier - target) > 1e-3f)
                {
                    st.Applied = target;
                    em.VolumeMultiplier = target;
                    st.Owned = true;
                }
                //  the faded state judged from the voice SE plays, not from the value written
                IMySourceVoice voice = em.Sound;
                st.VoiceQuiet = !em.IsPlaying || voice == null || voice.VolumeMultiplier < 0.05f;
                if (rowDue && !covered && st.Env.Playing && periodic < 4) { periodic++; SeEngineRow("periodic", st.Block.EntityId, st.Kind, st.Cue, st.Env, st.Strength, em, -1f, loud, false, false); }
            }
            // the ship-level sound per grid and type
            ShipGone.Clear();
            foreach (KeyValuePair<ShipKey, ShipSound> kv in Ships)
            {
                ShipSound ss = kv.Value;
                if (!ss.Seen) { ShipGone.Add(kv.Key); continue; }
                if (!ss.Submerged)
                {
                    ss.Factor = 1f;
                    if (ss.Env.Playing || ss.Env.Level > 0) ss.Env.Reset();
                    continue;
                }
                if (ss.Covered)
                {
                    if (ss.Env.Playing || ss.Env.Level > 0) ss.Env.Reset();
                    ss.Factor = (float)S14Rules.Step(ss.Factor, S14Rules.SeThrusterVolume(true, true, 0, loud), dtMs, S14Conventions.Envelope.SeThrusterFadeMs, S14Conventions.Envelope.SeThrusterFadeMs);
                    continue;
                }
                bool want = ss.MaxStrength > start;
                long attacks = ss.Env.Attacks;
                S14EnvEvent ev = ss.Env.Step(want, S14Rules.Clamp01(ss.MaxStrength), dtMs, nowS, loud);
                ss.Factor = (float)S14Rules.SeThrusterVolume(true, false, ss.Env.Volume(loud), loud);
                bool ship = nowMs <= ss.LastPostfixMs + 1000; // SE's ship sound plays this type on this grid
                if (ship && (ev != S14EnvEvent.None || ss.Env.Attacks != attacks))
                    SeEngineRow(ss.Env.Attacks != attacks && ev != S14EnvEvent.Start ? "attack" : ev.ToString().ToLowerInvariant(), ss.Grid, ss.Kind, "ship", ss.Env, ss.MaxStrength, null, ss.LastRead, loud, true, true);
                if (ship && rowDue && ss.Env.Playing && ss.LastRead >= 0f && periodic < 6) { periodic++; SeEngineRow("periodic", ss.Grid, ss.Kind, "ship", ss.Env, ss.MaxStrength, null, ss.LastRead, loud, false, true, ss.ReadLevel, ss.ReadRpmS); }
            }
            for (int i = 0; i < ShipGone.Count; i++) Ships.Remove(ShipGone[i]);
            if (rowDue) _nextSeEngineRowMs = nowMs + (ulong)S14Conventions.Rows.PeriodMs;
            for (int i = 0; i < ThrusterGone.Count; i++)
            {
                ThrusterState gone;
                Thrusters.TryRemove(ThrusterGone[i], out gone);
            }
            if (!on)
            {
                // switched off: every value S14 wrote is back (above), then forget them
                bool any = false;
                foreach (ThrusterState st in Thrusters.Values) if (st.Owned) { any = true; break; }
                if (!any) { Thrusters.Clear(); Ships.Clear(); }
            }
        }

        /// <summary>The C7 S14 block of a fresh SnStatus (which thruster types Subnautica's loop plays).</summary>
        private static void ReadC7()
        {
            _c7Fresh = false;
            _c7Flags = 0;
            try
            {
                Plugin pl = Plugin.Instance;
                Link l = pl != null ? pl.LinkForS1 : null;
                if (l == null || l.State != LinkState.Up || !l.HasSnStatus) return;
                SnStatus st = l.LastSnStatus;
                byte f;
                _c7Fresh = S14Wire.TryReadStatus(ref st, out f);
                _c7Flags = _c7Fresh ? f : (byte)0;
            }
            catch (Exception) { }
        }

        private static string KindName(ushort kind)
        {
            return kind == S7Conventions.ThrusterKind.Hydrogen ? "hydrogen" : kind == S7Conventions.ThrusterKind.Ion ? "ion" :
                   kind == S7Conventions.ThrusterKind.Atmospheric ? "atmospheric" : "other";
        }

        /// <summary>
 /// The s14.engine row of SE's own thruster sound (source se; events capped at 20 per second): a block's own emitter, or the
 /// ship-level sound of one type (<paramref name="ship"/>: <paramref name="shipRead"/> = the factor read back from its voice).
 /// </summary>
        private static void SeEngineRow(string why, long entity, ushort kind, string cue, S14EngineEnv env, double strength, MyEntity3DSoundEmitter em, float shipRead,
                                        bool loud, bool isEvent, bool ship, double readLevel = -1, double readRpmS = 0)
        {
            if (isEvent && ++_seRowsThisSecond > 20) return;
            IMySourceVoice v = em != null ? em.Sound : null;
            var d = Row("s14.engine", why);
            d["source"] = "se";
            d["ship"] = ship;
            d["type"] = KindName(kind);
            d["cue"] = cue;
            d["entity"] = entity;
            // round 4 (N4V on the 22:09 session: the ship read lagged one update): a ship's periodic row reports the envelope state
 // the read factor was written with (captured in ShipThrusterPostfix), and the current one as levelNow
            bool atRead = ship && readLevel >= 0;
            d["level"] = Math.Round(atRead ? readLevel : env.Level, 4);
            d["rpm"] = Math.Round(strength, 4);
            d["rpmS"] = Math.Round(atRead ? readRpmS : env.RpmS, 4);
            d["volume"] = Math.Round(atRead ? (loud ? 1.0 : S14Rules.EngineVolume(readLevel, readRpmS)) : env.Volume(loud), 4);
            if (atRead) d["levelNow"] = Math.Round(env.Level, 4);
            d["volumeRead"] = ship ? Math.Round(shipRead, 4) : v != null && v.IsPlaying ? Math.Round(v.VolumeMultiplier, 4) : -1.0;
            d["gain"] = Math.Round(S14Rules.EngineGain(atRead ? readRpmS : env.RpmS), 4);
            d["playing"] = env.Playing;
            d["soundPlaying"] = ship || (em != null && em.IsPlaying);
            d["want"] = env.Want;
            d["attackMs"] = double.IsNaN(env.LastAttackMs) ? -1.0 : Math.Round(env.LastAttackMs, 1);
            d["releaseMs"] = double.IsNaN(env.LastReleaseMs) ? -1.0 : Math.Round(env.LastReleaseMs, 1);
            d["starts"] = env.Starts;
            d["resumes"] = env.Resumes;
            d["stops"] = env.Stops;
            d["envelope"] = !loud;
            d["mutantS14M7"] = loud;
            Write(d);
        }

        // ============================================================================================== rows

        private static void JetRow(ulong nowMs, long swim)
        {
            MyCharacter c = MySession.Static != null ? MySession.Static.LocalCharacter : null;
            bool held = c != null && swim != 0 && c.EntityId == swim;
            bool running = false, idle = false, run = false;
            string state = "none";
            if (c != null)
            {
                try
                {
                    running = c.JetpackRunning;
                    state = c.GetCurrentMovementState().ToString();
                    ReadLoops(c, out idle, out run);
                }
                catch (Exception) { }
            }
            int key = (held ? 1 : 0) | (running ? 2 : 0) | (idle ? 4 : 0) | (run ? 8 : 0);
            bool changed = key != _jetKey;
            // out of the water only the latch's own edge is written (flying with the jetpack toggles its loops all the time)
            if (changed && !held && ((key ^ _jetKey) & 1) == 0) { _jetKey = key; return; }
            if (!changed && !(held && nowMs >= _nextJetRowMs)) return;
            _jetKey = key;
            _nextJetRowMs = nowMs + (ulong)S14Conventions.Rows.PeriodMs;
            long s = Interlocked.Read(ref _silenced);
            var d = Row("s14.jetpack", changed ? "change" : "periodic");
            d["swimHeld"] = held;
            d["jetpackRunning"] = running;
            d["state"] = state;
            d["idleLoopPlaying"] = idle;
            d["runLoopPlaying"] = run;
            d["silenced"] = s - _rowSilenced;
            d["patched"] = _jetpackPatched;
            d["on"] = JetpackOn;
            d["mutantS14M1"] = S14SeMutants.JetpackLoud;
            _rowSilenced = s;
            Write(d);
        }

        /// <summary>The character's jetpack loops as SE plays them: emitter 4 (idle, CharacterSounds[1]) and emitter 0 with the run pair [2].</summary>
        private static void ReadLoops(MyCharacter c, out bool idle, out bool run)
        {
            idle = run = false;
            MyCharacterSoundComponent sc = c.SoundComp;
            if (sc == null || _soundEmitters == null || _characterSounds == null) return;
            List<MyEntity3DSoundEmitter> em = _soundEmitters(sc);
            Dictionary<int, MySoundPair> cs = _characterSounds(sc);
            if (em == null || em.Count < 5 || cs == null) return;
            MySoundPair idlePair, runPair;
            cs.TryGetValue(1, out idlePair);
            cs.TryGetValue(2, out runPair);
            MyEntity3DSoundEmitter e4 = em[4], e0 = em[0];
            idle = e4 != null && e4.IsPlaying && (idlePair == null || idlePair.Equals(e4.SoundPair));
            run = e0 != null && e0.IsPlaying && runPair != null && runPair.Equals(e0.SoundPair);
        }

        private static void MuffleRow(ulong nowMs)
        {
            bool mix = _mixActive;
            int key = (mix ? 1 : 0) | (_inRoom ? 2 : 0) | (_realistic ? 4 : 0) | (_arcadeWanted ? 8 : 0) | (_reverbOwned ? 16 : 0) | (_eyeUnder ? 32 : 0);
            bool changed = key != _muffleKey;
            if (!changed && nowMs < _nextMuffleRowMs) return;
            _muffleKey = key;
            _nextMuffleRowMs = nowMs + (ulong)(mix || _eyeUnder ? S14Conventions.Rows.PeriodMs : 5 * S14Conventions.Rows.PeriodMs);
            long plays = Interlocked.Read(ref _plays), mod = Interlocked.Read(ref _modPlays), applied = Interlocked.Read(ref _applied);
            long gui = Interlocked.Read(ref _gui), guiM = Interlocked.Read(ref _guiMuffled);
            var d = Row("s14.muffle", changed ? "change" : "periodic");
            d["mixActive"] = mix;
            d["eyeUnder"] = _eyeUnder;   // the muffle's gate since ext s14 audio 5 (the eye), mixActive the S2 mix's
            d["eyeY"] = double.IsNaN(_eyeY) ? null : (object)Math.Round(_eyeY, 3);
            d["inRoom"] = _inRoom;
            d["realisticSound"] = _realistic;
            d["route"] = _realistic ? "realistic" : _arcadeWanted ? "arcade" : "none";
            d["effect"] = _effectName;
            d["polish"] = AudioPolish.Enabled;
            d["plays"] = plays - _rowPlays;
            d["modifiable"] = mod - _rowModPlays;
            d["applied"] = applied - _rowApplied;
            d["delegates"] = Interlocked.Read(ref _delegates);
            d["gui"] = gui - _rowGui;
            d["guiMuffled"] = guiM - _rowGuiMuffled;
            IMyAudio a = MyAudio.Static;
            d["reverbAvailable"] = a != null && a.EnableReverb;
            d["reverbOn"] = a != null && a.ApplyReverb;
            d["reverbOwned"] = _reverbOwned;
            d["reverbPreset"] = MyEntityReverbDetectorComponent.CurrentReverbPreset;
            string what;
            double hz = SampleCutoff(out what);
            d["sampleCutoffHz"] = double.IsNaN(hz) ? null : (object)Math.Round(hz, 1);
            d["sample"] = what;
            d["patched"] = _playPatched;
            d["mutantS14M2"] = S14SeMutants.ArcadeOff;
            ChainFields(d); // round 4: effect.chain
            _rowPlays = plays; _rowModPlays = mod; _rowApplied = applied; _rowGui = gui; _rowGuiMuffled = guiM;
            Write(d);
        }

        // reflection over SharpDX.XAudio2 (no compile-time reference): MySourceVoice.Voice → Voice.GetFilterParameters(out FilterParameters)
        private static Type _voiceType;
        private static PropertyInfo _voiceProp;
        private static MethodInfo _getFilter;
        private static FieldInfo _freqField;
        private static bool _filterBroken;

        /// <summary>The XAudio2 filter of the last modifiable SE sound that started, read back from its voice (Hz; NaN when none).</summary>
        private static double SampleCutoff(out string what)
        {
            what = null;
            MyEntity3DSoundEmitter e = _sample;
            if (e == null || _filterBroken) return double.NaN;
            try
            {
                IMySourceVoice v = e.Sound;
                if (v == null || !v.IsPlaying) return double.NaN;
                what = e.Entity != null ? e.Entity.GetType().Name : "emitter";
                Type vt = v.GetType();
                if (vt != _voiceType)
                {
                    _voiceType = vt;
                    _voiceProp = vt.GetProperty("Voice", BindingFlags.Instance | BindingFlags.Public);
                    _getFilter = null;
                }
                object voice = _voiceProp != null ? _voiceProp.GetValue(v, null) : null;
                if (voice == null) return double.NaN;
                if (_getFilter == null)
                {
                    foreach (MethodInfo m in voice.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
                    {
                        if (m.Name != "GetFilterParameters") continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType.IsByRef) { _getFilter = m; _freqField = ps[0].ParameterType.GetElementType().GetField("Frequency"); break; }
                    }
                    if (_getFilter == null || _freqField == null) { _filterBroken = true; Miss("SharpDX.XAudio2.Voice.GetFilterParameters(out FilterParameters)", "the s14.muffle filter readback"); return double.NaN; }
                }
                var args = new object[1];
                _getFilter.Invoke(voice, args);
                if (args[0] == null) return double.NaN;
                return S14Rules.CutoffHz(Convert.ToDouble(_freqField.GetValue(args[0]), CultureInfo.InvariantCulture));
            }
            catch (Exception) { return double.NaN; }
        }

        private static void ThrusterRow(ulong nowMs)
        {
            int n = Thrusters.Count, submerged = 0, covered = 0, coveredQuiet = 0, seAudible = 0, silenced = 0;
            int shipGrids = 0, shipCovered = 0, shipCoveredAudible = 0;
            foreach (ShipSound ss in Ships.Values)
            {
                if (!ss.Submerged || _lastTickMs > ss.LastPostfixMs + 1000) continue; // only types SE's ship sound plays now
                shipGrids++;
                if (ss.Covered) { shipCovered++; if (ss.Audible) shipCoveredAudible++; }
            }
            int[] byKind = new int[4], coveredByKind = new int[4];
            foreach (ThrusterState st in Thrusters.Values)
            {
                if (!st.Submerged) continue;
                submerged++;
                int k = st.Kind < 4 ? st.Kind : 0;
                byKind[k]++;
                if (st.Covered) coveredByKind[k]++;
                if (st.Silenced) { silenced++; continue; } // SE's ship sound plays it (the ship layer above)
                if (st.Covered) { covered++; if (st.VoiceQuiet) coveredQuiet++; }
                else if (st.Applied > 0f) seAudible++;
            }
            int key = (n > 0 ? 1 : 0) | (_mixActive ? 2 : 0) | (covered + shipCovered > 0 ? 4 : 0) | (submerged > 0 ? 8 : 0) | (_c7Flags << 4);
            bool changed = key != _thrKey;
            if (!changed && !(n > 0 && _mixActive && submerged > 0 && nowMs >= _nextThrRowMs)) return;
            _thrKey = key;
            _nextThrRowMs = nowMs + (ulong)S14Conventions.Rows.PeriodMs;
            var d = Row("s14.thrusters", changed ? "change" : "periodic");
            d["mixActive"] = _mixActive;
            d["registered"] = n;
            d["submerged"] = submerged;
            d["covered"] = covered;
            d["coveredQuiet"] = coveredQuiet;
            d["seAudible"] = seAudible;
            d["silenced"] = silenced;
            d["shipGrids"] = shipGrids;
            d["shipCovered"] = shipCovered;
            d["shipCoveredAudible"] = shipCoveredAudible;
            d["shipPatched"] = _shipPatched;
            d["snMode"] = !_c7Fresh ? "absent" : (_c7Flags & S14Conventions.C7Flags.SnLoops) != 0 ? "subnautica" : "se";
            d["hydrogen"] = byKind[S7Conventions.ThrusterKind.Hydrogen];
            d["ion"] = byKind[S7Conventions.ThrusterKind.Ion];
            d["atmospheric"] = byKind[S7Conventions.ThrusterKind.Atmospheric];
            d["hydrogenSource"] = Source(byKind, coveredByKind, S7Conventions.ThrusterKind.Hydrogen);
            d["ionSource"] = Source(byKind, coveredByKind, S7Conventions.ThrusterKind.Ion);
            d["atmosphericSource"] = Source(byKind, coveredByKind, S7Conventions.ThrusterKind.Atmospheric);
            d["seStarts"] = _seStarts;
            d["plays"] = Interlocked.Read(ref _thrusterPlays);
            d["on"] = ThrustersOn;
            d["mutantS14M7"] = S14SeMutants.ThrusterLoud;
            Write(d);
        }

        private static string Source(int[] byKind, int[] coveredByKind, ushort k)
        {
            if (byKind[k] == 0) return "-";
            return coveredByKind[k] == byKind[k] ? "subnautica" : coveredByKind[k] == 0 ? "se" : "mixed";
        }

        /// <summary>Point 3: the Water Mod's in-memory weapon configs (the world mod's override loaded?) and the weapons under water near the camera.</summary>
        private static void WeaponsRow(string why)
        {
            int configs = 0, functional = 0;
            string err = ReadWeaponConfigs(ref configs, ref functional);
            //  the override did not win (the Water Mod reads every mod's Data/WaterConfig.xml in the world's mod order,
 // the last entry of a definition wins): name the order once per world
            if (_waterDataType != null && configs > 0 && functional < S14Conventions.Weapons.Configs && !S14SeMutants.WeaponsReverted && !_modOrderWarned)
            {
                _modOrderWarned = true;
                DeLog.Info("DE.S14 WARN weapon override not effective: " + functional + " of " + S14Conventions.Weapons.Configs +
                           " Water Mod weapon configs functional under water; the world's mod order: " + ModOrder() +
                           " (the Water Mod reads each mod's Data/WaterConfig.xml in this order and the last wins: DeepEngineers.World must come after the Water Mod)");
            }
            //  the Water Mod keeps a submerged weapon's earlier state only in memory
 // so a ship saved under water reloads with its guns off and,
 // with the override loaded, nothing turns them on again: once per grid, at its first scan after load, while the
 // override is in effect, the weapons under water that are off are switched on (the terminal stays the fallback)
            bool overrideOn = configs >= S14Conventions.Weapons.Configs && functional == configs && !S14SeMutants.WeaponsReverted;
            int under = 0, enabled = 0, working = 0, turnedOn = 0, grids = 0;
            try
            {
                if (MySector.MainCamera != null && SiteBinding.Map != null)
                {
                    var sphere = new BoundingSphereD(MySector.MainCamera.Position, 400.0);
                    Near.Clear();
                    MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, Near);
                    foreach (MyEntity ent in Near)
                    {
                        var g = ent as MyCubeGrid;
                        if (g == null || g.Closed) continue;
                        bool first = overrideOn && WeaponGridsSeen.Add(g.EntityId);
                        int gridOn = 0;
                        foreach (MyUserControllableGun gun in g.GetFatBlocks<MyUserControllableGun>())
                        {
                            if (gun == null || SnY(gun.PositionComp.GetPosition()) >= 0) continue;
                            under++;
                            if (first && !gun.Enabled && gun.IsFunctional)
                            {
                                gun.Enabled = true; // MyFunctionalBlock.Enabled, SE's own terminal switch
                                gridOn++;
                            }
                            if (gun.Enabled) enabled++;
                            if (gun.IsWorking) working++;
                        }
                        if (first) grids++;
                        if (gridOn > 0)
                        {
                            turnedOn += gridOn;
                            _weaponsTurnedOn += gridOn;
                            var r = Row("s14.weapons.restore", "first scan");
                            r["grid"] = g.EntityId;
                            r["name"] = g.DisplayName;
                            r["turnedOn"] = gridOn;
                            Write(r);
                            DeLog.Info("DE.S14 weapons: " + gridOn + " weapon(s) under water on '" + g.DisplayName + "' switched back on (the Water Mod had turned them off before the save)");
                        }
                    }
                    Near.Clear();
                }
            }
            catch (Exception) { }
            var d = Row("s14.weapons", why);
            d["waterMod"] = _waterDataType != null ? "found" : _waterWhy;
            d["weaponConfigs"] = configs;
            d["functionalUnderwater"] = functional;
            d["expected"] = S14Conventions.Weapons.Configs;
            d["weaponsUnderwater"] = under;
            d["enabledUnderwater"] = enabled;
            d["workingUnderwater"] = working;
            d["overrideOn"] = overrideOn;
            d["gridsChecked"] = grids;
            d["turnedOn"] = turnedOn;
            d["turnedOnTotal"] = _weaponsTurnedOn;
            if (err != null) d["error"] = err;
            d["mutantS14M3"] = S14SeMutants.WeaponsReverted;
            Write(d);
        }

        private static string ReadWeaponConfigs(ref int configs, ref int functional)
        {
            if (_waterDataType == null)
            {
                _waterDataType = FindWaterData(out _waterWhy);
                if (_waterDataType == null) return _waterWhy;
            }
            FieldInfo f = _waterDataType.GetField("BlockConfigs", BindingFlags.Public | BindingFlags.Static);
            var dict = f != null ? f.GetValue(null) as IDictionary : null;
            if (dict == null) { Miss("Jakaria.WaterData.BlockConfigs", "the s14.weapons readback"); return "Jakaria.WaterData.BlockConfigs unreadable"; }
            bool revert = S14SeMutants.WeaponsReverted;
            foreach (DictionaryEntry kv in dict)
            {
                if (!(kv.Key is MyDefinitionId) || kv.Value == null) continue;
                var id = (MyDefinitionId)kv.Key;
                string type = id.TypeId.ToString();
                if (type.StartsWith("MyObjectBuilder_", StringComparison.Ordinal)) type = type.Substring("MyObjectBuilder_".Length);
                if (Array.IndexOf(WeaponTypes, type) < 0) continue;
                FieldInfo fu = kv.Value.GetType().GetField("FunctionalUnderWater", BindingFlags.Public | BindingFlags.Instance);
                if (fu == null || fu.FieldType != typeof(bool)) continue;
                configs++;
                if (revert)
                {
                    if (!WeaponOriginals.ContainsKey(kv.Value)) WeaponOriginals[kv.Value] = (bool)fu.GetValue(kv.Value);
                    fu.SetValue(kv.Value, false);
                }
                if ((bool)fu.GetValue(kv.Value)) functional++;
            }
            if (!revert) RestoreWeapons();
            return null;
        }

        private static void RestoreWeapons()
        {
            if (WeaponOriginals.Count == 0) return;
            foreach (KeyValuePair<object, bool> kv in WeaponOriginals)
            {
                try
                {
                    FieldInfo fu = kv.Key.GetType().GetField("FunctionalUnderWater", BindingFlags.Public | BindingFlags.Instance);
                    if (fu != null) fu.SetValue(kv.Key, kv.Value);
                }
                catch (Exception) { }
            }
            WeaponOriginals.Clear();
        }

        /// <summary>The world's mod list in its load order (<c>MySession.Mods</c>, the checkpoint's order the Water Mod iterates).</summary>
        private static string ModOrder()
        {
            try
            {
                var names = new List<string>();
                if (MySession.Static != null && MySession.Static.Mods != null)
                    foreach (var m in MySession.Static.Mods)
                        names.Add(!string.IsNullOrEmpty(m.FriendlyName) ? m.FriendlyName : m.Name);
                return "[" + string.Join(", ", names.ToArray()) + "]";
            }
            catch (Exception e) { return "(unreadable: " + e.GetType().Name + ")"; }
        }

        /// <summary>The Water Mod's <c>Jakaria.WaterData</c> with the pinned version (the same rule as WaterModAudio).</summary>
        private static Type FindWaterData(out string why)
        {
            why = "Jakaria.WaterData not found in any loaded assembly";
            Type best = null;
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
                if (version != WaterModClient.PinnedVersion) { why = "Jakaria.WaterData.Version=" + (version ?? "unreadable") + " (pinned " + WaterModClient.PinnedVersion + ")"; continue; }
                best = data;
            }
            if (best != null) why = null;
            return best;
        }

        private static Dictionary<string, object> Row(string ev, string why)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["t"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            d["ev"] = ev;
            d["why"] = why;
            d["tick"] = FrameWriter.LastTick;
            return d;
        }

        private static void Write(Dictionary<string, object> d)
        {
            _rows++;
            Jsonl.WriteLine(JsonText.Serialize(d));
        }

        // ============================================================================================== de.s14

        public static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "enabled", _enabled }, { "pin", Hex(S14Conventions.Hash()) }, { "pinned", S14Conventions.Hash() == S14Conventions.PinnedHash },
                { "jetpack", JetpackOn }, { "arcade", ArcadeOn }, { "gui", GuiOn }, { "reverb", ReverbOn }, { "thrusters", ThrustersOn },
                { "effect", _effectName }, { "patched", (_jetpackPatched ? "jetpack " : "") + (_playPatched ? "play " : "") + (_guiPatched ? "gui" : "") },
                { "missing", string.Join(" | ", Missing.ToArray()) }, { "mixActive", _mixActive }, { "inRoom", _inRoom }, { "realisticSound", _realistic },
                { "swimCharacter", Interlocked.Read(ref _swimCharId) != 0 }, { "silenced", Interlocked.Read(ref _silenced) },
                { "plays", Interlocked.Read(ref _plays) }, { "modifiable", Interlocked.Read(ref _modPlays) }, { "applied", Interlocked.Read(ref _applied) },
                { "delegates", Interlocked.Read(ref _delegates) }, { "guiMuffled", Interlocked.Read(ref _guiMuffled) }, { "thrusters registered", Thrusters.Count },
                { "reverbSets", _reverbSets }, { "reverbRestores", _reverbRestores }, { "rows", _rows }, { "mutants", S14SeMutants.Active },
                { "pan", PanOn }, { "cue", CueOn }, { "round4", Round4Status() },
            };
            return d;
        }

        /// <summary>
 /// <c>de.s14 {status} | {jetpack|arcade|gui|reverb|thrusters|pan|cue: on|off} | {effect: LowPassHelmet|LowPassCockpit|…|default} | {weapons: check}</c>
 /// (chat <c>/de s14 reverb off</c>); dev builds only (UserCommandRules lists no de.s14: a release build refuses it).
 /// </summary>
        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != Verb) return null;
            Dictionary<string, object> d;
            try
            {
                if (!BuildFlavor.IsDev) { d = new Dictionary<string, object>(StringComparer.Ordinal); d["ok"] = false; d["error"] = "developer command, not in the release build"; return d; }
                var a = new Dictionary<string, object>(StringComparer.Ordinal);
                if (args != null) foreach (KeyValuePair<string, object> kv in args) a[kv.Key] = kv.Value;
                object v;
                if (a.TryGetValue("argv", out v) && v is IList)
                {
                    var l = (IList)v;
                    if (l.Count >= 2) a[Convert.ToString(l[0], CultureInfo.InvariantCulture)] = Convert.ToString(l[1], CultureInfo.InvariantCulture);
                    else if (l.Count == 1) a["action"] = Convert.ToString(l[0], CultureInfo.InvariantCulture);
                }
                string error = null;
                error = Switch(a, "jetpack", ref JetpackOn) ?? error;
                error = Switch(a, "arcade", ref ArcadeOn) ?? error;
                error = Switch(a, "gui", ref GuiOn) ?? error;
                error = Switch(a, "reverb", ref ReverbOn) ?? error;
                error = Switch(a, "thrusters", ref ThrustersOn) ?? error;
                error = Switch(a, "pan", ref PanOn) ?? error;
                error = Switch(a, "cue", ref CueOn) ?? error;
                if (a.TryGetValue("effect", out v))
                {
                    var e = new Dictionary<string, object>(StringComparer.Ordinal) { { "effect", v } };
                    Dictionary<string, object> r = AudioPolish.Command(e);
                    object ok;
                    if (r.TryGetValue("ok", out ok) && ok is bool && !(bool)ok) error = Convert.ToString(r["error"], CultureInfo.InvariantCulture);
                }
                if (a.ContainsKey("weapons")) WeaponsRow("command");
                d = Status();
                d["ok"] = error == null;
                if (error != null) d["error"] = error;
                d["line"] = "DE.S14 jetpack=" + On(JetpackOn) + " arcade=" + On(ArcadeOn) + " gui=" + On(GuiOn) + " reverb=" + On(ReverbOn) + " thrusters=" + On(ThrustersOn) +
                            " effect=" + _effectName + " silenced=" + Interlocked.Read(ref _silenced) + " applied=" + Interlocked.Read(ref _applied) + "/" + Interlocked.Read(ref _modPlays) +
                            " guiMuffled=" + Interlocked.Read(ref _guiMuffled) + " thrusters=" + Thrusters.Count + " " + Round4Status() + (S14SeMutants.Active.Length > 0 ? " MUTANTS " + S14SeMutants.Active : "");
                if (error == null && (a.Count > 0 && !a.ContainsKey("action"))) DeLog.Info(Convert.ToString(d["line"], CultureInfo.InvariantCulture) + " (" + via + ")");
                return d;
            }
            catch (Exception e)
            {
                d = new Dictionary<string, object>(StringComparer.Ordinal);
                d["ok"] = false;
                d["error"] = e.GetType().Name + ": " + e.Message;
                return d;
            }
        }

        private static string On(bool b) { return b ? "on" : "off"; }

        private static string Hex(ulong v) { return "0x" + v.ToString("x16", CultureInfo.InvariantCulture); }

        private static string Switch(IDictionary<string, object> a, string key, ref bool field)
        {
            object v;
            if (!a.TryGetValue(key, out v)) return null;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            if (s != "on" && s != "off") return key + ": on|off";
            field = s == "on";
            return null;
        }
    }
}
