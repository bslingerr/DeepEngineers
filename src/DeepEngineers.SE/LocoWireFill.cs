using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox.Game.Entities.Character;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S7a: the C1 locomotion extension writer (CONTRACT §5.4 "C1 locomotion extension"; bytes 0xA0-0xAF), called by
 /// <see cref="FrameWriter"/> with every C1 record beside the S5a swim extension. Reads the controlled character
 /// composition is <see cref="LocoCompose"/> (game-independent, tested). No
 /// character: version 1, state None. A failure (an exception or a missing member at JIT time) logs once, writes
 /// version 0 from then on (Subnautica keeps S5's sources) and never breaks C1. Rows: <c>s7a.loco</c> {tick, state,
 /// raw, flags, vF, vS, vU, yawRate, inWater} on every change of state or flags and at 2 Hz. Update thread.
 /// </summary>
    internal static class LocoWireFill
    {
        private static readonly LocoCompose Composer = new LocoCompose();
        private static readonly LocoRowCadence Rows = new LocoRowCadence();
        private static long _charId;
        private static bool _disabled;
        private static LocoWireState _last;
        private static ulong _lastTick;

        public static long Written, Errors;
        public static bool Disabled { get { return _disabled; } }
        public static LocoWireState Last { get { return _last; } }
        public static ulong LastTick { get { return _lastTick; } }

        /// <summary>Writes the extension of this tick into <paramref name="f"/>; <paramref name="simDt"/> = simulation seconds since the previous C1 tick.</summary>
        public static void Fill(ref SeFrame f, MyCharacter c, bool swimLatch, double simDt)
        {
            if (_disabled) { S7aLoco.Erase(ref f); return; }
            try
            {
                FillCore(ref f, c, swimLatch, simDt);
            }
            catch (Exception e)
            {
                Errors++;
                _disabled = true;
                S7aLoco.Erase(ref f);
                DeLog.Info("DE.S7a loco OFF after " + e.GetType().Name + ": " + e.Message + " (C1 0xA0-0xAF written as version 0 from now on; " +
                           "Subnautica keeps S5's character sources; C1 itself is unaffected)");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FillCore(ref SeFrame f, MyCharacter c, bool swimLatch, double simDt)
        {
            var src = new LocoSource();
            if (c != null && !c.Closed)
            {
                if (c.EntityId != _charId)
                {
                    _charId = c.EntityId;
                    Composer.Reset();
                    Rows.Reset();
                }
                src.Present = true;
                src.Movement = (ushort)c.GetCurrentMovementState();
                src.SwimLatch = swimLatch;
                src.Crouching = c.IsCrouching;
                src.Dead = c.IsDead;
                src.Seated = c.IsSitting;
                src.Jetpack = c.JetpackRunning;
                try { src.ToolHeld = c.CurrentWeapon != null; } catch (Exception) { src.ToolHeld = false; }   // VERIFIED public IMyHandheldGunObject<MyDeviceBase> CurrentWeapon
                MatrixD wm = c.WorldMatrix;
                src.Forward = new Vec3(wm.Forward.X, wm.Forward.Y, wm.Forward.Z);
                src.Right = new Vec3(wm.Right.X, wm.Right.Y, wm.Right.Z);
                src.Up = new Vec3(wm.Up.X, wm.Up.Y, wm.Up.Z);
                var phys = c.Physics;
                if (phys != null)
                {
                    Vector3 v = phys.LinearVelocity;
                    bool grounded = false;
                    try
                    {
                        var proxy = phys.CharacterProxy;
                        grounded = proxy != null && proxy.Supported;
                        // review F7: the locomotion velocity is relative to the support (a moving or bobbing deck
 // played walk/run while SE's character stood still); VERIFIED MyCharacterProxy.GroundVelocity (public get)
                        if (grounded) v -= proxy.GroundVelocity;
                    }
                    catch (Exception) { grounded = false; }
                    src.Velocity = new Vec3(v.X, v.Y, v.Z);
                    src.Grounded = grounded;
                }
            }
            else if (_charId != 0)
            {
                _charId = 0;
                Composer.Reset();
                Rows.Reset();
            }
            LocoWireState w = Composer.Compose(ref src, simDt);
            S7aLoco.Write(ref f, ref w);
            _last = w;
            _lastTick = f.tick;
            Written++;
            if (Rows.Due(ref w, Win32.TickCount64())) Row(f.tick, ref w);
        }

        private static void Row(ulong tick, ref LocoWireState w)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"t\":");
            JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"ev\":\"s7a.loco\",\"tick\":").Append(tick).Append(",\"state\":");
            JsonText.AppendString(sb, S7aConventions.C1LocoState.Name(w.State));
            sb.Append(",\"stateId\":").Append(w.State).Append(",\"raw\":").Append(w.SeMovement).Append(",\"flags\":").Append(w.Flags)
              .Append(",\"vF\":").Append(w.VForwardCm).Append(",\"vS\":").Append(w.VStrafeCm).Append(",\"vU\":").Append(w.VUpCm)
              .Append(",\"yawRate\":").Append(w.YawRateCdeg).Append(",\"inWater\":")
              .Append((w.Flags & S7aConventions.C1LocoFlags.InWater) != 0 ? "true" : "false").Append('}');
            Jsonl.WriteLineBuffered(sb.ToString());
        }

        /// <summary><c>de.s7 status</c>: the last written extension.</summary>
        public static string Describe()
        {
            LocoWireState s = _last;
            return _disabled ? "off (error)" : "tick " + _lastTick + " " + S7aLoco.Describe(ref s);
        }
    }
}
