using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Utils;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.World;
using Sandbox.Definitions;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.Utils;
using VRage.Input;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>

 /// but in the water of the dive planet his movement is Subnautica's underwater motor, through
 /// <see cref="SwimModelAdapter"/>. Update thread only; every game call is guarded, a missing member disables only the
 /// feature that needs it (<c>DE.Swim FEATURE DISABLED …</c>, logged once) and the stock SE movement then applies.
 /// <para><b>Hook and order</b>: each session update runs the session components'
 /// <c>Simulate</c> by priority: <c>MySector</c>
 /// → <c>MyEntities.Simulate</c> → <c>MyCharacter.Simulate</c>
 /// <c>MyEntityComponentUpdater</c>
 /// → <c>MyCharacterJetpackComponent.UpdateSimulate</c> → <c>MyCharacter.UpdateMovement</c>
 /// then
 /// <c>MyPhysics</c> applies the queued forces and calls
 /// <c>SimulateInternal</c> (<c>:1090-1110</c>) which steps Havok and then <c>UpdateCharacters</c> →
 /// <c>MyCharacter.UpdatePhysicalMovement</c>. The Water Mod applies its character drag, buoyancy and swim force with
 /// immediate <c>AddForce</c> calls from its session <c>UpdateAfterSimulation</c>
 /// So the controller computes and applies the velocity in a Harmony <b>prefix on <c>MyPhysics.SimulateInternal</c></b>:
 /// the last write before the Havok step, after SE's walk/jetpack update, gravity and the queued forces, and it makes
 /// the Water Mod's forces of the previous tick irrelevant (bypassed, ROADMAP). The <b>postfix</b> reads the velocity and
 /// position the step produced (before the Water Mod touches it) for the swim rows. A <b>prefix on
 /// <c>MyCharacter.Simulate</c></b> lets a running script drive SE's own walking and jetpack out of the water (the
 /// character's <c>MoveAndRotate</c>, as the gameplay screen does).</para>
 /// <para><b>Rules</b>: no gravity and no walking in the water (the model's velocity is applied every tick while it
 /// controls it, modes Swim and Surface; SE keeps the character upright in its walking mode, <c>UpdateStandup</c>, so
 /// roll stays disabled and pitch is the head's); the swim state is the model's swim latch (entry below eye −0.1 m,
 /// exit at +0.8 m or +0.1 m grounded); jetpack and dampeners off in the water, the jetpack back with the head out
 /// (mode Fly: SE's jetpack moves the engineer out, a script's input then drives it), the previous states restored on
 /// exit (<see cref="SwimSuitPolicy"/>; the switches change the character proxy's flying state, so they are queued and
 /// applied by the plugin Update right after the tick, outside the simulation, as SE's own input handling does);
 /// oxygen at Subnautica's rate (<see cref="SwimO2Rule"/>); the model's input velocity from SE's post-step velocity
 /// (<see cref="VelocitySource"/>); the in-step effect of SE's physics on the set velocity measured and compensated
 /// (<see cref="SwimGain"/>); everything logged.
 /// Inert (stock SE movement) when the site is not bound, the activation gate is closed, off the dive planet, with
 /// <c>de.swim model=off</c>, <c>DEEPENGINEERS_SWIM=off</c> (<c>standby</c>: patches on, the model off until
 /// <c>de.swim model=on</c>), when the velocity hook is missing, or after a missing game member (logged once,
 /// FEATURE DISABLED controller).</para>
 /// <para><b>Review</b>: a seat taken while swimming parks the jetpack's remembered state until the seat is
 /// left (<see cref="SwimSuitPolicy.Parked"/>); a save while the policy holds the player's states stores those
 /// (<see cref="AfterJetpackObjectBuilder"/>); callers outside S2b go through <see cref="SwimGuard"/>.</para>
 /// </summary>
    internal static class SwimController
    {
        public const string EnvVar = "DEEPENGINEERS_SWIM";
        /// <summary>SE's simulation step (<c>MyEngineConstants.UPDATE_STEP_SIZE_IN_SECONDS</c>, 1/60 s, CONTRACT §2.1).</summary>
        public const double TickSec = 1.0 / 60.0;
        /// <summary>The chest point the model also receives: this far below the eye.</summary>
        public const double ChestBelowEyeM = 0.4;
        /// <summary>Eye height above the character origin when the head bone cannot be read.</summary>
        public const double EyeAboveFeetFallbackM = 1.7;
        /// <summary>Above this eye height (SN y) and not swimming nor scripted, nothing is computed (cheap path).</summary>
        public const double ArmBelowY = 5.0;
        public const int PlanetPollTicks = 30;
        /// <summary>The GetDepth cross-check runs with each per-second line (every 60 ticks).</summary>
        public const int DepthCheckTicks = 60;
        public const double DepthCheckTolM = 0.05;
        public const int O2RefillEveryTicks = 10;
        public const int RowVersion = 1;
        /// <summary>Without a script or <c>de.swim rows=on</c>: one row every this many ticks in the water (plus every transition).</summary>
        public const int RowIdleEveryTicks = 60;
        /// <summary><c>DEEPENGINEERS_SWIM=standby</c>: the patches are installed but the model starts off (the launcher's s2b session).</summary>
        public const string StandbyValue = "standby";

        /// <summary>The velocity sources of the model's <c>SwimInputs.Velocity</c> (<c>de.swim velocitySource=…</c>).</summary>
        public const string VelocityPostStep = "poststep", VelocityReadback = "readback", VelocityModel = "model";

        private static readonly SwimModelAdapter Model = new SwimModelAdapter();
        private static readonly SwimSuitPolicy Policy = new SwimSuitPolicy();
        private static readonly StringBuilder Sb = new StringBuilder(SwimTrace.MaxRowChars + 1024);
        private static readonly char[] RowBuf = new char[SwimTrace.MaxRowChars];
        private static readonly char[] RowOut = new char[SwimTrace.MaxRowChars + 1024];

        // configuration and features
        private static bool _configured;
        public static bool Enabled { get; private set; }
        public static bool ModelOn { get; private set; }
        public static bool HookVelocity { get; private set; }
        public static bool HookInject { get; private set; }
        private static readonly HashSet<string> Disabled = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>
 /// Where the model's input velocity comes from while it keeps control (on the tick it takes control it is always
 /// SE's current velocity): <c>poststep</c> (default) = SE's velocity right after the previous physics step, i.e.
 /// what Havok made of the imposed velocity, contacts included, before SE's character update and the Water Mod
 /// write into it again (both are overwritten by the next imposed velocity, so they never move the engineer and
 /// must not feed the model either); <c>readback</c> = the velocity at this prefix (after those writes);
 /// <c>model</c> = the model's own previous output (open loop).
 /// </summary>
        public static string VelocitySource { get; set; } = VelocityPostStep;
        /// <summary>The in-step compensation (<see cref="SwimGain"/>): <c>de.swim gain=auto|off|&lt;factor&gt;</c>.</summary>
        public static readonly SwimGain Gain = new SwimGain();
        /// <summary><c>de.swim rows=on</c>: a row every tick the model is evaluated (scripted sessions); <c>auto</c> (default): see the class remarks.</summary>
        public static bool RowsEveryTick { get; set; }
        /// <summary>The model started off because of <c>DEEPENGINEERS_SWIM=standby</c>.</summary>
        public static bool Standby { get; private set; }

        // the character
        private static MyCharacter _char;
        private static long _charId;
        private static bool _onPlanet;
        private static int _pollCountdown;

        // script
        private static SwimScript _script;
        private static int _scriptAnnounced = -1;
        private static bool _clearInjected;
        private static SwimInputs _injected;

        // pending (prefix -> postfix)
        private static bool _pending;
        private static SiteMap _pSite;
        private static ulong _pTick;
        private static string _pSrc;
        private static int _pStep;
        private static SwimInputs _pIn;
        private static string _pSeg;
        private static Vec3 _pEyeSe, _pFeetSe;
        private static double _pEyeY, _pChestY;
        private static bool _pGrounded;
        private static Vec3 _pVSet, _pVApplied;
        private static double _pGain = 1.0;
        private static SwimTickOut _pOut;
        private static bool _pJet, _pDamp, _pJetWant, _pDampWant;
        private static bool _pApplied;
        private static bool _pWriteRow;
        private static string _pVSrc = VelocityPostStep;

        // SE's velocity right after the last physics step in which the velocity was imposed (the poststep source)
        private static Vec3 _postVSe;
        private static ulong _postTick;
        private static bool _postValid;

        // last values (dump, sec line)
        private static Vec3 _lastVSe, _lastVSet, _lastEyeSn, _lastRes;
        private static double _lastEyeY = double.NaN;
        private static bool _lastJetAllowed = true;
        private static uint _lastMode;
        private static uint _lastExit;
        private static long _enters, _exits, _rows, _jetpackRefusals, _ticksSwimming;
        private static int _secCountdown;
        private static double _depthMaxDiff;
        private static long _depthChecks, _depthMismatches;
        private static RateLimit _depthLog = new RateLimit(60000);
        private static RateLimit _jetLog = new RateLimit(2000);
        private static RateLimit _errLog = new RateLimit(10000);
        private static RateLimit _saveLog = new RateLimit(10000);
        private static long _errors;

        // jetpack and dampener switches, queued here and applied by the plugin Update (outside the simulation, as SE's
 // own input handling does): TurnOnJetpack changes the character proxy's flying state
        private static MyCharacter _actChar;
        private static int _actJet = -1, _actDamp = -1;

        // S5a: fins, the O2 mirror override and the C1 swim extension (SwimWireFill; CONTRACT §5.4 "C1 swim extension")
        private static uint _fins = SwimFins.Default;
        private static double _o2Override = double.NaN;
        private static SwimWireSource _wire;
        private static SwimWireState _lastWire;
        private static double _lastMaxSpeed;

        // oxygen
        private static double _o2BaseRate = double.NaN;
        private static double _o2Wanted = double.NaN;
        private static double _o2RefillUnits;
        private static int _o2RefillTicks;
        private static double _o2RefilledLitres;
        private static bool _o2Touched;

        // ================================================================================================ config

 /// <summary>Plugin Init (before the Harmony patches): <c>DEEPENGINEERS_SWIM=off</c> leaves the controller and its patches out.</summary>
        public static void LoadConfig()
        {
            if (_configured) return;
            _configured = true;
            string v = null;
            try { v = Environment.GetEnvironmentVariable(EnvVar); } catch (Exception) { }
            Enabled = !(v != null && (v.Equals("off", StringComparison.OrdinalIgnoreCase) || v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)));
            // standby: the launcher's s2b session loads DE Lab at -3,000 m, where the model
 // would breathe the crush class (22.5 s of air) before the harness can refill; it switches the model on with
 // its first teleport
            Standby = Enabled && v != null && v.Equals(StandbyValue, StringComparison.OrdinalIgnoreCase);
            ModelOn = !Standby;
            DeLog.Info("DE.Swim config: " + (Enabled ? (Standby ? "on, STANDBY (" + EnvVar + "=" + StandbyValue + ": the model is off until de.swim model=on)" : "on")
                                                     : "OFF (" + EnvVar + "=" + v + "; no S2b patches, stock SE movement everywhere)") +
                       "; model " + SwimModelAdapter.ModelName);
        }

        /// <summary>SwimPatches reports which hooks are in place.</summary>
        public static void SetHooks(bool velocity, bool inject, bool save)
        {
            HookVelocity = velocity;
            HookInject = inject;
            if (!velocity) Disable("velocity", "MyPhysics.SimulateInternal hook missing: the swim model is inert, stock SE movement applies");
            if (!inject) Disable("script-walk", "MyCharacter.Simulate hook missing: scripts drive the swim model only, not SE's walk or jetpack out of the water");
            if (!save) Disable("save", "MyCharacterJetpackComponent.GetObjectBuilder hook missing: a save taken while swimming stores the jetpack and dampeners off");
        }

        /// <summary>Logs <c>DE.Swim FEATURE DISABLED</c> once per feature.</summary>
        public static void Disable(string feature, string why)
        {
            lock (Disabled)
            {
                if (!Disabled.Add(feature)) return;
            }
            DeLog.Info("DE.Swim FEATURE DISABLED " + feature + ": " + why);
        }

        private static bool On(string feature)
        {
            lock (Disabled) return !Disabled.Contains(feature);
        }

        private static void Disable(string feature, Exception e)
        {
            Disable(feature, e.GetType().Name + ": " + e.Message);
        }

        // ================================================================================================ queries

 /// <summary>The swimming character's entity id, 0 when nobody swims (C1 characterSwimming, FrameWriter).</summary>
        public static long SwimmingCharacterId { get { return Model.Swimming ? _charId : 0; } }

        /// <summary>The swim latch is on (modes Swim, Surface, Fly).</summary>
        public static bool IsSwimming { get { return Model.Swimming; } }

        /// <summary><c>disabled</c>, <c>model-off</c>, <c>swim</c> (the swim latch is on: mode Swim, Surface or Fly) or <c>out</c>.</summary>
        public static string StateName { get { return !Enabled ? "disabled" : !ModelOn ? "model-off" : Model.Swimming ? "swim" : "out"; } }

        /// <summary>The model's mode name (Walk, Swim, Surface, Fly).</summary>
        public static string ModeName { get { return SwimMode.Name(Model.Mode); } }

        public static bool ScriptRunning { get { return _script != null; } }

        /// <summary>The S5a fins setting (<see cref="SwimFins"/>; default ultra: +3.2 m/s on the swim model's max speed).</summary>
        public static uint Fins { get { return _fins; } }

        /// <summary>The S5a dev override of the mirrored O2 fraction (NaN = none: the suit's own level is mirrored).</summary>
        public static double O2Override { get { return _o2Override; } }

        // ================================================================================================ commands

 /// <summary><c>de.swim fins=off|basic|ultra</c>: the model's fins modifier (0 / +1.9 / +3.2 m/s), applied from the next motor step.</summary>
        public static string SetFins(string text, string via)
        {
            uint f;
            if (!SwimFins.TryParse(text, out f)) return null;
            string line = "DE.Swim fins " + SwimFins.Name(f) + " (+" + SwimText.N(SwimFins.Modifier(f)) + " m/s on the max speed" +
                          (f == _fins ? ", unchanged" : ", was " + SwimFins.Name(_fins)) + "; via " + via + ")";
            _fins = f;
            DeLog.Info(line);
            return line;
        }

        /// <summary>
 /// <c>de.swim refill=true</c>: fills the local character's suit
 /// (<c>MyCharacterOxygenComponent.SuitOxygenAmount</c> = capacity, the setter the surface refill already uses) so a
 /// scripted Survival session never drowns (the oxygen rule drains the suit like Subnautica's 45 s tank and refills
 /// only with the head out; the S6b script keeps the character under for minutes). The rule and the multiplier
 /// are untouched. Returns the log line, or null with <paramref name="why"/>.
 /// </summary>
        public static string RefillSuit(string via, out string why)
        {
            why = null;
            MyCharacter c = _char;
            if (c == null || c.Closed) { why = "no local character"; return null; }
            try
            {
                MyCharacterOxygenComponent oc = c.OxygenComponent;
                if (oc == null) { why = "no oxygen component"; return null; }
                float cap = (float)oc.OxygenCapacity, before = oc.SuitOxygenAmount;
                if (before < cap)
                {
                    oc.SuitOxygenAmount = cap;
                    _o2Touched = true;
                }
                string line = "DE.Swim refill suit " + SwimText.N(before) + " -> " + SwimText.N(cap) + " L (via " + via + ")";
                DeLog.Info(line);
                return line;
            }
            catch (Exception e) { why = e.GetType().Name + ": " + e.Message; return null; }
        }

        /// <summary>
 /// <c>de.swim o2=&lt;0..1&gt;|auto</c> (dev, S5a): forces the O2 fraction mirrored to Subnautica through C1 (the suit and
 /// the oxygen rule are untouched; the scripted s5a session drives the breathing sounds with it). Returns the log
 /// line, or null with <paramref name="why"/> on a bad value.
 /// </summary>
        public static string SetO2Override(object value, string via, out string why)
        {
            why = null;
            string line;
            string text = value as string;
            if (text != null && (text.Equals("auto", StringComparison.OrdinalIgnoreCase) || text.Equals("off", StringComparison.OrdinalIgnoreCase)))
            {
                line = "DE.Swim o2 override auto (the suit's own level is mirrored" + (double.IsNaN(_o2Override) ? ", unchanged" : ", was " + SwimText.N(_o2Override)) + "; via " + via + ")";
                _o2Override = double.NaN;
            }
            else
            {
                double v;
                if (value is double) v = (double)value;
                else if (text == null || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { why = "o2 must be a fraction 0..1, or auto"; return null; }
                if (double.IsNaN(v) || v < 0.0 || v > 1.0) { why = "o2 must be a fraction 0..1, or auto"; return null; }
                line = "DE.Swim o2 override " + SwimText.N(v) + " (mirrored to Subnautica as " + SwimWire.Fraction(v) + "/255; the SE suit is untouched; TEST ONLY; via " + via + ")";
                _o2Override = v;
            }
            DeLog.Info(line);
            return line;
        }

        /// <summary>
 /// FrameWriter, with every C1 record (every SE tick): the S5a swim extension from the controller's last tick
 /// (<see cref="SwimWireFill"/>; idle when the controller did not run a full tick: flags 0). Through <see cref="SwimGuard"/>.
 /// </summary>
        public static void FillWire(ref SeFrame f)
        {
            if (!Enabled) { SwimWire.Clear(ref f); return; }
            SwimWireSource src = _wire;
            src.Fins = _fins;
            src.O2Override = _o2Override;
            SwimWireState w = SwimWireFill.Compose(ref src);
            SwimWire.Write(ref f, ref w);
            _lastWire = w;
        }

        /// <summary><c>de.swim model=on|off</c>. Off leaves the water at once (restore) and lets SE's own movement run.</summary>
        public static string SetModel(bool on, string via)
        {
            if (!Enabled) return "DE.Swim model unchanged: the controller is off (" + EnvVar + ")";
            if (ModelOn == on) return "DE.Swim model already " + (on ? "on" : "off");
            ModelOn = on;
            if (!on && Model.Swimming)
            {
                MyCharacter c = _char;
                if (c != null && !c.Closed) Exit(c, SwimExit.ModelOff, _lastEyeY, 0, "de.swim model=off via " + via);
                else ExitWithoutCharacter(SwimExit.ModelOff);
            }
            string line = "DE.Swim model " + (on ? "ON (Subnautica's motor in the water of " + SiteV1.GeneratorSubtypeId + ")" : "OFF (stock SE movement)") + " via " + via;
            DeLog.Info(line);
            return line;
        }

        /// <summary>Starts (or with null stops) the scripted input source.</summary>
        public static string StartScript(SwimScript s, string via)
        {
            if (s == null)
            {
                if (_script == null) return "DE.Swim script stop: nothing running";
                string l = "DE.Swim script stopped at tick " + _script.Elapsed + "/" + _script.TotalTicks + " (via " + via + ")";
                EndScript();
                DeLog.Info(l);
                return l;
            }
            if (_script != null)
                DeLog.Info("DE.Swim script replaced at tick " + _script.Elapsed + "/" + _script.TotalTicks + " (via " + via + ")");
            _script = s;
            _scriptAnnounced = -1;
            _clearInjected = false; // the new script's first injection overwrites the move indicator anyway
            string line = "DE.Swim script start " + s.Describe() + " from tick ~" + (CurrentTick() + 1) +
                          " (scripted input replaces the keyboard; a movement key cancels it; via " + via + ")";
            DeLog.Info(line);
            return line;
        }

        /// <summary>
 /// After <c>de.swim teleportChar</c>: the swim state is kept and re-evaluated at the new point on the next tick
 /// (an exit there restores the jetpack and dampeners as usual); the model holds no velocity and steps afresh.
 /// </summary>
        public static void OnTeleported()
        {
            Model.RestartStep();
            _lastVSet = Vec3.Zero;
            _postValid = false;
            if (Model.Swimming) DeLog.Info("DE.Swim teleport while swimming: the state is re-evaluated at the new point next tick");
            _pollCountdown = 0;
        }

        private static void EndScript()
        {
            _script = null;
            _scriptAnnounced = -1;
            _clearInjected = true;
        }

        // ================================================================================================ plugin tick

 /// <summary>Plugin Update: forget everything when the world is gone.</summary>
        public static void PluginUpdate(bool inWorld)
        {
            if (!Enabled) return;
            if (inWorld)
            {
                FlushSuitActions();
                return;
            }
            _actChar = null;
            _actJet = _actDamp = -1;
            if (_char == null && _script == null && !Model.Swimming) return;
            if (Model.Swimming || Policy.Active) ExitWithoutCharacter(SwimExit.CharacterChanged);
            if (_script != null) { DeLog.Info("DE.Swim script aborted (left the world)"); EndScript(); }
            _char = null;
            _charId = 0;
            Model.Reset();
            _pending = false;
            _postValid = false;
        }

        // ================================================================================================ hooks

 /// <summary>
 /// Prefix of <c>MyCharacter.Simulate</c>: a running script drives SE's own walk and jetpack whenever the model
 /// does not set the velocity (out of the water, mode Fly with the head out, or the model off).
 /// </summary>
        public static void BeforeCharacterSimulate(MyCharacter c)
        {
            if (!Enabled || (_script == null && !_clearInjected)) return;
            if (!ReferenceEquals(c, _char)) return;
            try
            {
                if (_clearInjected)
                {
                    _clearInjected = false;
                    if (_injected.Any) InjectMove(c, default(SwimInputs));
                    _injected = default(SwimInputs);
                    return;
                }
                SwimScript s = _script;
                if (s == null || s.Done || (ModelOn && Model.Controls)) return;
                InjectMove(c, s.Current.Input);
            }
            catch (Exception e) { Disable("script-walk", e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InjectMove(MyCharacter c, SwimInputs i)
        {
            double x, y, z;
            SwimInputMap.ToMoveIndicator(i, out x, out y, out z);
            c.MoveAndRotate(new Vector3((float)x, (float)y, (float)z), Vector2.Zero, 0f);
            c.Sprint(i.Sprint);
            _injected = i;
        }

        /// <summary>Prefix of <c>MyPhysics.SimulateInternal</c>: one controller tick, velocity applied before the Havok step.</summary>
        public static void BeforePhysicsStep()
        {
            _pending = false;
            _wire.Active = false; // S5a: set again at the end of a full tick; idle otherwise
            if (!Enabled || !HookVelocity) return;
            Plugin p = Plugin.Instance;
            if (p == null || !p.S1Active) return;
            try { BeforeCore(); }
            catch (Exception e) { Error("before-step", e); }
        }

        /// <summary>Postfix of <c>MyPhysics.SimulateInternal</c>: the post-step velocity and position, the swim row.</summary>
        public static void AfterPhysicsStep()
        {
            if (!_pending) return;
            _pending = false;
            try { AfterCore(); }
            catch (Exception e) { Error("after-step", e); }
        }

        // ================================================================================================ the tick

        private static void BeforeCore()
        {
            MySession s = MySession.Static;
            if (s == null) return;
            MyCharacter c = s.LocalCharacter;
            long id = c != null && !c.Closed ? c.EntityId : 0;
            if (id != _charId)
            {
                if (Model.Swimming || Policy.Active) ExitWithoutCharacter(SwimExit.CharacterChanged);
                _charId = id;
                _char = id != 0 ? c : null;
                Model.Reset();
                Gain.Reset();
                _postValid = false;
                _pollCountdown = 0;
                if (id != 0) DeLog.Info("DE.Swim character " + id + " (local character; controller " + StateName + ")");
            }
            if (_char == null) { TickScriptIdle(); return; }
            c = _char;
            // a human movement key cancels a script on every path
            if (_script != null && HumanMovementKey())
            {
                DeLog.Info("DE.Swim script cancelled at tick " + _script.Elapsed + "/" + _script.TotalTicks + " (human key press)");
                EndScript();
            }

            if (c.IsDead)
            {
                if (Model.Swimming) Exit(c, SwimExit.Dead, _lastEyeY, 0, null);
                TickScriptIdle();
                return;
            }
            if (c.IsSitting)
            {
                if (Model.Swimming) Exit(c, SwimExit.Seated, _lastEyeY, 0, null);
                TickScriptIdle();
                return;
            }
            MyPhysicsBody phys = c.Physics;
            if (phys == null || !phys.Enabled || phys.CharacterProxy == null) { TickScriptIdle(); return; }

            SiteMap site = SiteBinding.Map;
            bool bound = ActivationGate.IsOpen && SiteBinding.IsOk && site != null;
            if (!bound)
            {
                if (Model.Swimming) Exit(c, SwimExit.LeftWater, _lastEyeY, 0, "site not bound or gate closed");
                ResolveParked(c, "site not bound or gate closed");
                TickScriptIdle();
                return;
            }
            bool modelOn = ModelOn;
            if (!modelOn && Model.Swimming) Exit(c, SwimExit.ModelOff, _lastEyeY, 0, null);

            Vector3D feetD = c.PositionComp.GetPosition();
            var feet = new Vec3(feetD.X, feetD.Y, feetD.Z);
            if (--_pollCountdown <= 0)
            {
                _pollCountdown = PlanetPollTicks;
                _onPlanet = OnSitePlanet(feetD);
            }
            if (!_onPlanet)
            {
                if (Model.Swimming) Exit(c, SwimExit.LeftWater, _lastEyeY, 0, "closest water is not the dive planet");
                ResolveParked(c, "off the dive planet");
                TickScriptIdle();
                return;
            }

            // eye (the first-person camera point: Subnautica's Player transform) and look
            MatrixD head;
            Vec3 eye = Eye(c, feet, site, out head);
            Vec3 eyeSn = site.SeToSn(eye);
            double eyeY = eyeSn.Y;
            bool scripted = _script != null;
            ulong tick = CurrentTick();
            // S12 hook (CONTRACT §5.4 "S12 conventions", format flood.surface
 // airtight room with a flood row the room's plane is the surface: the model takes the eye's height above it
            double roomSurfaceSnY;
            bool roomWater = S12Floods.SurfaceAt(new Vector3D(eye.X, eye.Y, eye.Z), eyeY, out roomSurfaceSnY);
            if (roomWater) eyeY -= roomSurfaceSnY;
            // S7 swim gate (CONTRACT §5.
 // cell is in an airtight room of a mirrored grid (keyed by the character's id; InteriorProbe.cs); SE walks there
            if (S7Interior.SwimGate(c, new Vector3D(eye.X, eye.Y, eye.Z), tick) && !roomWater)
            {
                if (Model.Swimming) Exit(c, SwimExit.Room, eyeY, 0, "airtight room");
                RepairStuck(c, "airtight room");
                _lastEyeY = eyeY;
                _postValid = false;
                ResolveParked(c, "airtight room");
                if (scripted) TickScriptIdle("airtight room");
                return;
            }
            // cheap path: the model off (stock SE movement; a running script then drives SE's own movement, no rows), or
 // out of the water high above the sea with no script to log
            if (!modelOn || (!Model.Swimming && !scripted && eyeY > ArmBelowY))
            {
                if (modelOn) RepairStuck(c, "out of the water");
                _lastEyeY = eyeY;
                _postValid = false;
                ResolveParked(c, modelOn ? "out of the water" : "model off");
                if (scripted) TickScriptIdle(modelOn ? "out of the water" : "model off: SE's own movement");
                return;
            }

            // inputs: the script, else SE's own input for the character, else none
            SwimInputs inp = default(SwimInputs);
            string src = "none";
            int step = -1;
            string seg = null;
            Vec3 lookF, lookR;
            if (scripted)
            {
                SwimScriptStep st = _script.Current;
                if (_script.AtStepStart && _scriptAnnounced != _script.StepIndex)
                {
                    _scriptAnnounced = _script.StepIndex;
                    DeLog.Info("DE.Swim script step " + (_script.StepIndex + 1) + "/" + _script.Steps.Length + " tick=" + tick + " " +
                               SwimScript.StepText(st) + " state=" + StateName + " mode=" + ModeName + " y=" + SwimText.N(eyeY));
                    if (st.Jetpack >= 0) ScriptJetpack(c, st.Jetpack == 1, eyeY);
                }
                inp = st.Input;
                src = "script";
                step = _script.StepIndex;
                seg = st.Label;
                SwimInputMap.LookFromYawPitch(st.LookYawDeg, st.LookPitchDeg, out lookF, out lookR);
            }
            else
            {
                bool keys = ReferenceEquals(s.ControlledEntity, c) && !ReferenceEquals(s.CameraController, MySpectatorCameraController.Static);
                if (keys && On("keys"))
                {
                    try
                    {
                        Vector3 mi = c.MoveIndicator;
                        inp = SwimInputMap.FromMoveIndicator(mi.X, mi.Y, mi.Z, c.WantsSprint);
                        src = "keys";
                    }
                    catch (Exception e) { Disable("keys", e); }
                }
                Look(c, s, site, eye, head, keys, out lookF, out lookR);
            }

            // state and velocity through the model
            bool jet = false, damp = false;
            ReadJetpack(c, out jet, out damp);
            bool jetRunning = false;
            try { jetRunning = c.JetpackRunning; } catch (Exception) { }
            Vector3 vNow = phys.LinearVelocity;
            Vec3 vNowSn = SwimModelAdapter.SeToSnVelocity(site, eye, new Vec3(vNow.X, vNow.Y, vNow.Z));
            string vsrc = VelocitySource;
            Vec3 vIn = vNowSn;
            if (Model.Controls)
            {
                if (vsrc == VelocityModel) vIn = _lastVSet;
                else if (vsrc == VelocityPostStep && _postValid && _postTick + 1 == tick) vIn = _postVSe;
                else vsrc = VelocityReadback;
            }
            else vsrc = VelocityReadback; // taking control: SE's own current velocity (a dive, a fall into the water)
            _postValid = false;
            bool grounded = false;
            try { grounded = phys.CharacterProxy.Supported; } catch (Exception) { }

            bool onLadder = false, onGrid = false;
            try { onLadder = c.IsOnLadder; } catch (Exception) { }
            if (grounded) onGrid = SupportIsGrid(phys.CharacterProxy);
            var t = new SwimTickIn
            {
                Tick = tick,
                Forward = inp.Forward,
                Strafe = inp.Strafe,
                Vertical = inp.Vertical,
                Sprint = inp.Sprint,
                ToolHeld = ToolHeld(c),
                LookForwardSn = lookF,
                LookRightSn = lookR,
                EyeY = eyeY,
                EyeSn = eyeSn,
                ChestY = eyeY - ChestBelowEyeM,
                Grounded = grounded,
                JetpackOn = jetRunning,
                OnLadder = onLadder,
                OnGrid = onGrid,
                VelocitySn = vIn,
                OxygenUnits = SuitOxygenUnits(c),
                Mutants = S2bMutants.ModelBits,
                FinsModifier = SwimFins.Modifier(_fins),
                FinsNotApplied = S5aMutants.FinsNotApplied,
            };
            SwimTickOut o = Model.Tick(ref t);
            if (o.Stepped) _lastMaxSpeed = o.MaxSpeed;
            if (o.Yield != _lastYield)
            {
                if (o.Yield != SwimYield.None) Yields++;
                DeLog.Info("DE.Swim yield " + SwimYield.Name(o.Yield) + (o.Yield == SwimYield.None ? " (the model moves the diver again)" : ": SE moves the character (" +
                           (o.Yield == SwimYield.Ladder ? "the ladder" : "walking on the deck, boots on") + "); the oxygen rule stays") + " tick=" + tick + " y=" + SwimText.N(eyeY));
                _lastYield = o.Yield;
            }

            if (o.Transition > 0) Enter(c, eyeY, vNowSn.Y, jet, damp, src);
            else if (o.Transition < 0) Exit(c, o.ExitReason, eyeY, vNowSn.Y, null);
            if (Policy.Parked)
            {
                // the seat taken while swimming has been left
                if (o.Swimming)
                {
                    Policy.Unpark();
                    DeLog.Info("DE.Swim seat left in the water: the swim goes on with jetpackWas=" + (Policy.JetpackWas ? "on" : "off") +
                               " dampenersWas=" + (Policy.DampenersWas ? "on" : "off"));
                }
                else ResolveParked(c, "out of the water");
            }

            bool jet0 = jet, damp0 = damp;
            bool applied = false;
            double gain = 1.0;
            Vec3 vApplied = Vec3.Zero;
            if (o.Swimming)
            {
                _ticksSwimming++;
                SuitRules(c, o, eyeY, ref jet, ref damp);
                if (o.ControlsVelocity)
                {
                    try
                    {
                        gain = Gain.Gain;
                        vApplied = o.VelocitySn * gain;
                        Vec3 vSe = SwimModelAdapter.SnToSeVelocity(site, eye, vApplied);
                        phys.LinearVelocity = new Vector3((float)vSe.X, (float)vSe.Y, (float)vSe.Z);
                        applied = true;
                        _lastVSet = o.VelocitySn;
                    }
                    catch (Exception e)
                    {
                        // leave the water cleanly (restore the jetpack, dampeners and oxygen), then stay inert: stock SE movement
                        Disable("velocity", e);
                        Exit(c, SwimExit.ModelOff, eyeY, vNowSn.Y, "velocity write failed");
                        HookVelocity = false;
                        o.Swimming = false;
                    }
                }
                if (o.Swimming) Oxygen(c, o);
            }
            _lastJetAllowed = o.JetpackAllowed;
            _lastMode = o.Mode;

            // remember for the post-step read (every tick in the water or scripted, at each transition, or with rows=on:
 // the poststep velocity source and the compensation need it) and decide whether this tick writes a row
            bool rowsOn = RowsEveryTick;
            _pending = o.Swimming || scripted || o.Transition != 0 || rowsOn;
            _pWriteRow = rowsOn || scripted || o.Transition != 0 || (o.Swimming && tick % (ulong)RowIdleEveryTicks == 0);
            _pSite = site;
            _pTick = tick;
            _pSrc = src;
            _pStep = step;
            _pSeg = seg;
            _pIn = inp;
            _pEyeSe = eye;
            _pFeetSe = feet;
            _pEyeY = eyeY;
            _pChestY = t.ChestY;
            _pGrounded = grounded;
            _pVSet = o.VelocitySn;
            _pOut = o;
            _pJet = jet0;        // SE's state at the start of this tick
            _pDamp = damp0;
            _pJetWant = jet;     // the swim rule's decision (applied by the plugin Update right after this tick)
            _pDampWant = damp;
            _pApplied = applied;
            _pVApplied = vApplied;
            _pGain = gain;
            _pVSrc = vsrc;
            _lastEyeY = eyeY;

            // S5a: the C1 swim extension's source (FillWire composes it with every C1 record)
            _wire.Active = true;
            _wire.Latched = o.Swimming;
            _wire.Mode = o.Mode;
            _wire.Controls = o.ControlsVelocity && applied;
            _wire.Grounded = grounded;
            _wire.JetpackAllowed = o.JetpackAllowed;
            _wire.EyeY = eyeY;
            _wire.Forward = inp.Forward;
            _wire.Strafe = inp.Strafe;
            _wire.Vertical = inp.Vertical;
            _wire.Velocity = applied ? o.VelocitySn : vNowSn;
            _wire.LastMaxSpeed = _lastMaxSpeed;
            _wire.Fins = _fins;
            _wire.O2Fraction = double.IsNaN(t.OxygenUnits) ? double.NaN : t.OxygenUnits / SwimModelAdapter.TankSeconds;
            _wire.O2Override = _o2Override;
            _wire.DepthClass = o.DepthClass;

            if (_script != null) AdvanceScript(eyeY, tick);
        }

        /// <summary>Consumes the script tick that just ran (the script's own clock: one tick per SE simulation step).</summary>
        private static void AdvanceScript(double eyeY, ulong tick)
        {
            if (_script == null) return;
            _script.Advance();
            if (_script.Done)
            {
                DeLog.Info("DE.Swim script done after " + _script.Elapsed + " ticks (" + SwimText.N(_script.Elapsed * TickSec) + " s) at tick " + tick +
                           " state=" + StateName + " mode=" + ModeName + " y=" + SwimText.N(eyeY));
                EndScript();
            }
        }

        private static void AfterCore()
        {
            MyCharacter c = _char;
            SiteMap site = _pSite;
            if (c == null || c.Closed || site == null) return;
            MyPhysicsBody phys = c.Physics;
            if (phys == null) return;
            Vector3D feetD = c.PositionComp.GetPosition();
            var feet = new Vec3(feetD.X, feetD.Y, feetD.Z);
            Vec3 eye = _pEyeSe + (feet - _pFeetSe);
            Vec3 eyeSn = site.SeToSn(eye);
            Vector3 v = phys.LinearVelocity;
            Vec3 vSe = SwimModelAdapter.SeToSnVelocity(site, eye, new Vec3(v.X, v.Y, v.Z));
            _lastVSe = vSe;
            _lastEyeSn = eyeSn;
            _lastRes = _pApplied ? vSe - _pVSet : Vec3.Zero;
            if (_pApplied)
            {
                Gain.Observe(_pVApplied, vSe, _pGrounded);
                _postVSe = vSe;
                _postTick = _pTick;
                _postValid = true;
            }
            bool swimming = _pOut.Swimming;

            if (_pWriteRow) WriteRow(c, eyeSn, vSe);

            if (--_secCountdown <= 0)
            {
                _secCountdown = 60;
                double o2 = double.NaN;
                try { MyCharacterOxygenComponent oc = c.OxygenComponent; if (oc != null) o2 = oc.SuitOxygenLevel; } catch (Exception) { }
                double gd = DepthCheck(site, eye, eyeSn.Y);
                DeLog.Info("DE.Swim sec state=" + (swimming ? "swim" : "out") + " mode=" + SwimMode.Name(_pOut.Mode) + " tick=" + _pTick +
                           " v=" + SwimText.V(vSe) + " speed=" + SwimText.N(vSe.Length) + " y=" + SwimText.N(eyeSn.Y) + " o2=" + SwimText.N(o2) + " bottle=" + (_bottle ? 1 : 0) +
                           " inputs=" + _pIn + " src=" + _pSrc + (_pStep >= 0 ? " step=" + (_pStep + 1) : "") +
                           " jet=" + (_pJet ? 1 : 0) + " damp=" + (_pDamp ? 1 : 0) + " grounded=" + (_pGrounded ? 1 : 0) +
                           " depthClass=" + SwimModelAdapter.DepthClassName(_pOut.DepthClass) + " res=" + SwimText.V(_lastRes) +
                           " vsrc=" + _pVSrc + " comp=" + Gain.Describe() +
                           (double.IsNaN(gd) ? "" : " getDepthDiff=" + SwimText.N(gd)) +
                           (S2bMutants.Active.Length > 0 ? " MUTANTS=" + S2bMutants.Active : ""));
            }
        }

        // ================================================================================================ transitions

        private static void Enter(MyCharacter c, double eyeY, double vy, bool jet, bool damp, string src)
        {
            _enters++;
            if (!Policy.Active) Policy.OnEnter(jet, damp); // a re-entry without a recorded exit keeps the first states
            _o2BaseRate = double.NaN;
            _o2Wanted = double.NaN;
            _o2RefillUnits = 0;
            _o2RefillTicks = 0;
            _secCountdown = 0;
            DeLog.Info("DE.Swim enter tick=" + CurrentTick() + " y=" + SwimText.N(eyeY) + " chestY=" + SwimText.N(eyeY - ChestBelowEyeM) +
                       " vy=" + SwimText.N(vy) + " jetpackWas=" + (Policy.JetpackWas ? "on" : "off") + " dampenersWas=" + (Policy.DampenersWas ? "on" : "off") +
                       " src=" + src + (S2bMutants.Active.Length > 0 ? " MUTANTS=" + S2bMutants.Active : ""));
        }

        private static void Exit(MyCharacter c, uint reason, double eyeY, double vy, string note)
        {
            _exits++;
            _lastExit = reason;
            if (Model.Swimming) Model.ForceOut(); // the controller's own exits; a model exit has already turned the latch off
            _postValid = false;
            SwimRestore r = Policy.OnExit(reason, S2bMutants.JetpackLeftOn);
            if (r.SetJetpack) SetJetpack(c, r.JetpackTo);
            if (r.SetDampeners) SetDampeners(c, r.DampenersTo);
            if (r.RestoreO2Multiplier) RestoreO2(c);
            else _o2Touched = false; // dead or another character: that suit is gone
            EnsureProxyFlying(c, false);
            _secCountdown = 0;
            DeLog.Info("DE.Swim exit tick=" + CurrentTick() + " y=" + SwimText.N(eyeY) + " vy=" + SwimText.N(vy) + " reason=" + SwimExit.Name(reason) +
                       " " + r + (note != null ? " (" + note + ")" : ""));
        }

        /// <summary>
 /// The engineer left the seat he took while swimming and is not in the water: the remembered jetpack and
 /// dampeners, after the cockpit's own restore of its copy (the swim's forced off). Review finding 9.
 /// </summary>
        private static void ResolveParked(MyCharacter c, string why)
        {
            if (!Policy.Parked || c == null) return;
            SwimRestore r = Policy.LeaveSeatOutOfWater(S2bMutants.JetpackLeftOn);
            if (r.SetJetpack) SetJetpack(c, r.JetpackTo);
            if (r.SetDampeners) SetDampeners(c, r.DampenersTo);
            DeLog.Info("DE.Swim seat left out of the water (" + why + "): " + r);
        }

        private static void ExitWithoutCharacter(uint reason)
        {
            _exits++;
            _lastExit = reason;
            if (Model.Swimming) Model.ForceOut();
            _postValid = false;
            Policy.OnExit(SwimExit.CharacterChanged, S2bMutants.JetpackLeftOn);
            _o2Touched = false;
            DeLog.Info("DE.Swim exit tick=" + CurrentTick() + " reason=" + SwimExit.Name(reason) + " (character gone: nothing restored)");
        }

        // ================================================================================================ suit rules

        private static void SuitRules(MyCharacter c, SwimTickOut o, double eyeY, ref bool jet, ref bool damp)
        {
            if (!On("jetpack")) return;
            if (Policy.MustSwitchJetpackOff(jet, o.JetpackAllowed, S2bMutants.JetpackLeftOn))
            {
                SetJetpack(c, false);
                jet = false;
                _jetpackRefusals++;
                if (_jetLog.Allow(Win32.TickCount64()))
                {
                    DeLog.Info("DE.Swim jetpack off: head under the surface (y=" + SwimText.N(eyeY) + "); it comes back with the head out");
                    Notify("Jetpack unavailable underwater", 2000);
                }
            }
            if (Policy.MustSwitchDampenersOff(damp))
            {
                SetDampeners(c, false);
                damp = false;
            }

            //: not while the model yields (a ladder climbs, a deck walks: SE's own states, Falling first so it lands)
            EnsureProxyFlying(c, o.Yield == SwimYield.None && (Policy.ProxyFlyingWanted(o.JetpackAllowed, S2bMutants.JetpackLeftOn) || jet));
        }

        public static long Yields;
        private static uint _lastYield;
        private static readonly List<MyEntity> _supports = new List<MyEntity>(4);

        /// <summary>The character proxy's support is a cube grid (deck, hull, floor of a ship); voxels and nothing never count.</summary>
        private static bool SupportIsGrid(MyCharacterProxy proxy)
        {
            if (proxy == null) return false;
            try
            {
                _supports.Clear();
                proxy.GetSupportingEntities(_supports);
                for (int i = 0; i < _supports.Count; i++)
                {
                    MyEntity e = _supports[i];
                    if (e is MyCubeGrid) return true;
                    if (e != null && e.Parent is MyCubeGrid) return true;   // a block entity reported as the support
                }
            }
            catch (Exception) { }
            finally { _supports.Clear(); }
            return false;
        }

        public static long ProxyFlyingSets;
        public static long StuckRepairs;
        private static int _stuckTicks;

        /// <summary>

 /// recover"): the model is not swimming here (an airtight room, or out of the water), yet the character is in SE's Flying
 /// movement state with the jetpack off, standing on something: the proxy's flying state outlived a quick enter/exit around
 /// a room recompute. SE never leaves Flying by itself without the jetpack; a jetpack toggle does. After ten ticks of that,
 /// the proxy's flying state goes off and the character is set Falling (SE lands it on the next support contact).
 /// </summary>
        private static void RepairStuck(MyCharacter c, string where)
        {
            if (!On("flying") || c == null || c.Closed) return;
            try
            {
                if (c.JetpackRunning || c.GetCurrentMovementState() != MyCharacterMovementEnum.Flying) { _stuckTicks = 0; return; }
                MyPhysicsBody phys = c.Physics;
                MyCharacterProxy proxy = phys != null ? phys.CharacterProxy : null;
                if (proxy == null) return;
                if (++_stuckTicks < 10) return;
                _stuckTicks = 0;
                proxy.EnableFlyingState(false);
                c.SetCurrentMovementState(MyCharacterMovementEnum.Falling);
                StuckRepairs++;
                ProxyFlyingSets++;
                DeLog.Info("DE.Swim stuck repair #" + StuckRepairs + " (" + where + "): Flying with the jetpack off while the model is not swimming; the proxy's flying state off, Falling");
            }
            catch (Exception e) { Disable("flying", e); }
        }

        /// <summary>
 /// The character proxy's flying state (what <c>MyCharacterJetpackComponent.TurnOnJetpack</c> sets:
 /// <c>MyCharacterProxy.EnableFlyingState(bool)</c>, idempotent, plus the Flying movement state). Wanted while the swim
 /// latch holds the head under (no walking on the seabed) or whenever the jetpack runs; else off, SE's own default.
 /// </summary>
        private static void EnsureProxyFlying(MyCharacter c, bool wanted)
        {
            if (!On("flying") || c == null || c.Closed) return;
            try
            {
                MyPhysicsBody phys = c.Physics;
                MyCharacterProxy proxy = phys != null ? phys.CharacterProxy : null;
                if (proxy == null) return;
                bool jet = c.JetpackRunning;
                if (wanted && !jet)
                {
                    if (c.GetCurrentMovementState() != MyCharacterMovementEnum.Flying)
                    {
                        proxy.EnableFlyingState(true);           // no-op inside SE when already enabled
                        c.SetCurrentMovementState(MyCharacterMovementEnum.Flying);
                        ProxyFlyingSets++;
                    }
                }
                else if (!wanted && !jet && c.GetCurrentMovementState() == MyCharacterMovementEnum.Flying)
                {
                    proxy.EnableFlyingState(false);
                    c.SetCurrentMovementState(MyCharacterMovementEnum.Falling);   // SE lands it on the next support contact
                    ProxyFlyingSets++;
                }
            }
            catch (Exception e) { Disable("flying", e); }
        }

        private static void ReadJetpack(MyCharacter c, out bool jet, out bool damp)
        {
            jet = false;
            damp = false;
            if (!On("jetpack")) return;
            try { ReadJetpackCore(c, out jet, out damp); }
            catch (Exception e) { Disable("jetpack", e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadJetpackCore(MyCharacter c, out bool jet, out bool damp)
        {
            MyCharacterJetpackComponent j = c.JetpackComp;
            jet = j != null && j.TurnedOn;
            damp = j != null && j.DampenersTurnedOn;
        }

        /// <summary>Queues a jetpack switch for the plugin Update (the last request of a tick wins).</summary>
        private static void SetJetpack(MyCharacter c, bool on)
        {
            if (!On("jetpack")) return;
            if (!ReferenceEquals(_actChar, c)) { _actChar = c; _actDamp = -1; }
            _actJet = on ? 1 : 0;
        }

        /// <summary>Plugin Update: the queued jetpack and dampener switches, on SE's update thread outside the simulation.</summary>
        private static void FlushSuitActions()
        {
            MyCharacter c = _actChar;
            if (c == null) return;
            int j = _actJet, d = _actDamp;
            _actChar = null;
            _actJet = _actDamp = -1;
            if (c.Closed || c.MarkedForClose || c.IsDead || !On("jetpack")) return;
            try
            {
                if (j >= 0) SetJetpackCore(c, j == 1);
                if (d >= 0) SetDampenersCore(c, d == 1);
            }
            catch (Exception e) { Disable("jetpack", e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SetJetpackCore(MyCharacter c, bool on)
        {
            MyCharacterJetpackComponent j = c.JetpackComp;
            if (j != null && j.TurnedOn != on) j.TurnOnJetpack(on, false, false);
        }

        /// <summary>Queues a dampener switch for the plugin Update.</summary>
        private static void SetDampeners(MyCharacter c, bool on)
        {
            if (!On("jetpack")) return;
            if (!ReferenceEquals(_actChar, c)) { _actChar = c; _actJet = -1; }
            _actDamp = on ? 1 : 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SetDampenersCore(MyCharacter c, bool on)
        {
            MyCharacterJetpackComponent j = c.JetpackComp;
            if (j != null && j.DampenersTurnedOn != on) j.EnableDampeners(on);
        }

        /// <summary>A script step's jetpack request: what a press of SE's jetpack key would do (the swim rule may refuse it).</summary>
        private static void ScriptJetpack(MyCharacter c, bool on, double eyeY)
        {
            DeLog.Info("DE.Swim script jetpack request " + (on ? "on" : "off") + " at y=" + SwimText.N(eyeY) +
                       (on && Model.Swimming && eyeY <= 0 && !S2bMutants.JetpackLeftOn ? " (head under: the swim rule switches it off again)" : ""));
            SetJetpack(c, on);
        }

        // ================================================================================================ oxygen

        private static void Oxygen(MyCharacter c, SwimTickOut o)
        {
            if (S2bMutants.OxygenRuleOff)
            {
                if (_o2Touched) RestoreO2(c);
                return;
            }
            if (!On("oxygen")) return;
            try { OxygenCore(c, o); }
            catch (Exception e) { Disable("oxygen", e); }
        }

        private static bool _bottle, _bottleLogged, _bottleLoggedOnce;
        private static long _bottleCheckedTs;
        private static FieldInfo _gasLevelField;
        private static Type _gasLevelType;

        /// scanned once per second.</summary>
        public static bool BottleWithGas(MyCharacter c)
        {
            long now = Stopwatch.GetTimestamp();
            if (_bottleCheckedTs != 0 && now - _bottleCheckedTs < Stopwatch.Frequency) return _bottle;
            _bottleCheckedTs = now;
            bool found = false;
            try
            {
                MyInventory inv = c.GetInventory(0);
                MyDefinitionManager defs = MyDefinitionManager.Static;
                if (inv != null && defs != null)
                {
                    int count = inv.GetItemsCount();
                    for (int k = 0; k < count && !found; k++)
                    {
                        MyPhysicalInventoryItem? it = inv.GetItemByIndex(k);
                        if (!it.HasValue || it.Value.Content == null) continue;
                        var bottle = defs.GetPhysicalItemDefinition(it.Value.Content) as MyOxygenContainerDefinition;
                        if (bottle == null || bottle.StoredGasId != MyCharacterOxygenComponent.OxygenId) continue;
                        found = GasLevel(it.Value.Content) > 0.001f;
                    }
                }
            }
            catch (Exception) { found = false; }
            _bottle = found;
            return found;
        }

        /// <summary><c>MyObjectBuilder_GasContainerObject.GasLevel</c> (public float field), read by reflection and cached by type: no object-build pass assembly reference.</summary>
        private static float GasLevel(object content)
        {
            Type t = content.GetType();
            if (!ReferenceEquals(t, _gasLevelType))
            {
                _gasLevelType = t;
                _gasLevelField = t.GetField("GasLevel", BindingFlags.Instance | BindingFlags.Public);
            }
            if (_gasLevelField == null) return 1f;   // unknown shape: the bottle counts as full (SE's own refill decides)
            object v = _gasLevelField.GetValue(content);
            return v is float ? (float)v : 1f;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void OxygenCore(MyCharacter c, SwimTickOut o)
        {
            MyCharacterOxygenComponent oc = c.OxygenComponent;
            if (oc == null) return;
            float cur = oc.OxygenConsumptionRateMutliplier;
            Policy.RememberO2Multiplier(cur);
            if (double.IsNaN(_o2BaseRate))
            {
                // the definition's rate
 // OxygenConsumptionRate = that x OxygenConsumptionRateMutliplier
                var def = c.Definition;
                _o2BaseRate = def != null ? (double)def.OxygenConsumption * def.OxygenConsumptionMultiplier : double.NaN;
            }
            double cap = oc.OxygenCapacity;
            double tank = SwimModelAdapter.TankSeconds;
            double wanted = SwimO2Rule.Multiplier(o.O2UseUnitsPerSec, tank, cap, _o2BaseRate);

            bool bottle = BottleWithGas(c);
            if (bottle) wanted = SwimO2Rule.WantedMultiplier(true, Policy.HaveO2Multiplier ? Policy.O2MultiplierWas : 1.0, wanted);
            if (bottle != _bottleLogged || !_bottleLoggedOnce)
            {
                _bottleLogged = bottle;
                _bottleLoggedOnce = true;
                DeLog.Info("DE.Swim o2 tank: " + (bottle ? "SE's own suit timing (an oxygen bottle with gas is carried; depth factor off)"
                                                          : "Subnautica's " + SwimText.N(tank) + " s tank (no oxygen bottle with gas)"));
            }
            if (SwimO2Rule.NeedsWrite(cur, wanted))
            {
                oc.OxygenConsumptionRateMutliplier = (float)wanted;
                _o2Touched = true;
                try { c.SinkComp.Update(); } catch (Exception) { }
                if (SwimO2Rule.NeedsWrite(_o2Wanted, wanted))
                    DeLog.Info("DE.Swim o2 multiplier " + SwimText.N(cur) + " -> " + SwimText.N(wanted) + " (" + SwimModelAdapter.DepthClassName(o.DepthClass) +
                               ": " + SwimText.N(o.O2UseUnitsPerSec) + " units/s of a " + SwimText.N(tank) + " s tank = " +
                               SwimText.N(SwimO2Rule.FractionPerSecond(o.O2UseUnitsPerSec, tank) * cap) + " L/s of " + SwimText.N(cap) + " L)");
                _o2Wanted = wanted;
            }
            if (o.O2RefillUnitsPerSec > 0)
            {
                _o2RefillUnits += o.O2RefillUnitsPerSec * TickSec;
                if (++_o2RefillTicks >= O2RefillEveryTicks)
                {
                    double litres = SwimO2Rule.RefillLitres(_o2RefillUnits / (O2RefillEveryTicks * TickSec), tank, cap, O2RefillEveryTicks * TickSec);
                    _o2RefillUnits = 0;
                    _o2RefillTicks = 0;
                    float amount = oc.SuitOxygenAmount;
                    if (amount < cap && litres > 0)
                    {
                        float next = (float)Math.Min(cap, amount + litres);
                        oc.SuitOxygenAmount = next;
                        _o2RefilledLitres += next - amount;
                        _o2Touched = true;
                    }
                }
            }
            else
            {
                _o2RefillUnits = 0;
                _o2RefillTicks = 0;
            }
        }

        private static void RestoreO2(MyCharacter c)
        {
            if (!_o2Touched) return;
            _o2Touched = false;
            if (!On("oxygen")) return;
            try { RestoreO2Core(c); }
            catch (Exception e) { Disable("oxygen", e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RestoreO2Core(MyCharacter c)
        {
            MyCharacterOxygenComponent oc = c.OxygenComponent;
            if (oc == null) return;
            float to = Policy.HaveO2Multiplier ? Policy.O2MultiplierWas : 1f;
            if (oc.OxygenConsumptionRateMutliplier != to)
            {
                DeLog.Info("DE.Swim o2 multiplier restored " + SwimText.N(oc.OxygenConsumptionRateMutliplier) + " -> " + SwimText.N(to));
                oc.OxygenConsumptionRateMutliplier = to;
                try { c.SinkComp.Update(); } catch (Exception) { }
            }
            _o2Wanted = double.NaN;
            _o2BaseRate = double.NaN;
        }

        // ================================================================================================ geometry

 /// <summary>The eye (head bone, first-person camera point) in SE world; feet + up × 1.7 m when it cannot be read.</summary>
        private static Vec3 Eye(MyCharacter c, Vec3 feet, SiteMap site, out MatrixD head)
        {
            head = MatrixD.Identity;
            if (On("head"))
            {
                try
                {
                    head = HeadMatrix(c);
                    Vector3D t = head.Translation;
                    var e = new Vec3(t.X, t.Y, t.Z);
                    // without a head bone GetHeadMatrix returns the character origin
                    if (t.IsValid() && (e - feet).Length > 0.5) return e;
                    if (t.IsValid()) Disable("head", "no head bone (GetHeadMatrix returned the character origin): eye = origin + 1.7 m along the planet's up");
                }
                catch (Exception e) { Disable("head", e); }
            }
            Vec3 up = (feet - site.C).Normalized();
            head = MatrixD.Identity;
            return feet + up * EyeAboveFeetFallbackM;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static MatrixD HeadMatrix(MyCharacter c)
        {
            // MyCharacter.GetHeadMatrix(bool includeY, bool includeX, bool forceHeadAnim, bool forceHeadBone,
 // bool preferLocalOverSync): the head bone with the head's yaw and pitch, world space;
 // the first-person view matrix uses the same call
            return c.GetHeadMatrix(true, true, false, true, true);
        }

        /// <summary>
 /// The look: SE's camera whenever the character is the camera controller (first or third person), else the head.

 /// (backward moved the character up, the strafe keys ran along a fixed rail while third person was fine), so first
 /// person no longer trusts <c>GetHeadMatrix(forceHeadBone)</c> for the direction; the camera is what the player sees.
 /// </summary>
        private static void Look(MyCharacter c, MySession s, SiteMap site, Vec3 eye, MatrixD head, bool keys, out Vec3 f, out Vec3 r)
        {
            Vector3D fw = head.Forward, rw = head.Right;
            if (keys && On("camera"))
            {
                try
                {
                    MatrixD body;
                    if (ReferenceEquals(s.CameraController, c) && S13Se.SwimBasis(c, CurrentTick(), out body)) { fw = body.Forward; rw = body.Right; } // S13 hook (point 5): third person swims along the body's own look (body yaw, head pitch), not the camera's
                    else if (ReferenceEquals(s.CameraController, c))
                    {
                        MyCamera cam = MySector.MainCamera;
                        if (cam != null)
                        {
                            MatrixD w = cam.WorldMatrix;
                            fw = w.Forward;
                            rw = w.Right;
                        }
                    }
                }
                catch (Exception e) { Disable("camera", e); }
            }
            f = SwimModelAdapter.SeToSnDirection(site, eye, new Vec3(fw.X, fw.Y, fw.Z));
            r = SwimModelAdapter.SeToSnDirection(site, eye, new Vec3(rw.X, rw.Y, rw.Z));
            if (f.LengthSquared < 0.5) f = Vec3.UnitZ;
            if (r.LengthSquared < 0.5) r = Vec3.UnitX;
        }

        private static bool OnSitePlanet(Vector3D p)
        {
            WaterModClient api = SiteBinding.Api;
            MyPlanet planet = SiteBinding.Planet;
            if (api == null || planet == null) return false;
            MyPlanet closest = api.ClosestWater(p);
            return closest != null && ReferenceEquals(closest, planet);
        }

        /// <summary>GetDepth (Water Mod API) against the map's y at the eye, once per second line (S1 cross-check).</summary>
        private static double DepthCheck(SiteMap site, Vec3 eye, double y)
        {
            WaterModClient api = SiteBinding.Api;
            MyPlanet planet = SiteBinding.Planet;
            if (api == null || planet == null) return double.NaN;
            float? d = api.Depth(new Vector3D(eye.X, eye.Y, eye.Z), planet);
            if (!d.HasValue) return double.NaN;
            double diff = d.Value - y;
            _depthChecks++;
            if (Math.Abs(diff) > _depthMaxDiff) _depthMaxDiff = Math.Abs(diff);
            if (Math.Abs(diff) > DepthCheckTolM)
            {
                _depthMismatches++;
                if (_depthLog.Allow(Win32.TickCount64()))
                    DeLog.Info("DE.Swim depth MISMATCH y=" + SwimText.N(y) + " GetDepth=" + SwimText.N(d.Value) + " diff=" + SwimText.N(diff) +
                               " (> " + DepthCheckTolM + " m; at most once a minute)");
            }
            return diff;
        }

        // ================================================================================================ input helpers

 /// <summary>A human pressing a movement key (cancels a script): SE's own controls, read from MyInput.</summary>
        private static bool HumanMovementKey()
        {
            if (!On("cancel")) return false;
            try { return HumanMovementKeyCore(); }
            catch (Exception e) { Disable("cancel", e); return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool HumanMovementKeyCore()
        {
            IMyInput i = MyInput.Static;
            if (i == null) return false;
            return i.IsGameControlPressed(MyControlsSpace.FORWARD) || i.IsGameControlPressed(MyControlsSpace.BACKWARD) ||
                   i.IsGameControlPressed(MyControlsSpace.STRAFE_LEFT) || i.IsGameControlPressed(MyControlsSpace.STRAFE_RIGHT) ||
                   i.IsGameControlPressed(MyControlsSpace.JUMP) || i.IsGameControlPressed(MyControlsSpace.CROUCH) ||
                   i.IsGameControlPressed(MyControlsSpace.SPRINT);
        }

        /// <summary>Consumes a script tick while the controller cannot act (no character, seated, off the planet, model off).</summary>
        private static void TickScriptIdle()
        {
            TickScriptIdle("controller idle: not on the dive planet's water, seated, dead or no character");
        }

        private static void TickScriptIdle(string why)
        {
            if (_script == null) return;
            if (_script.AtStepStart && _scriptAnnounced != _script.StepIndex)
            {
                _scriptAnnounced = _script.StepIndex;
                DeLog.Info("DE.Swim script step " + (_script.StepIndex + 1) + "/" + _script.Steps.Length + " tick=" + CurrentTick() + " " +
                           SwimScript.StepText(_script.Current) + " (" + why + ")");
                MyCharacter c = _char;
                if (_script.Current.Jetpack >= 0 && c != null && !c.Closed) ScriptJetpack(c, _script.Current.Jetpack == 1, _lastEyeY);
            }
            _script.Advance();
            if (_script.Done)
            {
                DeLog.Info("DE.Swim script done after " + _script.Elapsed + " ticks (" + why + " at the end)");
                EndScript();
            }
        }

        // ================================================================================================ output

 /// <summary>
 /// One <c>swim</c> row (CONTRACT §2.10 "Telemetry"): the Wire <see cref="SwimTrace"/> row of this tick's model step
 /// (exact bits, replayed by tests/s2b/swim_replay.py) with the SE keys of <see cref="SwimRowExtras"/>.
 /// </summary>
        private static void WriteRow(MyCharacter c, Vec3 eyeSn, Vec3 vSe)
        {
            var x = new SwimRowExtras
            {
                UtcTicks = DateTime.UtcNow.Ticks,
                SiteVersion = SiteV1.SiteVersion,
                Dt = TickSec,
                Src = _pSrc,
                Step = _pStep,
                VelocitySource = _pVSrc,
                Exit = _pOut.Transition < 0 ? SwimExit.Name(_pOut.ExitReason) : "",
                ChestY = _pChestY,
                Applied = _pApplied,
                Gain = _pGain,
                VApplied = _pVApplied,
                VSe = vSe,
                PosAfter = eyeSn,
                JetWant = _pJetWant,
                Damp = _pDamp,
                DampWant = _pDampWant,
                O2Use = _pOut.O2UseUnitsPerSec,
                O2Refill = _pOut.O2RefillUnitsPerSec,
            };
            if (On("oxygen"))
            {
                try { SuitRow(c, ref x); }
                catch (Exception) { x.HaveSuit = false; }
            }
            // no string per row: the build pass chars go to the log through a reused buffer
            if (!Model.AppendRow(RowBuf, _pSeg, ref x, Sb)) return;
            int n = Sb.Length;
            if (n > RowOut.Length) return;
            Sb.CopyTo(0, RowOut, 0, n);
            Jsonl.WriteLineBuffered(RowOut, n);
            _rows++;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SuitRow(MyCharacter c, ref SwimRowExtras x)
        {
            MyCharacterOxygenComponent oc = c.OxygenComponent;
            if (oc == null) return;
            x.O2Litres = oc.SuitOxygenAmount;
            x.O2Capacity = oc.OxygenCapacity;
            x.O2Multiplier = oc.OxygenConsumptionRateMutliplier;
            x.Helmet = oc.HelmetEnabled;
            x.HaveSuit = true;
        }

        /// <summary>The suit's oxygen in Subnautica units (SE fill level × 45); NaN when it cannot be read.</summary>
        private static double SuitOxygenUnits(MyCharacter c)
        {
            if (!On("oxygen")) return double.NaN;
            try { return SuitOxygenUnitsCore(c); }
            catch (Exception e) { Disable("oxygen", e); return double.NaN; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double SuitOxygenUnitsCore(MyCharacter c)
        {
            MyCharacterOxygenComponent oc = c.OxygenComponent;
            return oc == null ? double.NaN : oc.SuitOxygenLevel * SwimModelAdapter.TankSeconds;
        }

        /// <summary>A hand tool or weapon is in the character's hands (Subnautica: −1 m/s on the swim speed).</summary>
        private static bool ToolHeld(MyCharacter c)
        {
            if (!On("tool")) return false;
            try { return ToolHeldCore(c); }
            catch (Exception e) { Disable("tool", e); return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ToolHeldCore(MyCharacter c)
        {
            // MyCharacter.CurrentWeapon: the
 // tool or weapon in the hands, null with empty hands
            return c.CurrentWeapon != null;
        }

        private static void Notify(string text, int ms)
        {
            try { NotifyCore(text, ms); } catch (Exception) { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void NotifyCore(string text, int ms)
        {
            var u = Sandbox.ModAPI.MyAPIGateway.Utilities;
            if (u != null) u.ShowNotification(text, ms, "Red");
        }

        private static ulong CurrentTick()
        {
            try { MySession s = MySession.Static; return s != null ? (ulong)(uint)s.GameplayFrameCounter : 0; }
            catch (Exception) { return 0; }
        }

        private static void Error(string where, Exception e)
        {
            _errors++;
            if (IsPermanent(e))
            {
                //  a game member or type missing in this build fails every tick; log once
 // (FEATURE DISABLED controller) and stay inert (stock SE movement), restoring what the swim changed
                Disable("controller", where + " " + e.GetType().Name + ": " + e.Message + " (a game member or type is missing: the swim controller stays inert, stock SE movement)");
                HookVelocity = false;
                _pending = false;
                MyCharacter c = _char;
                try { if (Model.Swimming && c != null && !c.Closed) Exit(c, SwimExit.ModelOff, _lastEyeY, 0, "controller disabled"); }
                catch (Exception) { try { ExitWithoutCharacter(SwimExit.ModelOff); } catch (Exception) { } }
                return;
            }
            if (_errLog.Allow(Win32.TickCount64()))
                DeLog.Info("DE.Swim error #" + _errors + " " + where + " " + e.GetType().Name + ": " + e.Message);
        }

        /// <summary>A failure no later tick can heal: a member or type missing in the loaded game or Wire build.</summary>
        internal static bool IsPermanent(Exception e)
        {
            for (Exception x = e; x != null; x = x.InnerException)
                if (x is MissingMemberException || x is TypeLoadException || x is TypeInitializationException || x is BadImageFormatException)
                    return true;
            return false;
        }

        // ================================================================================================ save

 /// <summary>
 /// Postfix of <c>MyCharacterJetpackComponent.GetObjectBuilder(MyObjectBuilder_Character)</c>: while the swim policy holds the player's own jetpack and dampener states (swimming,
 /// or seated after a swim), the saved character gets those, not the swim's forced ones. The jetpack is saved on if
 /// it was on at the entry or is on now (switched on with the head out); the dampeners as at the entry.
 /// </summary>
        public static void AfterJetpackObjectBuilder(MyCharacterJetpackComponent comp, MyObjectBuilder_Character ob)
        {
            if (!Enabled || comp == null || ob == null || !Policy.Active || !On("save")) return;
            try { AfterJetpackObjectBuilderCore(comp, ob); }
            catch (Exception e) { Disable("save", e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AfterJetpackObjectBuilderCore(MyCharacterJetpackComponent comp, MyObjectBuilder_Character ob)
        {
            MyCharacter c = _char;
            if (c == null || !ReferenceEquals(comp.Character, c)) return;
            bool jet = Policy.JetpackWas || ob.JetpackEnabled, damp = Policy.DampenersWas;
            if (ob.JetpackEnabled == jet && ob.DampenersEnabled == damp) return;
            string line = "DE.Swim save while swimming: the character is saved with jetpack " + (jet ? "on" : "off") + " dampeners " + (damp ? "on" : "off") +
                          " (the player's states; the swim's are jetpack " + (ob.JetpackEnabled ? "on" : "off") + " dampeners " + (ob.DampenersEnabled ? "on" : "off") + ")";
            ob.JetpackEnabled = jet;
            ob.DampenersEnabled = damp;
            if (_saveLog.Allow(Win32.TickCount64())) DeLog.Info(line);
        }

        // ================================================================================================ dump

        public static Dictionary<string, object> Dump()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "enabled", Enabled },
                { "model", ModelOn ? "on" : "off" },
                { "standby", Standby },
                { "tick", (double)CurrentTick() },
                { "rows", RowsEveryTick ? "on" : "auto" },
                { "state", StateName },
                { "mode", ModeName },
                { "controls", Model.Controls },
                { "jetpackAllowed", _lastJetAllowed },
                { "steps", (double)Model.Steps },
                { "hooks", new Dictionary<string, object>(StringComparer.Ordinal) { { "velocity", HookVelocity }, { "scriptWalk", HookInject }, { "save", SwimPatches.SaveOk } } },
                { "velocitySource", VelocitySource },
                { "compensation", new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "mode", Gain.Mode }, { "gain", Gain.Gain }, { "inStepRatio", double.IsNaN(Gain.Ratio) ? null : (object)Gain.Ratio },
                        { "samples", Gain.Samples }, { "rejected", Gain.Rejected },
                        { "bias", Gain.BiasSamples > 0 ? Arr(Gain.Bias) : null }, { "biasSamples", Gain.BiasSamples },
                    } },
                { "characterId", _charId },
                { "onPlanet", _onPlanet },
                { "eyeY", double.IsNaN(_lastEyeY) ? null : (object)_lastEyeY },
                { "eyeSn", Arr(_lastEyeSn) },
                { "vSet", Arr(_lastVSet) },
                { "vSe", Arr(_lastVSe) },
                { "residual", Arr(_lastRes) },
                { "enters", _enters },
                { "exits", _exits },
                { "lastExit", SwimExit.Name(_lastExit) },
                { "ticksSwimming", _ticksSwimming },
                { "rowsWritten", _rows },   // "rows" above is the on|auto setting; a duplicate key threw in the real run of
                { "jetpackRefusals", _jetpackRefusals },
                { "policy", new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "active", Policy.Active }, { "parked", Policy.Parked }, { "jetpackWas", Policy.JetpackWas }, { "dampenersWas", Policy.DampenersWas },
                        { "o2MultiplierWas", Policy.HaveO2Multiplier ? (object)(double)Policy.O2MultiplierWas : null },
                    } },
                { "depthCheck", new Dictionary<string, object>(StringComparer.Ordinal)
                    { { "checks", _depthChecks }, { "mismatches", _depthMismatches }, { "maxAbsDiffM", _depthMaxDiff } } },
                { "mutants", S2bMutants.Active + (S5aMutants.Active.Length > 0 ? (S2bMutants.Active.Length > 0 ? "," : "") + S5aMutants.Active : "") },
                { "errors", _errors },
                { "numbers", SwimModelAdapter.Numbers() },
                // S5a
                { "fins", SwimFins.Name(_fins) },
                { "finsModifier", SwimFins.Modifier(_fins) },
                { "o2Override", double.IsNaN(_o2Override) ? null : (object)_o2Override },
                { "o2Fraction", _wire.Active && !double.IsNaN(_wire.O2Fraction) ? (object)_wire.O2Fraction : null },
                { "wire", new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "version", (double)_lastWire.Version }, { "flags", (double)_lastWire.Flags }, { "mode", SwimMode.Name(_lastWire.Mode) },
                        { "fins", SwimFins.Name(_lastWire.Fins) }, { "speedH", (double)_lastWire.SpeedH }, { "speedV", (double)_lastWire.SpeedV },
                        { "move", new object[] { (double)_lastWire.MoveForward, (double)_lastWire.MoveStrafe, (double)_lastWire.MoveVertical } },
                        { "o2", (double)_lastWire.O2 }, { "depthClass", (double)_lastWire.DepthClass }, { "vyFrac", (double)_lastWire.VyFrac },
                        { "text", SwimWire.Describe(ref _lastWire) },
                    } },
            };
            lock (Disabled) d["disabled"] = new List<object>(Disabled);
            SwimScript sc = _script;
            d["script"] = sc == null ? null : (object)new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "step", sc.StepIndex + 1 }, { "steps", sc.Steps.Length }, { "elapsedTicks", sc.Elapsed }, { "totalTicks", sc.TotalTicks },
            };
            // the character's suit as SE has it now (the analyser reads jetpack, dampeners and o2Level from dumps)
            MyCharacter c = _char;
            d["jetpack"] = null;
            d["dampeners"] = null;
            d["o2Level"] = null;
            if (c != null && !c.Closed)
            {
                bool jet, damp;
                ReadJetpack(c, out jet, out damp);
                if (On("jetpack"))
                {
                    d["jetpack"] = jet;
                    d["dampeners"] = damp;
                }
                double units = SuitOxygenUnits(c);
                if (!double.IsNaN(units)) d["o2Level"] = units / SwimModelAdapter.TankSeconds;
                // the scripted session's preconditions: helmet, hydrogen, bottles
                try { d["suit"] = SuitInfoCore(c); }
                catch (Exception e) { d["suit"] = new Dictionary<string, object>(StringComparer.Ordinal) { { "error", e.GetType().Name + ": " + e.Message } }; }
            }
            return d;
        }

        /// <summary>
 /// The suit as the scripted session needs it: helmet closed
 /// the jetpack powered (<c>MyCharacterJetpackComponent.IsPowered</c>,: hydrogen or
 /// creative tools; without it <c>TurnOnJetpack</c> refuses), the hydrogen level, the gas bottles carried
 /// survival mode.
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Dictionary<string, object> SuitInfoCore(MyCharacter c)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            MyCharacterOxygenComponent oc = c.OxygenComponent;
            d["helmet"] = oc != null ? (object)oc.HelmetEnabled : null;
            d["h2Level"] = oc != null ? (object)(double)oc.GetGasFillLevel(MyCharacterOxygenComponent.HydrogenId) : null;
            MyCharacterJetpackComponent j = c.JetpackComp;
            d["jetpackPowered"] = j != null ? (object)j.IsPowered : null;
            int o2b = 0, h2b = 0;
            MyInventory inv = c.GetInventory(0);
            MyDefinitionManager defs = MyDefinitionManager.Static;
            if (inv != null && defs != null)
            {
                int count = inv.GetItemsCount();
                for (int k = 0; k < count; k++)
                {
                    MyPhysicalInventoryItem? it = inv.GetItemByIndex(k);
                    if (!it.HasValue || it.Value.Content == null) continue;
                    var bottle = defs.GetPhysicalItemDefinition(it.Value.Content) as MyOxygenContainerDefinition;
                    if (bottle == null) continue;
                    if (bottle.StoredGasId == MyCharacterOxygenComponent.OxygenId) o2b++;
                    else if (bottle.StoredGasId == MyCharacterOxygenComponent.HydrogenId) h2b++;
                }
            }
            d["o2Bottles"] = o2b;
            d["h2Bottles"] = h2b;
            MySession s = MySession.Static;
            d["survival"] = s != null ? (object)s.SurvivalMode : null;
            return d;
        }

        public static Dictionary<string, object> O2Info()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "rule", S2bMutants.OxygenRuleOff ? "OFF (S2b.M4)" : "on" },
                { "tankSeconds", SwimModelAdapter.TankSeconds },
                { "refilledLitres", _o2RefilledLitres },
                { "multiplierWanted", double.IsNaN(_o2Wanted) ? null : (object)_o2Wanted },
                { "baseRate", double.IsNaN(_o2BaseRate) ? null : (object)_o2BaseRate },
                { "seSinkPerSecondFactor", SwimO2Rule.SeSinkPerSecondFactor },
                { "bottleWithGas", _bottle },
            };
            MyCharacter c = _char;
            if (c == null || c.Closed) { d["character"] = null; return d; }
            try { O2InfoCore(c, d); }
            catch (Exception e) { d["error"] = e.GetType().Name + ": " + e.Message; }
            return d;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void O2InfoCore(MyCharacter c, Dictionary<string, object> d)
        {
            MyCharacterOxygenComponent oc = c.OxygenComponent;
            if (oc == null) { d["oxygenComponent"] = null; return; }
            double cap = oc.OxygenCapacity, tank = SwimModelAdapter.TankSeconds;
            float mult = oc.OxygenConsumptionRateMutliplier;
            double rate = oc.OxygenConsumptionRate;
            double baseRate = mult > 0 ? rate / mult : (double.IsNaN(_o2BaseRate) ? double.NaN : _o2BaseRate);
            d["capacityL"] = cap;
            d["level"] = (double)oc.SuitOxygenLevel;
            d["amountL"] = (double)oc.SuitOxygenAmount;
            d["helmetClosed"] = oc.HelmetEnabled;
            d["consumptionRate"] = rate;
            d["multiplier"] = (double)mult;
            d["stockLitresPerSecond"] = double.IsNaN(baseRate) ? null : (object)(baseRate * SwimO2Rule.SeSinkPerSecondFactor);
            d["subnauticaLitresPerSecond"] = new object[]
            {
                SwimO2Rule.FractionPerSecond(1.0, tank) * cap, SwimO2Rule.FractionPerSecond(1.5, tank) * cap, SwimO2Rule.FractionPerSecond(2.0, tank) * cap,
            };
            d["refillLitresPerSecond"] = SwimO2Rule.FractionPerSecond(30.0, tank) * cap;
            if (!double.IsNaN(baseRate))
                d["multipliers"] = new object[]
                {
                    SwimO2Rule.Multiplier(1.0, tank, cap, baseRate), SwimO2Rule.Multiplier(1.5, tank, cap, baseRate), SwimO2Rule.Multiplier(2.0, tank, cap, baseRate),
                };
        }

        private static object[] Arr(Vec3 v) { return new object[] { v.X, v.Y, v.Z }; }
    }

    /// <summary>
 /// The only way in for callers outside S2b (Plugin.UpdateCore, FrameWriter's C1, the plugin state dump): review
 /// finding 10. If <see cref="SwimController"/> cannot initialise (a stale DeepEngineers.Wire.dll without
 /// the S2b types passes the layout check, its static initializer then throws on every access), the first failure is
 /// logged once (<c>DE.Swim FEATURE DISABLED controller</c>) and the swim controller is skipped for the session, so the
 /// link, the heartbeat and C1 go on. Holds no state of Wire or game types itself.
 /// </summary>
    internal static class SwimGuard
    {
        private static volatile bool _broken;

        public static bool Broken { get { return _broken; } }

        public static void PluginUpdate(bool inWorld)
        {
            if (_broken) return;
            try { PluginUpdateCore(inWorld); }
            catch (Exception e) { Fail("PluginUpdate", e); }
        }

        public static long SwimmingCharacterId
        {
            get
            {
                if (_broken) return 0;
                try { return IdCore(); }
                catch (Exception e) { Fail("SwimmingCharacterId", e); return 0; }
            }
        }

        public static string StateName
        {
            get
            {
                if (_broken) return "broken";
                try { return StateCore(); }
                catch (Exception e) { Fail("StateName", e); return "broken"; }
            }
        }

        /// <summary>S5a: the C1 swim extension (FrameWriter, every C1 record); a broken controller leaves the pad zero.</summary>
        public static void FillWire(ref SeFrame f)
        {
            if (_broken) { SwimWire.Clear(ref f); return; }
            try { FillWireCore(ref f); }
            catch (Exception e) { SwimWire.Clear(ref f); Fail("FillWire", e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FillWireCore(ref SeFrame f) { SwimController.FillWire(ref f); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PluginUpdateCore(bool inWorld) { SwimController.PluginUpdate(inWorld); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long IdCore() { return SwimController.SwimmingCharacterId; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string StateCore() { return SwimController.StateName; }

        private static void Fail(string where, Exception e)
        {
            if (_broken) return;
            _broken = true;
            try
            {
                DeLog.Info("DE.Swim FEATURE DISABLED controller: " + where + " threw " + e.GetType().Name + ": " + e.Message +
                           " (the swim controller is skipped for this session; S0/S1/S2 unaffected)");
            }
            catch (Exception) { }
        }
    }
}
