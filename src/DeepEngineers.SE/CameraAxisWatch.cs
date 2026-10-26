using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Game.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>

 /// the plugin Update, the local character's yaw-block state (<see cref="YawLockRules"/>: walking mode, proxy in the air,
 /// not falling nor jumping — SE then turns the head up and down but never the body) is detected; held for
 /// <see cref="YawLockRules.PersistTicks"/> ticks it is cleared with SE's own <c>MyCharacter.StartFalling</c>
 /// which sets <c>m_isFalling</c> as SE does when the
 /// proxy enters IN_AIR. Each episode logs <c>DE.S7p camera yaw unlocked</c> and an <c>s7p.camera</c> row with
 /// <c>why: "fix"</c>; once a second an <c>s7p.camera</c> row carries the per-axis input ticks and motion
 /// (<see cref="CameraAxisRules"/>), <c>yawStuck</c>/<c>pitchStuck</c>, the ticks the block held, the character state
 /// (ground state, falling, jetpack, seat, ladder, first person, head angle) and the camera controller's type (a
 /// spectator or entity controller left behind shows there). Live <c>de.s7p {yawfix: on|off}</c>, env
 /// <c>DEEPENGINEERS_S7P_YAWFIX</c> (default on). The local character only (the camera is the local viewer's).
 /// </summary>

    internal static class CameraAxisWatch
    {
        // R1: SE's yaw rule reads the private m_isFalling / m_currentJumpTime
 // the public IsFalling is false whenever the movement state is Flying, which the S2b swim
 // controller holds under water - with it the fix fired every 2 s of every normal swim. Null refs fall back to the
 // public properties (and the trace says so).
        private static readonly AccessTools.FieldRef<MyCharacter, bool> FallingRef = SafeFieldRef<bool>("m_isFalling");
        private static readonly AccessTools.FieldRef<MyCharacter, float> JumpTimeRef = SafeFieldRef<float>("m_currentJumpTime");
        internal static bool PrivateFields { get { return FallingRef != null && JumpTimeRef != null; } }

        private static AccessTools.FieldRef<MyCharacter, T> SafeFieldRef<T>(string name)
        {
            try { return AccessTools.FieldRefAccess<MyCharacter, T>(name); }
            catch (Exception e) { DeLog.Info("DE.S7p yawfix: MyCharacter." + name + " not reachable (" + e.GetType().Name + "): using the public property"); return null; }
        }

        private static bool Falling(MyCharacter c) { return FallingRef != null ? FallingRef(c) : Falling(c); }
        private static bool Jumping(MyCharacter c) { return JumpTimeRef != null ? JumpTimeRef(c) > 0f : Jumping(c); }

        public static bool FixOn = true;
        private static MethodInfo _startFalling;
        private static string _startFallingError;
        private static bool _init;
        private static int _blockedTicks;
        private static readonly CameraAxisRules Window = new CameraAxisRules();
        private static ulong _nextRowTick;
        public static long Fixes, FixFailures, Episodes, BlockedTicksTotal, Rows;

        private static void Init()
        {
            _init = true;
            string env = Environment.GetEnvironmentVariable(YawLockRules.EnvVar);
            bool on;
            string error;
            if (YawLockRules.TryParse(env, out on, out error)) FixOn = on;
            else DeLog.Info("DE.S7p camera: " + YawLockRules.EnvVar + "=" + env + " " + error + " (default on)");
            try
            {
                _startFalling = AccessTools.Method(typeof(MyCharacter), "StartFalling", Type.EmptyTypes);
                if (_startFalling == null) _startFallingError = "MyCharacter.StartFalling() not found";
            }
            catch (Exception e) { _startFallingError = e.GetType().Name + ": " + e.Message; }
            DeLog.Info("DE.S7p camera watch: yaw-lock " + (FixOn ? "fix on" : "fix OFF (trace only)") +
                       (_startFallingError != null ? " (MISSING " + _startFallingError + ": trace only)" : " (MyCharacter.StartFalling)") +
                       ", s7p.camera rows once a second");
        }

        /// <summary>Plugin Update, every tick in a world.</summary>
        public static void Tick(ulong tick, bool inWorld)
        {
            if (!_init) Init();
            if (!inWorld) { _blockedTicks = 0; return; }
            MySession s = MySession.Static;
            MyCharacter c = s != null ? s.LocalCharacter : null;
            if (c == null || c.Closed || c.MarkedForClose)
            {
                _blockedTicks = 0;
                return;
            }
            bool controlled = ReferenceEquals(s.ControlledEntity, c);
            int gs = (int)c.CharacterGroundState;
            bool blocked = controlled && YawLockRules.YawBlocked(c.JetpackRunning, c.IsDead, c.IsSitting, c.IsOnLadder, Falling(c), Jumping(c), gs);
            if (blocked) BlockedTicksTotal++;
            Vector2 rot = c.RotationIndicator;
            MyCamera cam = MySector.MainCamera;
            if (cam != null)
            {
                MatrixD m = cam.WorldMatrix;
                Vector3D f = m.Forward;
                Vector3 g = Sandbox.Game.GameSystems.MyGravityProviderSystem.CalculateNaturalGravityInPoint(m.Translation);
                Vector3D up = g.LengthSquared() > 1e-6f ? -Vector3D.Normalize((Vector3D)g) : m.Up;
                Window.Add(f.X, f.Y, f.Z, up.X, up.Y, up.Z, rot.X, rot.Y, blocked);
            }
            int before = _blockedTicks;
            bool fire = YawLockRules.Step(blocked, ref _blockedTicks);
            if (blocked && before == 0) Episodes++;
            if (fire && FixOn) Fix(c, tick, gs);
            if (tick >= _nextRowTick)
            {
                _nextRowTick = tick + 60;
                Row(c, tick, gs, "second", s);
            }
        }

        private static void Fix(MyCharacter c, ulong tick, int gs)
        {
            if (_startFalling == null) { FixFailures++; return; }
            try
            {
                _startFalling.Invoke(c, null);
                Fixes++;
                DeLog.Info("DE.S7p camera yaw unlocked: the character was in the air, not falling, jetpack off (SE ignores yaw then; ground state " + gs +
                           ", after " + YawLockRules.PersistTicks + " ticks): MyCharacter.StartFalling() (#" + Fixes + ")");
                Row(c, tick, gs, "fix", MySession.Static);
            }
            catch (Exception e)
            {
                FixFailures++;
                DeLog.Info("DE.S7p camera yaw unlock failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Row(MyCharacter c, ulong tick, int gs, string why, MySession s)
        {
            bool any = Window.Samples > 0 && (Window.YawInputTicks + Window.PitchInputTicks + Window.BlockedTicks > 0 || Window.YawDeg + Window.PitchDeg > 0.01);
            if (why == "second" && !any) { Window.Restart(); return; }
            string controller = null;
            try { controller = s != null && s.CameraController != null ? s.CameraController.GetType().Name : null; } catch (Exception) { }
            var row = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }, { "ev", "s7p.camera" }, { "tick", (long)tick }, { "why", why },
                { "yawDeg", Math.Round(Window.YawDeg, 3) }, { "pitchDeg", Math.Round(Window.PitchDeg, 3) },
                { "yawInputTicks", Window.YawInputTicks }, { "pitchInputTicks", Window.PitchInputTicks }, { "blockedTicks", Window.BlockedTicks },
                { "yawStuck", Window.YawStuck }, { "pitchStuck", Window.PitchStuck }, { "samples", Window.Samples },
                { "groundState", gs }, { "falling", Falling(c) }, { "jumping", Jumping(c) }, { "jetpack", c.JetpackRunning }, { "sitting", c.IsSitting },
                { "ladder", c.IsOnLadder }, { "dead", c.IsDead }, { "firstPerson", c.IsInFirstPersonView }, { "headX", Math.Round(c.HeadLocalXAngle, 2) },
                { "cameraController", controller }, { "fixOn", FixOn }, { "fixes", Fixes }, { "fixFailures", FixFailures }, { "episodes", Episodes },
            };
            Jsonl.WriteLineBuffered(JsonText.Serialize(row));
            Rows++;
            if (why == "second") Window.Restart();
        }

        public static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["yawfixOn"] = FixOn;
            d["yawfixMethod"] = _startFalling != null ? "MyCharacter.StartFalling" : (_startFallingError ?? "not resolved yet");
            d["yawfixFixes"] = Fixes;
            d["yawfixFailures"] = FixFailures;
            d["yawfixEpisodes"] = Episodes;
            d["yawBlockedTicksTotal"] = BlockedTicksTotal;
            d["cameraRows"] = Rows;
            return d;
        }
    }
}
