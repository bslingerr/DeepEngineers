using System;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>The SE camera of one tick, in plain doubles (filled from <c>MySector.MainCamera</c> by the game glue).</summary>
    internal struct CameraSample
    {
        /// <summary><c>MyCamera.WorldMatrix</c> rows (Right, Up, Backward, Translation), SE world.</summary>
        public SeMatrix World;
        /// <summary><c>MyCamera.FovWithZoom</c>: the vertical FOV of the projection, radians.</summary>
        public double FovRad;
        /// <summary>The projection's near plane: min(4, <c>NearPlaneDistance</c>) (<c>MyCamera.GetSafeNear</c>).</summary>
        public float Near;
        /// <summary><c>MyCamera.AspectRatio</c> (viewport width / height), for logs.</summary>
        public float Aspect;
        /// <summary>C1 <c>cameraMode</c> (Wire.CameraMode).</summary>
        public uint Mode;
        public bool FirstPerson;
    }

    /// <summary>The local SE character of one tick (C1 character fields).</summary>
    internal struct CharacterSample
    {
        public bool Present;
        public ulong EntityId;
        /// <summary>Character world matrix rows (orthonormal), SE world.</summary>
        public SeMatrix World;
        public bool Dead;
        public bool Seated;
        /// <summary>True when <see cref="PelvisWorld"/> is the <c>SE_RigPelvis</c> bone; false: the fallback point.</summary>
        public bool PelvisFromBone;
        /// <summary>Pelvis (or fallback) position, SE world.</summary>
        public Vec3 PelvisWorld;
    }

    /// <summary>The tick-level fields of C1 that do not come from the camera or the character.</summary>
    internal struct FrameClock
    {
        /// <summary><c>MySession.Static.GameplayFrameCounter</c>.</summary>
        public ulong Tick;
        /// <summary>QueryPerformanceCounter at capture.</summary>
        public long Qpc;
        public double SimTimeSec;
        public float SimSpeed;
        public uint WorldEpoch;
        public bool InWorld;
        public bool Paused;
        /// <summary>A plugin command moved the camera this tick (de.teleport, first de.path sample).</summary>
        public bool TeleportRequested;
        /// <summary>C1 flag 3 (S7: airtight rooms); false in S1.</summary>
        public bool CameraInAirtightRoom;
    }

    /// <summary>What the composer derived besides the C1 record (C5 fields, logs). All doubles.</summary>
    internal struct FrameDiagnostics
    {
        /// <summary>F(Pcam) in doubles (the naive map under S1.M1).</summary>
        public Vec3 CamSn;
        public Vec3 CamSe;
        /// <summary>The SE camera's unit Forward and Up (SE world, doubles), for the test records of tests/s1/check_wm.py.</summary>
        public Vec3 CamFwdSe;
        public Vec3 CamUpSe;
        public Quat CamRot;
        /// <summary>Rate of change of <see cref="CamSn"/> per simulation second (C5 camVelSn).</summary>
        public Vec3 CamVelSn;
        public bool Teleported;
        /// <summary>The teleported flag came from a detected jump (&gt; 20 m or &gt; 45° in one tick), not a command.</summary>
        public bool Jump;
        /// <summary>The character pivot P* (SE world); the camera when no character.</summary>
        public Vec3 PivotSe;
        public Vec3 PelvisSn;
        public double CharS;
    }

    /// <summary>
 /// The boxes of the camera character's pivot group (CONTRACT §2.4): the character's own LocalAABB, plus every grid
 /// of the physical group of the block it is seated in. Preallocated; the game glue refills it each tick without
 /// allocating. Members beyond <see cref="Capacity"/> are dropped (counted).
 /// </summary>
    internal sealed class PivotGroup
    {
        public readonly SeMatrix[] Matrices;
        public readonly Vec3[] Mins;
        public readonly Vec3[] Maxs;
        public int Count;
        public long Dropped;

        public PivotGroup(int capacity)
        {
            Matrices = new SeMatrix[capacity];
            Mins = new Vec3[capacity];
            Maxs = new Vec3[capacity];
        }

        public int Capacity { get { return Matrices.Length; } }

        public void Clear() { Count = 0; }

        public bool Add(SeMatrix m, Vec3 min, Vec3 max)
        {
            if (Count >= Matrices.Length) { Dropped++; return false; }
            Matrices[Count] = m;
            Mins[Count] = min;
            Maxs[Count] = max;
            Count++;
            return true;
        }
    }

    /// <summary>
 /// Composes the C1 <c>SeFrame</c> record (CONTRACT §5.4 C1, §2.3, §2.8) from one tick's camera and character,
 /// game-independently (tests/DeepEngineers.SE.Tests checks it against <c>tools/worldmap/wire_ref.py</c>):
 /// <list type="bullet">
 /// <item><c>camPos</c> = F(Pcam) exactly, <c>camRot</c> = LookRotation(mapdir(Forward), mapdir(Up)) at Pcam (Unity
 /// quaternion, w ≥ 0), never scaled (<see cref="WireMath.Camera"/>);</item>
 /// <item><c>vFovDeg</c> = FovWithZoom × 180/π; <c>near</c>; <c>aspect</c>; <c>cameraMode</c>;</item>
 /// <item>character: <c>(charO, charQ, charS)</c> = <see cref="WireMath.Pose"/> at P* = the point of the pivot
 /// group's boxes nearest to the camera (the camera itself when inside); <c>pelvisSn</c> = F(pelvis) exactly;</item>
 /// <item>flags: inWorld, paused, firstPerson, cameraInAirtightRoom (S7), teleported (a command, or a camera jump
 /// &gt; 20 m or &gt; 45° in one tick, as CONTRACT §2.8), characterPresent/Dead/Seated, characterSwimming (pelvis
 /// below the sea sphere, F(pelvis).y &lt; 0, not seated, not dead);</item>
 /// <item>bones: <c>boneCount</c> = 0 and <c>bonesVersion</c> = 0 in S1 (S5+ fills them).</item>
 /// </list>
 /// No character: <c>characterId</c> 0, <c>charO</c> = <c>pelvisSn</c> = <c>camPos</c>, <c>charQ</c> identity,
 /// <c>charS</c> 1. Allocation-free.
 /// </summary>
    internal sealed class FrameComposer
    {
        /// <summary>CONTRACT §2.8: a camera jump larger than this in one tick snaps (and SE sets C1 teleported).</summary>
        public const double JumpDistanceM = 20.0;
        public const double JumpAngleDeg = 45.0;
        /// <summary>SE simulation ticks per second (CONTRACT §2.1).</summary>
        public const double TicksPerSecond = 60.0;

        private bool _hasLast;
        private ulong _lastTick;
        private Vec3 _lastCamSe;
        private Vec3 _lastCamSn;
        private Quat _lastRot;
        private Vec3 _vel;

        public long Frames { get; private set; }
        public long Teleports { get; private set; }
        public long Jumps { get; private set; }

        /// <summary>Forget the previous camera (new world, re-bound site): the next frame has no velocity and no jump.</summary>
        public void Reset()
        {
            _hasLast = false;
            _vel = Vec3.Zero;
        }

        public void Compose(SiteMap site, ref FrameClock clock, ref CameraSample cam, ref CharacterSample ch, PivotGroup group,
                            ref SeFrame f, out FrameDiagnostics d)
        {
            if (site == null) throw new ArgumentNullException("site");
            d = default(FrameDiagnostics);
            Frames++;

            Vec3 p = cam.World.Translation;
            Vec3 camSn;
            Quat camRot;
            S1Math.CameraOf(site, cam.World, out camSn, out camRot);

            // teleport flag: a command this tick, or a jump larger than the CONTRACT §2.8 snap thresholds
            bool jump = false;
            if (_hasLast)
            {
                double moved = (p - _lastCamSe).Length;
                double turned = RotationAngleDeg(camRot, _lastRot);
                jump = moved > JumpDistanceM || turned > JumpAngleDeg;
            }
            bool teleported = clock.TeleportRequested || jump;
            if (teleported) Teleports++;
            if (jump) Jumps++;

            // camera velocity in SN metres per simulation second, from consecutive ticks (0 after a teleport)
            if (teleported || !_hasLast) _vel = Vec3.Zero;
            else if (clock.Tick > _lastTick)
                _vel = (camSn - _lastCamSn) * (TicksPerSecond / (clock.Tick - _lastTick));
            _hasLast = true;
            _lastTick = clock.Tick;
            _lastCamSe = p;
            _lastCamSn = camSn;
            _lastRot = camRot;

            uint flags = 0;
            if (clock.InWorld) flags |= SeFrameFlags.InWorld;
            if (clock.Paused) flags |= SeFrameFlags.Paused;
            if (cam.FirstPerson) flags |= SeFrameFlags.FirstPerson;
            if (clock.CameraInAirtightRoom) flags |= SeFrameFlags.CameraInAirtightRoom;
            if (teleported) flags |= SeFrameFlags.Teleported;

            f.tick = clock.Tick;
            f.qpcCapture = clock.Qpc;
            f.simTimeSec = clock.SimTimeSec;
            f.simSpeed = clock.SimSpeed;
            f.worldEpoch = clock.WorldEpoch;
            f.camPos = F3(camSn);
            f.camRot = F4(camRot);
            f.vFovDeg = (float)WireMath.VerticalFovDegrees(cam.FovRad);
            f.near = cam.Near;
            f.aspect = cam.Aspect;
            f.cameraMode = cam.Mode;
            f.boneCount = 0;      // S1: no bones (S5+)
            f.bonesVersion = 0;

            Vec3 pivot = p;
            Vec3 pelvisSn = camSn;
            double charS = 1.0;
            if (ch.Present)
            {
                flags |= SeFrameFlags.CharacterPresent;
                if (ch.Dead) flags |= SeFrameFlags.CharacterDead;
                if (ch.Seated) flags |= SeFrameFlags.CharacterSeated;
                pivot = group != null && group.Count > 0
                    ? WireMath.PivotNearest(group.Matrices, group.Mins, group.Maxs, group.Count, p)
                    : ch.World.Translation;
                WirePose pose = S1Math.Pose(site, ch.World, pivot);
                pelvisSn = S1Math.MapPoint(site, ch.PelvisWorld);
                charS = pose.S;
                if (!ch.Dead && !ch.Seated && pelvisSn.Y < 0.0) flags |= SeFrameFlags.CharacterSwimming;
                f.characterId = ch.EntityId;
                f.charO = F3(pose.O);
                f.charQ = F4(pose.Q);
                f.charS = (float)pose.S;
            }
            else
            {
                f.characterId = 0;
                f.charO = F3(camSn);
                f.charQ = new F32x4(0f, 0f, 0f, 1f);
                f.charS = 1f;
            }
            f.pelvisSn = F3(pelvisSn);
            f.flags = flags;

            d.CamSn = camSn;
            d.CamSe = p;
            d.CamFwdSe = cam.World.Forward;
            d.CamUpSe = cam.World.Up;
            d.CamRot = camRot;
            d.CamVelSn = _vel;
            d.Teleported = teleported;
            d.Jump = jump;
            d.PivotSe = pivot;
            d.PelvisSn = pelvisSn;
            d.CharS = charS;
        }

        /// <summary>Angle between two unit quaternions as rotations, degrees.</summary>
        public static double RotationAngleDeg(Quat a, Quat b)
        {
            double dot = Math.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
            if (dot > 1.0) dot = 1.0;
            return 2.0 * Math.Acos(dot) * (180.0 / Math.PI);
        }

        public static F32x3 F3(Vec3 v) { return new F32x3((float)v.X, (float)v.Y, (float)v.Z); }

        public static F32x4 F4(Quat q) { return new F32x4((float)q.X, (float)q.Y, (float)q.Z, (float)q.W); }
    }

    /// <summary>
 /// C1 <c>teleported</c> after a test command: armed by the command, consumed only by frames that
 /// were written to C1 (a frame composed while there is no hello or the link is down never takes it), and carried on
 /// <see cref="Frames"/> written frames, so a Subnautica frame that misses one snapshot still snaps (a commanded move
 /// under 20 m would otherwise be interpolated). Protocol (FrameWriter): TeleportRequested = <see cref="Pending"/>;
 /// after a written frame that carried it, <see cref="OnWritten"/>. Update thread only.
 /// </summary>
    internal sealed class TeleportLatch
    {
        public const int Frames = 2;
        private int _left;

        public bool Pending { get { return _left > 0; } }

        public void Arm() { _left = Frames; }

        public void OnWritten()
        {
            if (_left > 0) _left--;
        }
    }

    /// <summary>
 /// Game-independent S1 geometry: the map with the S1.M1 seeded failure, the spectator poses of the C11 test
 /// commands (de.teleport, de.path, de.look) and the screen projection of de.pixel. Doubles throughout.
 /// </summary>
    internal static class S1Math
    {
        /// <summary>F(P), or under S1.M1 the naive tangent-plane map ((P − C)·E0, (P − C)·U0 − Rs, (P − C)·N0).</summary>
        public static Vec3 MapPoint(SiteMap site, Vec3 p)
        {
            if (!S1Mutants.NaiveTangentMap) return site.SeToSn(p);
            Vec3 v = p - site.C;
            return new Vec3(Vec3.Dot(v, site.E0), Vec3.Dot(v, site.U0) - site.Rs, Vec3.Dot(v, site.N0));
        }

        /// <summary>WireMath.Camera, or under S1.M1 the naive point and the anchor frame's directions.</summary>
        public static void CameraOf(SiteMap site, SeMatrix m, out Vec3 position, out Quat rotation)
        {
            if (!S1Mutants.NaiveTangentMap)
            {
                CameraPose cp = WireMath.Camera(site, m);
                position = cp.Position;
                rotation = cp.Rotation;
                return;
            }
            position = MapPoint(site, m.Translation);
            rotation = Quat.LookRotation(site.DirSeToSn(site.E0, site.U0, site.N0, m.Forward), site.DirSeToSn(site.E0, site.U0, site.N0, m.Up));
        }

        /// <summary>WireMath.Pose (S1.M1 leaves the entity pose alone: the WM checks look at the camera).</summary>
        public static WirePose Pose(SiteMap site, SeMatrix m, Vec3 pivot)
        {
            return WireMath.Pose(site, m, pivot);
        }

        /// <summary>
 /// VRage <c>MatrixD.CreateWorld(position, forward, up)</c> rebuilt in doubles: Backward = −normalize(forward),
 /// Right = normalize(up × Backward), Up = Backward × Right.
 /// </summary>
        public static SeMatrix CreateWorld(Vec3 position, Vec3 forward, Vec3 up)
        {
            Vec3 back = (-forward).Normalized();
            Vec3 right = Vec3.Cross(up, back).Normalized();
            Vec3 up2 = Vec3.Cross(back, right);
            return new SeMatrix(right, up2, back, position);
        }

        /// <summary>SN direction of a heading: yaw 0 = SN north (+Z), 90 = east (+X) (clockwise seen from above).</summary>
        public static Vec3 HeadingSn(double yawDeg)
        {
            double a = yawDeg * (Math.PI / 180.0);
            return new Vec3(Math.Sin(a), 0.0, Math.Cos(a));
        }

        /// <summary>Rotates an SN vector about SN +Y by <paramref name="deg"/> (positive turns north toward east).</summary>
        public static Vec3 YawSn(Vec3 v, double deg)
        {
            double a = deg * (Math.PI / 180.0);
            double c = Math.Cos(a), s = Math.Sin(a);
            return new Vec3(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
        }

        /// <summary>
 /// The SE spectator pose that shows SN point <paramref name="snPoint"/> looking along SN direction
 /// <paramref name="forwardSn"/> with screen-up <paramref name="upSn"/>: P = F⁻¹(Q), directions by mapdir⁻¹ at P.
 /// </summary>
        public static SeMatrix SpectatorAt(SiteMap site, Vec3 snPoint, Vec3 forwardSn, Vec3 upSn)
        {
            Vec3 p = site.SnToSe(snPoint);
            return SpectatorAtSe(site, p, forwardSn, upSn);
        }

        /// <summary>As <see cref="SpectatorAt"/> for an SE point.</summary>
        public static SeMatrix SpectatorAtSe(SiteMap site, Vec3 p, Vec3 forwardSn, Vec3 upSn)
        {
            Vec3 e, u, n;
            site.FrameAt(p, out e, out u, out n);
            Vec3 fwd = site.DirSnToSe(e, u, n, forwardSn.Normalized());
            Vec3 up = site.DirSnToSe(e, u, n, upSn.Normalized());
            return CreateWorld(p, fwd, up);
        }

        /// <summary>
 /// de.look directions in SN space: "E" (1, 0, 0), "N" (0, 0, 1), "down" (0, −1, 0) with north as screen-up,
 /// "yaw+&lt;deg&gt;" / "yaw-&lt;deg&gt;" the current forward and up turned about SN +Y, "pitch+&lt;deg&gt;" /
 /// "pitch-&lt;deg&gt;" the current view tilted up (+) or down (−) in its own forward/up plane (S1.G1-E5 elevations).
 /// False for an unknown word.
 /// </summary>
        public static bool LookDirections(string dir, Vec3 currentForwardSn, Vec3 currentUpSn, out Vec3 forwardSn, out Vec3 upSn, out string label)
        {
            forwardSn = currentForwardSn;
            upSn = currentUpSn;
            label = dir;
            string d = (dir ?? "").Trim();
            if (d.Equals("E", StringComparison.OrdinalIgnoreCase) || d.Equals("east", StringComparison.OrdinalIgnoreCase))
            {
                forwardSn = Vec3.UnitX; upSn = Vec3.UnitY; label = "E"; return true;
            }
            if (d.Equals("N", StringComparison.OrdinalIgnoreCase) || d.Equals("north", StringComparison.OrdinalIgnoreCase))
            {
                forwardSn = Vec3.UnitZ; upSn = Vec3.UnitY; label = "N"; return true;
            }
            if (d.Equals("down", StringComparison.OrdinalIgnoreCase) || d.Equals("D", StringComparison.OrdinalIgnoreCase))
            {
                forwardSn = new Vec3(0.0, -1.0, 0.0); upSn = Vec3.UnitZ; label = "down"; return true;
            }
            if (d.StartsWith("pitch", StringComparison.OrdinalIgnoreCase))
            {
                double deg;
                string num = d.Substring(5).Trim();
                if (num.Length == 0) num = "+45";
                if (!double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out deg))
                    return false;
                double a = deg * (Math.PI / 180.0), c = Math.Cos(a), s = Math.Sin(a);
                Vec3 f = currentForwardSn.Normalized(), u = currentUpSn.Normalized();
                forwardSn = f * c + u * s;
                upSn = u * c - f * s;
                label = "pitch" + (deg >= 0 ? "+" : "") + deg.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            if (d.StartsWith("yaw", StringComparison.OrdinalIgnoreCase))
            {
                double deg;
                string num = d.Substring(3).Trim();
                if (num.Length == 0) num = "+30";
                if (!double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out deg))
                    return false;
                forwardSn = YawSn(currentForwardSn, deg);
                upSn = YawSn(currentUpSn, deg);
                label = "yaw" + (deg >= 0 ? "+" : "") + deg.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            return false;
        }

        /// <summary>
 /// The S1.WM-3 path (ROADMAP S1.WM-3): sample <paramref name="k"/> of <paramref name="samples"/> along the great
 /// circle from the anchor in heading <paramref name="headingDeg"/>, at SN altitude <paramref name="altitude"/>,
 /// evenly spaced over <paramref name="lengthM"/> (the first at the anchor, the last at the full length).
 /// </summary>
        public static Vec3 PathSampleSn(int k, int samples, double lengthM, double altitude, double headingDeg)
        {
            double t = samples > 1 ? (double)k / (samples - 1) : 0.0;
            Vec3 h = HeadingSn(headingDeg);
            return new Vec3(h.X * lengthM * t, altitude, h.Z * lengthM * t);
        }

        /// <summary>
 /// Screen pixel of a world point through a VRage view-projection matrix (row-vector convention, M11…M44 in
 /// <paramref name="vp"/>, row-major), as <c>MyCamera.WorldToScreen</c> and its inverse <c>ScreenToWorld</c>
 /// define it: clip = [x y z 1]·VP, ndc = clip/w, pixel x = (ndc.x + 1)/2·W + offsetX, y = (1 − ndc.y)/2·H +
 /// offsetY. Returns false when the point is behind the camera (w ≤ 0); the pixel is then meaningless.
 /// </summary>
        public static bool Project(double[] vp, double offsetX, double offsetY, double width, double height, Vec3 world,
                                   out double px, out double py, out double ndcZ)
        {
            if (vp == null || vp.Length < 16) throw new ArgumentException("vp: 16 values M11..M44");
            double x = world.X, y = world.Y, z = world.Z;
            double cx = x * vp[0] + y * vp[4] + z * vp[8] + vp[12];
            double cy = x * vp[1] + y * vp[5] + z * vp[9] + vp[13];
            double cz = x * vp[2] + y * vp[6] + z * vp[10] + vp[14];
            double cw = x * vp[3] + y * vp[7] + z * vp[11] + vp[15];
            if (!(cw > 0.0))
            {
                px = py = ndcZ = double.NaN;
                return false;
            }
            double nx = cx / cw, ny = cy / cw;
            ndcZ = cz / cw;
            px = (nx + 1.0) * 0.5 * width + offsetX;
            py = (1.0 - ny) * 0.5 * height + offsetY;
            return true;
        }

        /// <summary>
 /// VRage <c>MatrixD.CreatePerspectiveFieldOfView</c> (fov = vertical, radians) as 16 row-major doubles; used by
 /// the tests to build the matrices SE builds (<c>MyCamera.UpdatePropertiesInternal</c>).
 /// </summary>
        public static double[] PerspectiveFieldOfView(double fov, double aspect, double near, double far)
        {
            double ys = 1.0 / Math.Tan(fov * 0.5);
            var m = new double[16];
            m[0] = ys / aspect;
            m[5] = ys;
            m[10] = far / (near - far);
            m[11] = -1.0;
            m[14] = near * far / (near - far);
            return m;
        }

        /// <summary>The view matrix of a world matrix (its rigid inverse) as 16 row-major doubles.</summary>
        public static double[] ViewOf(SeMatrix w)
        {
            Vec3 r = w.Right, u = w.Up, b = w.Backward, t = w.Translation;
            return new[]
            {
                r.X, u.X, b.X, 0.0,
                r.Y, u.Y, b.Y, 0.0,
                r.Z, u.Z, b.Z, 0.0,
                -Vec3.Dot(t, r), -Vec3.Dot(t, u), -Vec3.Dot(t, b), 1.0,
            };
        }

        /// <summary>Row-major 4×4 product a·b.</summary>
        public static double[] Multiply(double[] a, double[] b)
        {
            var m = new double[16];
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    double s = 0.0;
                    for (int k = 0; k < 4; k++) s += a[i * 4 + k] * b[k * 4 + j];
                    m[i * 4 + j] = s;
                }
            return m;
        }
    }
}
