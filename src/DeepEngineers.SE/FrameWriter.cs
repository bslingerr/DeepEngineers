using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI.Interfaces;
using VRage.Game.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The C1 <c>SeFrame</c> writer (CONTRACT §5.4 C1, §2.8; ROADMAP S1 "SE plugin"). Runs in a Harmony postfix on
 /// <c>MySandboxGame.PrepareForDraw</c> (<see cref="S1Patches"/>): there the camera of this tick is final
 /// (<c>MyGuiSandbox.Draw → MyGuiScreenGamePlay.Draw → CameraController.ControlCamera(MySector.MainCamera)</c>,
 /// <c>MainCamera.Update</c>, <c>UploadViewMatrixToRender</c>) and the simulation of the same tick has run
 /// <para>
 /// Reads, all on the update thread: <c>MySector.MainCamera</c> (<c>WorldMatrix</c>, <c>FovWithZoom</c> = the
 /// projection's vertical FOV in radians, <c>NearPlaneDistance</c> through min(4, ·) as <c>GetSafeNear</c>,
 /// <c>AspectRatio</c>), <c>MySession.Static</c> (<c>GameplayFrameCounter</c>, <c>ElapsedGameTime</c>,
 /// <c>CameraController</c>, <c>GetCameraControllerEnum</c>, <c>LocalCharacter</c>), <c>MyPhysics.SimulationRatio</c>,
 /// <c>MySandboxGame.IsPaused</c>, the character's <c>WorldMatrix</c>, <c>IsDead</c>, <c>IsSitting</c>, <c>Parent</c>,
 /// <c>PositionComp.LocalAABB</c> and its <c>SE_RigPelvis</c> bone (<c>AnimationController.FindBone</c>,
 /// <c>CharacterBones[i].AbsoluteTransform</c>, model space; bone name from <c>Characters.sbc</c>).
 /// </para>
 /// Composition is <see cref="FrameComposer"/> (game-independent, tested). C1 is written only while the site is
 /// bound, the hello is published and the link is up; the zone and perf mode for C5 are derived whenever the site is
 /// bound. Never throws; allocation-free per frame (one log line every 10 s).
 /// </summary>
    internal static class FrameWriter
    {
        public const string PelvisBone = "SE_RigPelvis";
        public const ulong StatsIntervalMs = 10000;

        private static readonly FrameComposer Composer = new FrameComposer();
        private static readonly ZoneTracker Zones = new ZoneTracker();
        private static readonly PivotGroup Group = new PivotGroup(64);
        private static SiteMap _lastMap;
        private static SeFrame _frame;
        // S7a: C6 (SE's sun at the camera, SunSync) and the simulation clock of the previous tick (loco yaw rate, C6 jumps)
        private static SeTime _time;
        private static double _prevSimSec = double.NaN;

        private static long _pelvisCharId = long.MinValue;
        /// <summary>S5: the C1 bone rows of this tick (78 × 12 floats, reused).</summary>
        private static readonly float[] BoneRows = new float[12 * SeFrameLimits.MaxBones];
        private static int _pelvisIndex = -1;

        // C5 inputs for the next plugin Update
        public static bool HasC5 { get; private set; }
        public static uint C5Zone { get; private set; }
        public static uint C5PerfMode { get; private set; }
        public static uint C5TargetFps { get; private set; }
        public static float C5CamSnY { get; private set; }
        public static F32x3 C5CamVelSn { get; private set; }

        public static ulong LastTick { get; private set; }
        public static FrameDiagnostics LastDiagnostics;

        // stats
        private static long _captures, _written, _notWritten, _errors;
        private static long _sumTicks, _maxTicks;
        private static ulong _nextStatsMs;
        private static RateLimit _errorLog = new RateLimit(10000);

        public static long Written { get { return _written; } }

        /// <summary>The PrepareForDraw postfix (S1.M2 off), or the plugin Update (S1.M2 on).</summary>
        public static void Capture(bool fromPluginUpdate)
        {
            if (fromPluginUpdate != S1Mutants.CameraFromPluginUpdate) return;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                CaptureCore();
            }
            catch (Exception e)
            {
                _errors++;
                if (_errorLog.Allow(Win32.TickCount64()))
                    DeLog.Info("DE.Error C1 writer #" + _errors + " " + e.GetType().Name + ": " + e.Message);
            }
            long dt = Stopwatch.GetTimestamp() - t0;
            _sumTicks += dt;
            if (dt > _maxTicks) _maxTicks = dt;
        }

        private static void CaptureCore()
        {
            Plugin p = Plugin.Instance;
            if (p == null || !p.S1Active) return;
            SiteMap site = SiteBinding.Map;
            if (site == null || p.CurrentPhase != SePhase.InWorld)
            {
                HasC5 = false;
                return;
            }
            MySession s = MySession.Static;
            MyCamera camera = MySector.MainCamera;
            if (s == null || camera == null) { HasC5 = false; return; }

            if (!ReferenceEquals(site, _lastMap))
            {
                _lastMap = site;
                Composer.Reset();
                Zones.Reset();
                _prevSimSec = double.NaN;
            }
            _captures++;

            // camera
            var cam = new CameraSample();
            cam.World = ToSe(camera.WorldMatrix);
            cam.FovRad = camera.FovWithZoom;
            cam.Near = Math.Min(4f, camera.NearPlaneDistance);
            cam.Aspect = camera.AspectRatio;
            bool firstPerson;
            cam.Mode = CameraModeOf(s, out firstPerson);
            cam.FirstPerson = firstPerson;

            // character and its pivot group
            var ch = new CharacterSample();
            Group.Clear();
            MyCharacter c = s.LocalCharacter;
            if (c != null && !c.Closed) FillCharacter(c, ref ch);

            // tick-level fields
            var clock = new FrameClock
            {
                Tick = (ulong)(uint)s.GameplayFrameCounter,
                Qpc = Win32.QpcNow(),
                SimTimeSec = s.ElapsedGameTime.TotalSeconds,
                SimSpeed = MyPhysics.SimulationRatio,
                WorldEpoch = Phases.WorldEpoch,
                InWorld = true,
                Paused = MySandboxGame.IsPaused,
                TeleportRequested = S1Commands.TeleportFlag.Pending,
                // S7: the camera's cell in an airtight room of a mirrored grid (InteriorProbe.cs; C1 flags bit 3; guarded)
                CameraInAirtightRoom = S7Interior.ProbeCamera(camera.WorldMatrix.Translation, (ulong)(uint)s.GameplayFrameCounter, c != null && !c.Closed ? c : null),
            };

            FrameDiagnostics d;
            Composer.Compose(site, ref clock, ref cam, ref ch, Group, ref _frame, out d);
            // S2b: characterSwimming also follows the swim controller's state (the composer sets it from the pelvis y)
            long swimmer = SwimGuard.SwimmingCharacterId; // guarded: a broken swim controller never breaks C1
            if (swimmer != 0 && ch.Present && !ch.Dead && !ch.Seated && (long)ch.EntityId == swimmer)
                _frame.flags |= SeFrameFlags.CharacterSwimming;
            // S5a: the C1 swim extension in the pad bytes 0x94-0x9F (flags, move axes, speed fractions, O2 fraction, fins;
 // CONTRACT §5.4 "C1 swim extension"; layout 1.0 unchanged); guarded like the flag above
            SwimGuard.FillWire(ref _frame);
            // S7a: the C1 locomotion extension in the pad bytes 0xA0-0xAF (state, flags, velocity, yaw rate; CONTRACT §5.4
 // "C1 locomotion extension"; layout 1.0 unchanged); its swim-latch input is the swim byte just written; guarded
            double simDt = double.IsNaN(_prevSimSec) ? 0.0 : Math.Max(0.0, clock.SimTimeSec - _prevSimSec);
            _prevSimSec = clock.SimTimeSec;
            SwimWireState swNow;
            bool swimLatch = SwimWire.TryRead(ref _frame, out swNow) && swNow.Has(SwimWireFlags.InWater);
            LocoWireFill.Fill(ref _frame, c != null && !c.Closed ? c : null, swimLatch, simDt);
            S7Interior.FillWire(ref _frame); // S7: the C1 interior extension 0xB0-0xBF (CONTRACT §5.4 "S7 conventions"; guarded)
            S13Se.FillWire(ref _frame, c != null && !c.Closed ? c : null); // S13 hook: the C1 S13 extension 0xC0-0xC7, the look-frame velocity (CONTRACT §5.4 "S13 conventions"; guarded)
 // S5: the character's bones in canon1 order (CONTRACT §5.4 "Character"; boneCount 78, bonesVersion = the body model
 // key's low 32 bits; 0/0 until its export is ready); guarded, never breaks C1
            int boneCount = c != null && !c.Closed ? CharacterExport.FillBones(c, ref _frame, BoneRows) : 0;
            LastDiagnostics = d;
            LastTick = clock.Tick;

            // C5 S1 fields (zone from the map, perf mode)
            bool hello = p.HelloEpoch != 0;
            uint zone = Zones.Update(SiteBinding.OnSite(d.CamSe), d.CamSn.X, d.CamSn.Z);
            uint fps;
            C5PerfMode = S1Perf.For(zone, hello, out fps);
            C5TargetFps = fps;
            C5Zone = zone;
            C5CamSnY = (float)d.CamSn.Y;
            C5CamVelSn = FrameComposer.F3(d.CamVelSn);
            HasC5 = true;

            bool written = false;
            Link link = p.LinkForS1;
            if (hello && link != null && link.State == LinkState.Up)
                written = link.WriteSeFrame(ref _frame, BoneRows, boneCount);
            if (written) GridMirrorProducer.RecordC1(ref _frame, BoneRows, boneCount);
            if (written) _written++;
            else _notWritten++;
            // S7a: C6 every tick with C1 (SE's sun at this tick's final camera, the sky pole, the jump counter; CONTRACT §5.4
 // "C6 written by SE"); SunSync disables itself on a failure and never breaks C1
            if (SunSync.Tick(clock.Tick, camera.WorldMatrix.Translation, simDt, ref _time) && hello && link != null && link.State == LinkState.Up)
                link.WriteSeTime(ref _time);
            if (written && clock.TeleportRequested) S1Commands.TeleportFlag.OnWritten(); // consumed only by published frames

            S1Commands.OnFrame(ref _frame, ref d, site, written);
            S2Host.OnFrame(ref d, zone, clock.Tick, d.Teleported || d.Jump); // S2: the view state machine on this tick's final camera
            Stats(link);
        }

        private static void FillCharacter(MyCharacter c, ref CharacterSample ch)
        {
            MatrixD wm = c.WorldMatrix;
            ch.Present = true;
            ch.EntityId = (ulong)c.EntityId;
            ch.World = ToSe(wm);
            ch.Dead = c.IsDead;
            ch.Seated = c.IsSitting;

            // pelvis: the SE_RigPelvis bone (model space) through the character's world matrix; else the box centre
            if (c.EntityId != _pelvisCharId)
            {
                _pelvisCharId = c.EntityId;
                _pelvisIndex = -1;
                var acNew = c.AnimationController;
                if (acNew != null)
                {
                    int idx;
                    acNew.FindBone(PelvisBone, out idx);
                    _pelvisIndex = idx;
                }
                DeLog.Info("DE.Frame character " + c.EntityId + ": pelvis " + (_pelvisIndex >= 0 ? "bone " + PelvisBone + " index " + _pelvisIndex : "bone not found, world AABB centre used"));
            }
            bool fromBone = false;
            var ac = c.AnimationController;
            var bones = ac != null ? ac.CharacterBones : null;
            if (_pelvisIndex >= 0 && bones != null && _pelvisIndex < bones.Length && bones[_pelvisIndex] != null)
            {
                Vector3D local = bones[_pelvisIndex].AbsoluteTransform.Translation;
                Vector3D w = Vector3D.Transform(local, ref wm);
                ch.PelvisWorld = new Vec3(w.X, w.Y, w.Z);
                fromBone = true;
            }
            if (!fromBone)
            {
                Vector3D centre = c.PositionComp.WorldAABB.Center;
                ch.PelvisWorld = new Vec3(centre.X, centre.Y, centre.Z);
            }
            ch.PelvisFromBone = fromBone;

            // pivot group (CONTRACT §2.4): the character's own box, plus the physical grid group of its seat
            AddBox(c, wm);
            if (ch.Seated)
            {
                var block = c.Parent as MyCubeBlock;
                MyCubeGrid grid = block != null ? block.CubeGrid : null;
                if (grid != null)
                {
                    var groups = MyCubeGridGroups.Static;
                    var group = groups != null ? groups.Physical.GetGroup(grid) : null;
                    if (group != null)
                        foreach (var node in group.Nodes)
                        {
                            MyCubeGrid g = node.NodeData;
                            if (g != null && !g.Closed) AddBox(g, g.WorldMatrix);
                        }
                    else AddBox(grid, grid.WorldMatrix);
                }
            }
        }

        private static void AddBox(VRage.Game.Entity.MyEntity e, MatrixD wm)
        {
            BoundingBox b = e.PositionComp.LocalAABB;
            Group.Add(ToSe(wm), new Vec3(b.Min.X, b.Min.Y, b.Min.Z), new Vec3(b.Max.X, b.Max.Y, b.Max.Z));
        }

        /// <summary>C1 cameraMode (0 other, 1 first person, 2 third person, 3 cockpit, 4 spectator) and the first-person flag.</summary>
        private static uint CameraModeOf(MySession s, out bool firstPerson)
        {
            firstPerson = false;
            IMyCameraController cc = s.CameraController;
            if (cc == null) return CameraMode.Other;
            if (ReferenceEquals(cc, MySpectatorCameraController.Static)) return CameraMode.Spectator;
            MyCameraControllerEnum e = s.GetCameraControllerEnum();
            switch (e)
            {
                case MyCameraControllerEnum.Entity:
                    firstPerson = true;
                    if (cc is MyCharacter) return CameraMode.FirstPerson;
                    if (cc is MyShipController) return CameraMode.Cockpit;
                    return CameraMode.Other;
                case MyCameraControllerEnum.ThirdPersonSpectator:
                    return CameraMode.ThirdPerson;
                case MyCameraControllerEnum.Spectator:
                case MyCameraControllerEnum.SpectatorDelta:
                case MyCameraControllerEnum.SpectatorFixed:
                case MyCameraControllerEnum.SpectatorOrbit:
                case MyCameraControllerEnum.SpectatorFreeMouse:
                    return CameraMode.Spectator;
                default:
                    return CameraMode.Other;
            }
        }

        public static SeMatrix ToSe(MatrixD m)
        {
            return new SeMatrix(new Vec3(m.M11, m.M12, m.M13), new Vec3(m.M21, m.M22, m.M23), new Vec3(m.M31, m.M32, m.M33), new Vec3(m.M41, m.M42, m.M43));
        }

        private static void Stats(Link link)
        {
            ulong now = Win32.TickCount64();
            if (_nextStatsMs == 0) { _nextStatsMs = now + StatsIntervalMs; return; }
            if (now < _nextStatsMs) return;
            _nextStatsMs = now + StatsIntervalMs;
            double meanUs = _captures > 0 ? _sumTicks * 1e6 / Stopwatch.Frequency / _captures : 0;
            double maxUs = _maxTicks * 1e6 / Stopwatch.Frequency;
            FrameDiagnostics d = LastDiagnostics;
            var sb = new StringBuilder(320);
            sb.Append("{\"t\":");
            JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"ev\":\"c1\",\"captures\":").Append(_captures)
              .Append(",\"written\":").Append(_written).Append(",\"notWritten\":").Append(_notWritten)
              .Append(",\"errors\":").Append(_errors).Append(",\"teleports\":").Append(Composer.Teleports)
              .Append(",\"jumps\":").Append(Composer.Jumps).Append(",\"groupDropped\":").Append(Group.Dropped)
              .Append(",\"usMean\":");
            JsonText.AppendNumber(sb, Math.Round(meanUs, 2));
            sb.Append(",\"usMax\":");
            JsonText.AppendNumber(sb, Math.Round(maxUs, 2));
            sb.Append(",\"tick\":").Append(LastTick).Append(",\"zone\":");
            JsonText.AppendString(sb, ZoneTracker.Name(C5Zone));
            sb.Append(",\"perfMode\":").Append(C5PerfMode).Append(",\"camSn\":[");
            JsonText.AppendNumber(sb, d.CamSn.X); sb.Append(',');
            JsonText.AppendNumber(sb, d.CamSn.Y); sb.Append(',');
            JsonText.AppendNumber(sb, d.CamSn.Z);
            sb.Append("],\"seFramesWritten\":").Append(link != null ? link.SeFramesWritten : 0).Append('}');
            Jsonl.WriteLine(sb.ToString());
            _sumTicks = 0; _maxTicks = 0; _captures = 0;
        }
    }
}
