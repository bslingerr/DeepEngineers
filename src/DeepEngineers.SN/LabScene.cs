using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The S3 lab scene and its C11 commands (ROADMAP S3 "Lab scene command"; dev builds only, like every C11 command):
 /// <list type="bullet">
 /// <item><c>de.s3 {action: "probe", x, z, fromY?, y?, warp?}</c>: a layer-30 (TerrainCollider) ray down at (x, z); the
 /// seabed height h is in the reply when the collider is already there, else the reply is "pending" and the result
 /// is logged as <c>DE.S3 PROBE</c> and kept in <c>de.s3 status</c>. <c>warp: true</c> holds the camera at (x, y, z)
 /// looking down until the collider exists (terrain colliders are built only near the camera).</item>
 /// <item><c>de.s3 {lab: [x, z] | {x, z}, seamoth: true, captures: ["P1", "P2", "P3", "P3+1s", "P4", "P3*60"],
 /// frames: 90, headingDeg: 0, camera: "override" | "c1", session, requireBlock, entity}</c>: probe, wait for the
 /// mirrored block nearest to (x, z), spawn the reference Seamoth 6 m to its right (kinematic, lights off, not
 /// streamed or saved), then for each capture: the camera at the pose (the de.warpcam override, or wait for the
 /// producer's C1 camera there), 90 frames, an in-game capture (S1 CaptureService: UI hidden, PNG + sidecar) plus
 /// the S3 sidecar <c>&lt;png&gt;.s3.json</c> (block corners, screen rectangles, buried-corner rays, Seamoth, time of
 /// day, material). S3.M5 copies the previous PNG for a repeated pose instead of capturing.</item>
 /// <item><c>de.time {t, freeze?}</c>: <c>DayNightCycle.SetDayNightTime(t)</c> (t = day fraction: 0.5 noon, 0 midnight;
 /// the console's daynight is never used), optionally <c>debugFreeze</c>.</item>
 /// <item><c>de.s3 {action: "shear", s, frames, axis, k}</c>: S3.G1-E2 (S3.M7: rigid Frame); <c>"count"</c> (S3.C12);
 /// <c>"teardown"</c>; <c>"rebuild"</c>; <c>"visible", on</c>; <c>"frametime", frames</c> (S3.C11);
 /// <c>"placement"</c> (S3.C2 lines); <c>"config",...</c> (S3Options); <c>"status"</c>.</item>
 /// </list>
 /// Every Unity/Subnautica call is inside the S3 host's feature guard; a failure stops the lab run, logged.
 /// </summary>
    internal sealed class LabScene
    {
        public const ulong ProbeTimeoutMs = 30000;
        public const ulong BlockTimeoutMs = 60000;
        public const ulong SeamothTimeoutMs = 30000;
        public const ulong CaptureTimeoutMs = 10000;
        public const ulong C1PoseTimeoutMs = 30000;
        public const double C1PoseToleranceM = 0.5, C1PoseToleranceDeg = 2.0;
        public const float SeamothLiftM = 0.3f;

        private readonly S1Host _s1;
        private readonly MirrorConsumer _mirror;
        private readonly MonoBehaviour _host;
        private readonly Func<ulong> _snFrame;

        public LabScene(S1Host s1, MirrorConsumer mirror, MonoBehaviour host, Func<ulong> snFrame)
        {
            _s1 = s1;
            _mirror = mirror;
            _host = host;
            _snFrame = snFrame;
        }

        // ------------------------------------------------------------------------------------------ probe

        private sealed class Probe
        {
            public double X, Z, FromY;
            public bool Active, Done, Warp;
            public ulong StartMs, Ms;
            public double SeabedY = double.NaN;
            public string Collider, Error;
            public int Layer;
        }

        private Probe _probe;

        public static int TerrainLayer()
        {
            int l = LayerMask.NameToLayer("TerrainCollider");
            return l >= 0 ? l : 30;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Seabed(double x, double z, double fromY, out RaycastHit hit)
        {
            return Physics.Raycast(new Vector3((float)x, (float)fromY, (float)z), Vector3.down, out hit, 4000f, 1 << TerrainLayer(), QueryTriggerInteraction.Ignore);
        }

        private bool StepProbe(Probe p, ulong now)
        {
            RaycastHit hit;
            if (Seabed(p.X, p.Z, p.FromY, out hit))
            {
                p.Done = true;
                p.Active = false;
                p.SeabedY = hit.point.y;
                p.Collider = hit.collider != null ? hit.collider.name : "?";
                p.Layer = hit.collider != null ? hit.collider.gameObject.layer : -1;
                p.Ms = now - p.StartMs;
                if (p.Warp && (_run == null || _run.Mode != "override")) _s1.Cam.ClearOverride();
                DeLog.Info("DE.S3 PROBE x=" + F(p.X) + " z=" + F(p.Z) + " seabedY=" + F(p.SeabedY) + " collider=" + p.Collider + " layer=" + p.Layer +
                           " fromY=" + F(p.FromY) + " afterMs=" + p.Ms + " (layer-30 TerrainCollider ray; the block centre goes at this height, half buried)");
                return true;
            }
            if (now - p.StartMs > ProbeTimeoutMs)
            {
                p.Active = false;
                p.Error = "no TerrainCollider under (" + F(p.X) + ", " + F(p.Z) + ") within " + ProbeTimeoutMs + " ms (terrain colliders exist only near the camera: warp=true or move the producer's camera there)";
                if (p.Warp) _s1.Cam.ClearOverride();
                DeLog.Warn("DE.S3 PROBE FAILED " + p.Error);
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------ lab run

        private enum Step { Probe, WaitBlock, Seamoth, Move, Settle, Capture, WaitCapture, Done, Failed }

        private sealed class Run
        {
            public double X, Z, HeadingDeg, FromY;
            public bool WantSeamoth, RequireBlock, KeepCamera, ProbeStarted;
            public int Frames;
            public string Mode, Session;
            public ulong EntityId;
            public List<CaptureStep> Steps;
            public Step State;
            public int Index;
            public LabPlan Plan;
            public MirrorGridView Block;
            public ulong StepStartMs;
            public int SettleFrame;
            public string Full, PendingSidecar;
            public long Ok0, Failed0;
            public ulong LastDoneMs;
            public string LastPng;
            public int Ok, FailedCount;
            public string Error;
            public readonly List<string> Results = new List<string>();
        }

        private Run _run;

        // ------------------------------------------------------------------------------------------ seamoth

        private GameObject _seamoth;
        private bool _seamothSpawning;
        private string _seamothError;
        private ulong _nextKinematicMs;

        public bool SeamothAlive { get { return _seamoth != null; } }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void StartSeamoth(Vector3 pos, Quaternion rot, double seabedFallback)
        {
            _seamothSpawning = true;
            _seamothError = null;
            try
            {
                _host.StartCoroutine(SpawnSeamoth(pos, rot, seabedFallback));
            }
            catch (Exception e)
            {
                _seamothSpawning = false;
                _seamothError = "StartCoroutine: " + e.GetType().Name + ": " + e.Message;
                DeLog.Error("DE.S3 FEATURE seamoth DISABLED: " + _seamothError);
            }
        }

        private IEnumerator SpawnSeamoth(Vector3 pos, Quaternion rot, double seabedFallback)
        {
            CoroutineTask<GameObject> task = CraftData.GetPrefabForTechTypeAsync(TechType.Seamoth, false);
            yield return task;
            PlaceSeamoth(task, pos, rot, seabedFallback);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void PlaceSeamoth(CoroutineTask<GameObject> task, Vector3 pos, Quaternion rot, double seabedFallback)
        {
            _seamothSpawning = false;
            try
            {
                GameObject prefab = task.GetResult();
                if (prefab == null) { _seamothError = "CraftData.GetPrefabForTechTypeAsync(Seamoth) returned no prefab"; DeLog.Warn("DE.S3 SEAMOTH FAILED " + _seamothError); return; }
                if (Player.main == null) { _seamothError = "the host world went away while the prefab loaded"; DeLog.Warn("DE.S3 SEAMOTH not placed: " + _seamothError); return; }
                // instantiated deactivated
 // the components are changed first, so LargeWorldEntity.OnEnable, FreezeRigidbodyWhenFar.Awake
 // and the vehicle's Awake never run before the stripping
                GameObject go = UWE.Utils.InstantiateDeactivated(prefab, pos, rot);
                if (go == null) { _seamothError = "UWE.Utils.InstantiateDeactivated returned null"; DeLog.Warn("DE.S3 SEAMOTH FAILED " + _seamothError); return; }
                go.name = "DE.S3.ReferenceSeamoth";
                // a reference object: never streamed or saved (no LargeWorldEntity), never moved by physics or its own vehicle code
                LargeWorldEntity lwe = go.GetComponent<LargeWorldEntity>();
                if (lwe != null) UnityEngine.Object.DestroyImmediate(lwe);
                FreezeRigidbodyWhenFar frz = go.GetComponent<FreezeRigidbodyWhenFar>();
                if (frz != null) UnityEngine.Object.DestroyImmediate(frz);
                WorldForces wf = go.GetComponent<WorldForces>();
                if (wf != null) wf.enabled = false;
                SeaMoth sm = go.GetComponent<SeaMoth>();
                if (sm != null)
                {
                    sm.enabled = false; // its FixedUpdate would set the rigidbody kinematic or not
                    if (sm.lightsParent != null) sm.lightsParent.SetActive(false);
                    if (sm.toggleLights != null && sm.toggleLights.lightsParent != null) sm.toggleLights.lightsParent.SetActive(false);
                }
                Rigidbody rb = go.GetComponent<Rigidbody>();
                if (rb != null) rb.isKinematic = true;
                int lights = 0;
                foreach (Light l in go.GetComponentsInChildren<Light>(true)) { l.enabled = false; lights++; }
                if (!go.activeSelf) go.SetActive(true);
                // rest it 0.3 m above the seabed under its own position
                Bounds b;
                if (RenderBounds(go, out b))
                {
                    RaycastHit hit;
                    double floor = Seabed(go.transform.position.x, go.transform.position.z, go.transform.position.y + 30, out hit) ? hit.point.y : seabedFallback;
                    float delta = (float)(floor + SeamothLiftM - b.min.y);
                    go.transform.position += new Vector3(0, delta, 0);
                    Physics.SyncTransforms();           // collider bounds follow the move before they are read again
                    RenderBounds(go, out b);
                }
                _seamoth = go;
                // the Marmoset fallback (ValCraft pattern) when Addressables failed; tried again whenever a later
 // Addressables request fails while the Seamoth exists
                TryAdoptSeamothShader();
                DeLog.Info("DE.S3 SEAMOTH spawned pos=" + V(go.transform.position) + " rot=" + Q(go.transform.rotation) + " bounds=" + V(b.min) + "-" + V(b.max) +
                           " kinematic=" + (rb != null && rb.isKinematic) + " lightsOff=" + lights + " vehicleScriptDisabled=" + (sm != null) +
                           " (reference for S3.C6-C9: CraftData.GetPrefabForTechTypeAsync(Seamoth), no LargeWorldEntity)");
            }
            catch (Exception e)
            {
                _seamothError = e.GetType().Name + ": " + e.Message;
                DeLog.Error("DE.S3 SEAMOTH FAILED " + _seamothError);
            }
        }

        private long _adoptTriedAt = -1;

        /// <summary>When MarmosetUBER failed to load from Addressables, adopt the reference Seamoth's hull shader (once per failure).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TryAdoptSeamothShader()
        {
            MarmosetMaterials mm = _mirror.Marmoset;
            if (_seamoth == null || !mm.Failed || _adoptTriedAt == mm.Failures) return;
            _adoptTriedAt = mm.Failures;
            foreach (Renderer r in _seamoth.GetComponentsInChildren<Renderer>(true))
                if (r.sharedMaterial != null && mm.TryAdopt(r.sharedMaterial.shader, "the reference Seamoth (" + r.name + ")")) return;
            DeLog.Warn("DE.S3 SEAMOTH has no MarmosetUBER renderer to adopt the shader from (Addressables failure #" + mm.Failures + ")");
        }

        /// <summary>The reference Seamoth's hull: the union of its enabled non-trigger colliders. Its enabled mesh renderers
 /// include an invisible 16 m effect sphere, which the real run reported as a 17 x 16 x 16 m box
 /// (seamothMin/Max): it masked the block in the C8/C9 image regions and rested the Seamoth 8 m too high. Fallback:
 /// mesh renderers no larger than MaxPartM.</summary>
        private static bool RenderBounds(GameObject go, out Bounds b)
        {
            b = default(Bounds);
            bool any = false;
            foreach (Collider c in go.GetComponentsInChildren<Collider>(false))
            {
                if (c.isTrigger || !c.enabled) continue;
                if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
            }
            if (any && b.size.magnitude <= MaxHullM) return true;
            any = false;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(false))
            {
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (r.bounds.size.magnitude > MaxPartM) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any;
        }

        private const float MaxHullM = 10f, MaxPartM = 8f;

        private void DestroySeamoth()
        {
            if (_seamoth != null) { try { UnityEngine.Object.Destroy(_seamoth); } catch (Exception) { } }
            _seamoth = null;
        }

        // ------------------------------------------------------------------------------------------ shear test (S3.G1-E2)

        private sealed class Shear
        {
            public MirrorGridView View;
            public Mesh Mesh;
            public Material Material;
            public double S, K;
            public Vec3 Axis;
            public int Frames, Done;
            public bool Rigid;
            public double MaxLinear, MaxWorld;
            public bool Applied;
        }

        private Shear _shear;
        public string LastShear { get; private set; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private string StartShear(double s, double k, Vec3 axis, int frames)
        {
            if (_shear != null) return "a shear test is running";
            Camera cam = MainCamera.camera;
            if (cam == null) return "no main camera";
            bool rigid = _mirror.Mutants.M7;
            var sh = new Shear { S = s, K = k, Axis = axis, Frames = frames, Rigid = rigid };
            sh.View = MirrorGridView.Create(0xDE5EA2UL, false, null);
            sh.View.Frame.name = "DE.S3.ShearTest";
            ModelData cube = ProceduralCube.Model(0xC0BE, 0.5f, 0);
            PreparedModel pm = MeshBuilder.Prepare(cube, true, true);
            var input = new CellBuildInput { GridId = 0, Options = new BuildOptions() };
            var part = new PartRecord();
            float[] id = { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };
            Array.Copy(id, part.Local, 12);
            input.Parts.Add(new PartBuild { Part = part, Model = pm });
            ChunkMeshData c = MeshBuilder.BuildCell(input).Chunks[0];
            ChunkView cv = sh.View.EnsureChunk(c.Key, false, false);
            sh.Mesh = _mirror.Owned.NewMesh("DE.S3 shear cube");
            MeshUpload.Upload(sh.Mesh, c);
            cv.Mesh = sh.Mesh;
            cv.Filter.sharedMesh = sh.Mesh;
            cv.HasMesh = true;
            if (_mirror.Marmoset.Ready)
            {
                sh.Material = _mirror.Owned.Track(new Material(_mirror.Marmoset.Shader) { name = "DE.S3 shear" });
                cv.Renderer.sharedMaterial = sh.Material;
            }
            cv.Renderer.enabled = true;
            Vector3 o = cam.transform.position + cam.transform.forward * 6f;
            sh.View.FrameT.position = o;
            sh.View.FrameT.localScale = rigid ? Vector3.one : new Vector3((float)s, 1f, (float)s);
            sh.View.EntityT.localScale = new Vector3((float)k, (float)k, (float)k);
            sh.View.Frame.SetActive(true);
            _shear = sh;
            DeLog.Info("DE.S3 SHEAR start s=" + F(s) + " k=" + F(k) + " axis=" + V3(axis) + " frames=" + frames + " frame=" + V(o) +
                       (rigid ? " (MUTANT S3.M7: rigid Frame, emulation keeps s)" : ""));
            return null;
        }

        /// <summary>Update: the Entity rotates by 1° per frame about the tilted axis.</summary>
        private void TickShear()
        {
            Shear sh = _shear;
            if (sh == null) return;
            if (!sh.View.Alive) { _shear = null; LastShear = "aborted (the test object was destroyed)"; return; }
            Quat q = MirrorPoseMath.AngleAxis(sh.Done, sh.Axis);
            sh.View.EntityT.localRotation = new Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W);
            sh.Applied = true;
        }

        /// <summary>Main camera onPreCull: the renderer matrix Unity draws with, against the emulated Apply.</summary>
        public void OnPreCull()
        {
            Shear sh = _shear;
            if (sh == null || !sh.Applied || !sh.View.Alive) return;
            MeshRenderer r = sh.View.AnyRenderer();
            if (r == null) return;
            Matrix4x4 m = r.localToWorldMatrix;
            Quaternion uq = sh.View.EntityT.localRotation;
            Vector3 of = sh.View.FrameT.position;
            var p = new EntityPose { Qx = uq.x, Qy = uq.y, Qz = uq.z, Qw = uq.w, S = sh.S, K = sh.K, Ox = of.x, Oy = of.y, Oz = of.z };
            Vec3[] corners = MirrorPoseMath.Corners(new Vec3(-0.5, -0.5, -0.5), new Vec3(0.5, 0.5, 0.5));
            foreach (Vec3 c in corners)
            {
                Vec3 emul = MirrorPoseMath.ApplyUnityLocal(p, c);
                var lin = new Vec3(m.m00 * c.X + m.m01 * c.Y + m.m02 * c.Z, m.m10 * c.X + m.m11 * c.Y + m.m12 * c.Z, m.m20 * c.X + m.m21 * c.Y + m.m22 * c.Z);
                double dl = (lin - (emul - p.O)).Length;
                Vector3 w = m.MultiplyPoint3x4(new Vector3((float)c.X, (float)c.Y, (float)c.Z));
                double dw = (new Vec3(w.x, w.y, w.z) - emul).Length;
                if (dl > sh.MaxLinear) sh.MaxLinear = dl;
                if (dw > sh.MaxWorld) sh.MaxWorld = dw;
            }
            sh.Applied = false;
            sh.Done++;
            if (sh.Done >= sh.Frames) FinishShear();
        }

        private void FinishShear()
        {
            Shear sh = _shear;
            _shear = null;
            bool pass = sh.MaxLinear < 1e-4;
            LastShear = "s=" + F(sh.S) + " k=" + F(sh.K) + " frames=" + sh.Done + " maxDevLinearM=" + sh.MaxLinear.ToString("0.########", CultureInfo.InvariantCulture) +
                        " maxDevWorldM=" + sh.MaxWorld.ToString("0.########", CultureInfo.InvariantCulture) + " verdict=" + (pass ? "PASS" : "FAIL") +
                        (sh.Rigid ? " (MUTANT S3.M7)" : "");
            DeLog.Info("DE.S3 SHEAR " + LastShear + " (S3.G1-E2: renderer.localToWorldMatrix linear part vs S·R(q)·k < 1e-4 m on every frame; the world " +
                       "figure adds float32 translation rounding)");
            sh.View.Destroy(_mirror.Owned);
            if (sh.Material != null) _mirror.Owned.Destroy(sh.Material);
        }

        // ------------------------------------------------------------------------------------------ frame time (S3.C11)

        private int _ftTarget, _ftCount;
        private double _ftSum, _ftMax;
        private List<float> _ftSamples;
        private long _ftThread0 = -1, _ftProcess0 = -1;
        public string LastFrameTime { get; private set; }

        /// <summary>
 /// S3.C11 frame cost over N frames: the wall frame time (unscaledDeltaTime: flat
 /// under vsync or a frame cap, so it hides a cost that still fits the frame) and the CPU the main thread spent per
 /// frame (GetThreadTimes on the main thread: waits for vsync or the cap are not counted), plus the process CPU per
 /// frame (every thread, the render thread included; information). analyze_s3 compares cpuMsPerFrame.
 /// </summary>
        private void TickFrameTime()
        {
            if (_ftTarget <= 0) return;
            if (_ftThread0 < 0 && _ftCount == 0)
            {
                // the window starts at this frame's Update: CPU baselines now, deltas from the next frame on
                _ftThread0 = SnNative.CurrentThreadCpu100ns();
                _ftProcess0 = SnNative.ProcessCpu100ns();
                if (_ftThread0 < 0) _ftThread0 = 0;
                return;
            }
            float ms = Time.unscaledDeltaTime * 1000f;
            _ftSum += ms;
            if (ms > _ftMax) _ftMax = ms;
            _ftSamples.Add(ms);
            if (++_ftCount < _ftTarget) return;
            long th1 = SnNative.CurrentThreadCpu100ns(), pr1 = SnNative.ProcessCpu100ns();
            var ci = CultureInfo.InvariantCulture;
            string cpu = th1 >= 0 && _ftThread0 > 0 ? ((th1 - _ftThread0) / 1e4 / _ftCount).ToString("0.####", ci) : "-";
            string pcpu = pr1 >= 0 && _ftProcess0 >= 0 ? ((pr1 - _ftProcess0) / 1e4 / _ftCount).ToString("0.####", ci) : "-";
            _ftSamples.Sort();
            float p50 = _ftSamples[_ftSamples.Count / 2], p99 = _ftSamples[(int)(_ftSamples.Count * 0.99)];
            LastFrameTime = "frames=" + _ftCount + " meanMs=" + (_ftSum / _ftCount).ToString("0.####", ci) + " p50Ms=" +
                            p50.ToString("0.###", ci) + " p99Ms=" + p99.ToString("0.###", ci) + " maxMs=" +
                            _ftMax.ToString("0.###", ci) + " cpuMsPerFrame=" + cpu + " processCpuMsPerFrame=" + pcpu +
                            " vSyncCount=" + QualitySettings.vSyncCount + " targetFrameRate=" + Application.targetFrameRate +
                            " mirrorVisible=" + !_mirrorHidden + " views=" + Count(_mirror.Views);
            DeLog.Info("DE.S3 FRAMETIME " + LastFrameTime + " (S3.C11: compare with and without the block, delta < 0.3 ms; cpuMsPerFrame = main-thread CPU, " +
                       "insensitive to vsync and frame caps)");
            _ftTarget = 0;
            _ftThread0 = _ftProcess0 = -1;
        }

        private bool _mirrorHidden;

        private static int Count(IEnumerable<MirrorGridView> v) { int n = 0; foreach (MirrorGridView x in v) n++; return n; }

        // ------------------------------------------------------------------------------------------ per frame

        public void Tick(ulong now, bool worldReady)
        {
            if (!worldReady)
            {
                if (_run != null && _run.State != Step.Done && _run.State != Step.Failed) FailRun("the host world went away");
                _seamoth = null; // the scene took it (quit to menu)
                _shear = null;
                return;
            }
            if (_probe != null && _probe.Active) StepProbe(_probe, now);
            TickShear();
            TickFrameTime();
            if (_seamoth != null && _mirror.Marmoset.Failed) TryAdoptSeamothShader();
            if (_seamoth != null && now >= _nextKinematicMs)
            {
                _nextKinematicMs = now + 1000;
                Rigidbody rb = _seamoth.GetComponent<Rigidbody>();
                if (rb != null && !rb.isKinematic) { rb.isKinematic = true; DeLog.Warn("DE.S3 SEAMOTH was not kinematic any more: set again"); }
            }
            if (_run != null) StepRun(now);
        }

        private void FailRun(string why)
        {
            Run r = _run;
            r.State = Step.Failed;
            r.Error = why;
            if (r.Mode == "override" && !r.KeepCamera) { try { _s1.Cam.ClearOverride(); } catch (Exception) { } }
            DeLog.Warn("DE.S3 LAB FAILED at step " + (r.Index < r.Steps.Count ? r.Steps[r.Index].Label : "-") + ": " + why + " (captures ok " + r.Ok + ")");
        }

        private void StepRun(ulong now)
        {
            Run r = _run;
            switch (r.State)
            {
                case Step.Probe:
                {
                    if (!r.ProbeStarted)
                    {
                        r.ProbeStarted = true;
                        if (_probe != null && _probe.Done && _probe.X == r.X && _probe.Z == r.Z) { r.State = Step.WaitBlock; r.StepStartMs = now; return; }
                        _probe = new Probe { X = r.X, Z = r.Z, FromY = r.FromY, Active = true, StartMs = now, Warp = r.Mode == "override" };
                        if (_probe.Warp) _s1.Cam.SetOverride(new Vector3((float)r.X, (float)(r.FromY + 2), (float)r.Z), Quaternion.Euler(90f, (float)r.HeadingDeg, 0f));
                        return;
                    }
                    if (_probe.Active) return;
                    if (!_probe.Done) { FailRun(_probe.Error ?? "probe failed"); return; }
                    r.State = Step.WaitBlock;
                    r.StepStartMs = now;
                    return;
                }
                case Step.WaitBlock:
                {
                    MirrorGridView best = null;
                    double bestD = double.MaxValue;
                    foreach (MirrorGridView v in _mirror.Views)
                    {
                        if (!v.Alive || !v.HasPose || v.AnyRenderer() == null) continue;
                        if (r.EntityId != 0 && v.Id != r.EntityId) continue;
                        double dx = v.LastPose.Ox - r.X, dz = v.LastPose.Oz - r.Z;
                        double d = dx * dx + dz * dz;
                        if (d < bestD) { bestD = d; best = v; }
                    }
                    Vec3 b;
                    Vec3[] corners;
                    if (best != null && _mirror.Centre(best, out b, out corners))
                    {
                        r.Block = best;
                        StartPlan(r, b, now);
                        return;
                    }
                    if (now - r.StepStartMs > BlockTimeoutMs)
                    {
                        if (r.RequireBlock) { FailRun("no mirrored block with a pose within " + BlockTimeoutMs + " ms"); return; }
                        DeLog.Warn("DE.S3 LAB no mirrored block within " + BlockTimeoutMs + " ms: the plan uses the lab point at the seabed");
                        StartPlan(r, new Vec3(r.X, _probe.SeabedY, r.Z), now);
                    }
                    return;
                }
                case Step.Seamoth:
                {
                    if (_seamothSpawning)
                    {
                        if (now - r.StepStartMs > SeamothTimeoutMs) { FailRun("the Seamoth prefab did not load within " + SeamothTimeoutMs + " ms"); }
                        return;
                    }
                    if (_seamoth == null) DeLog.Warn("DE.S3 LAB continues without the reference Seamoth (" + (_seamothError ?? "not spawned") + ")");
                    r.State = Step.Move;
                    return;
                }
                case Step.Move:
                {
                    if (r.Index >= r.Steps.Count) { FinishRun(); return; }
                    CaptureStep st = r.Steps[r.Index];
                    if (st.Repeat)
                    {
                        if ((now - r.LastDoneMs) < (ulong)(st.MinGapS * 1000.0)) return;
                        r.State = Step.Capture;
                        return;
                    }
                    Vec3 pos;
                    Quat rot;
                    r.Plan.TryPose(st.Pose, out pos, out rot);
                    if (r.Mode == "override")
                    {
                        _s1.Cam.SetOverride(new Vector3((float)pos.X, (float)pos.Y, (float)pos.Z), new Quaternion((float)rot.X, (float)rot.Y, (float)rot.Z, (float)rot.W));
                        r.SettleFrame = Time.frameCount;
                        r.State = Step.Settle;
                        DeLog.Info("DE.S3 LAB " + st.Label + " camera " + Pose(pos, rot) + " (override), settling " + r.Frames + " frames");
                        return;
                    }
                    // c1: the producer's camera must come here (the harness drives SE's spectator / the fake producer)
                    if (r.StepStartMs == 0) { r.StepStartMs = now; DeLog.Info("DE.S3 LAB " + st.Label + " waiting for the C1 camera at " + Pose(pos, rot)); }
                    Camera cam = _s1.Cam.Camera;
                    if (cam != null)
                    {
                        double dm = (new Vec3(cam.transform.position.x, cam.transform.position.y, cam.transform.position.z) - pos).Length;
                        double dd = Quaternion.Angle(cam.transform.rotation, new Quaternion((float)rot.X, (float)rot.Y, (float)rot.Z, (float)rot.W));
                        if (dm <= C1PoseToleranceM && dd <= C1PoseToleranceDeg)
                        {
                            r.SettleFrame = Time.frameCount;
                            r.State = Step.Settle;
                            r.StepStartMs = 0;
                            return;
                        }
                    }
                    if (now - r.StepStartMs > C1PoseTimeoutMs) FailRun("the C1 camera did not reach " + st.Label + " within " + C1PoseTimeoutMs + " ms");
                    return;
                }
                case Step.Settle:
                    if (Time.frameCount - r.SettleFrame < r.Frames) return;
                    r.State = Step.Capture;
                    return;
                case Step.Capture:
                {
                    CaptureStep st = r.Steps[r.Index];
                    string rel = "s3\\" + r.Session + "\\" + st.Label + "_f" + Time.frameCount.ToString(CultureInfo.InvariantCulture) + ".png";
                    r.PendingSidecar = Sidecar(r, st);
                    if (_mirror.Mutants.M5 && st.Repeat && r.LastPng != null && File.Exists(r.LastPng))
                    {
                        string full = Path.Combine(CapturePaths.Dir(), rel);
                        File.Copy(r.LastPng, full, true);
                        if (File.Exists(r.LastPng + ".json")) File.Copy(r.LastPng + ".json", full + ".json", true);
                        DeLog.Warn("DE.S3 LAB " + st.Label + " MUTANT S3.M5: " + full + " is a copy of " + r.LastPng + " (no new frame captured)");
                        Captured(r, st, full, now);
                        return;
                    }
                    string fullPath, error;
                    r.Ok0 = _s1.Capture.Ok;
                    r.Failed0 = _s1.Capture.Failed;
                    if (!_s1.Capture.StartManual(now, rel, "S3." + st.Label, _snFrame(), out fullPath, out error)) { FailRun("capture refused: " + error); return; }
                    r.Full = fullPath;
                    r.StepStartMs = now;
                    r.State = Step.WaitCapture;
                    return;
                }
                case Step.WaitCapture:
                {
                    CaptureStep st = r.Steps[r.Index];
                    if (_s1.Capture.Ok > r.Ok0 && _s1.Capture.LastPath == r.Full) { Captured(r, st, r.Full, now); return; }
                    if (_s1.Capture.Failed > r.Failed0 && _s1.Capture.LastPath == r.Full)
                    {
                        r.FailedCount++;
                        DeLog.Warn("DE.S3 LAB " + st.Label + " capture FAILED (" + _s1.Capture.LastOutcome + "); next");
                        Next(r, now);
                        return;
                    }
                    if (now - r.StepStartMs > CaptureTimeoutMs) { r.FailedCount++; DeLog.Warn("DE.S3 LAB " + st.Label + " capture timed out; next"); Next(r, now); }
                    return;
                }
            }
        }

        private void StartPlan(Run r, Vec3 b, ulong now)
        {
            r.Plan = LabPlan.Make(b, _probe.SeabedY, r.HeadingDeg, LabPlan.SeamothSideM);
            var sb = new System.Text.StringBuilder();
            foreach (string n in LabPlan.PoseNames)
            {
                Vec3 pos;
                Quat rot;
                r.Plan.TryPose(n, out pos, out rot);
                double yaw, pitch;
                LabPlan.YawPitch(rot, out yaw, out pitch);
                sb.Append(' ').Append(n).Append("={pos=").Append(V3(pos)).Append(" yawDeg=").Append(F(yaw)).Append(" pitchDeg=").Append(F(pitch)).Append('}');
            }
            DeLog.Info("DE.S3 LAB plan block=" + (r.Block != null ? DeLog.Hex(r.Block.Id) : "none") + " B=" + V3(b) + " seabedY=" + F(_probe.SeabedY) +
                       " headingDeg=" + F(r.HeadingDeg) + " seamothSpot=" + V3(r.Plan.SeamothSpot) + sb + " (SN world; SE's de.teleport takes these for camera=c1)");
            r.StepStartMs = now;
            if (r.WantSeamoth && _seamoth != null)
            {
                Vector3 sp = _seamoth.transform.position;
                double dx = sp.x - r.Plan.SeamothSpot.X, dz = sp.z - r.Plan.SeamothSpot.Z;
                if (dx * dx + dz * dz > 1.0) { DeLog.Info("DE.S3 SEAMOTH from an earlier lab is elsewhere: re-spawned"); DestroySeamoth(); }
            }
            if (r.WantSeamoth && _seamoth == null && !_seamothSpawning)
            {
                Vec3 s = r.Plan.SeamothSpot;
                Quat q = Quat.LookRotation(r.Plan.R, Vec3.UnitY); // nose along R: seen in profile from P1
                StartSeamoth(new Vector3((float)s.X, (float)(b.Y + 1.0), (float)s.Z), new Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W), _probe.SeabedY);
                r.State = Step.Seamoth;
            }
            else r.State = r.WantSeamoth ? Step.Seamoth : Step.Move;
        }

        private void Captured(Run r, CaptureStep st, string full, ulong now)
        {
            try { File.WriteAllText(full + ".s3.json", r.PendingSidecar + "\n"); }
            catch (Exception e) { DeLog.Warn("DE.S3 sidecar not written: " + e.GetType().Name + ": " + e.Message); }
            r.Ok++;
            r.LastPng = full;
            r.Results.Add(full);
            DeLog.Info("DE.S3 CAPTURE " + st.Label + " png=" + full + " sidecar=" + full + ".s3.json");
            Next(r, now);
        }

        private void Next(Run r, ulong now)
        {
            r.LastDoneMs = now;
            r.Index++;
            r.StepStartMs = 0;
            r.State = Step.Move;
        }

        private void FinishRun()
        {
            Run r = _run;
            r.State = Step.Done;
            if (r.Mode == "override" && !r.KeepCamera) _s1.Cam.ClearOverride();
            DeLog.Info("DE.S3 LAB DONE session=" + r.Session + " captures ok=" + r.Ok + " failed=" + r.FailedCount + " dir=" +
                       Path.Combine(CapturePaths.Dir(), "s3\\" + r.Session) + (r.KeepCamera ? " (camera kept at the last pose)" : ""));
        }

        // ------------------------------------------------------------------------------------------ S3 sidecar

        [MethodImpl(MethodImplOptions.NoInlining)]
        private string Sidecar(Run r, CaptureStep st)
        {
            var j = new JsonOut();
            j.Str("slice", "S3").Str("checkId", "S3." + st.Label).Str("pose", st.Pose).Str("label", st.Label).Str("session", r.Session)
             .Num("unityFrame", (long)Time.frameCount).Num("snFrame", _snFrame()).Num("presentedSeTick", _s1.Cam.PresentedSeTick)
             .Str("cameraSource", r.Mode).Str("mutants", _mirror.Mutants.Describe() + " / " + DevMutants.Describe());
            j.Obj("lab").Num("x", r.X).Num("z", r.Z).Num("seabedY", _probe.SeabedY).Str("collider", _probe.Collider).Num("layer", (long)_probe.Layer)
             .Num("headingDeg", r.HeadingDeg).Vec("B", r.Plan.B).Vec("M", r.Plan.M).Vec("F", r.Plan.F).Vec("R", r.Plan.R).EndObj();
            Camera cam = MainCamera.camera;
            if (cam != null)
            {
                Vector3 cp = cam.transform.position;
                Quaternion cq = cam.transform.rotation;
                j.Obj("camera").Vec("pos", cp.x, cp.y, cp.z).Quat4("rot", cq.x, cq.y, cq.z, cq.w).Num("fov", cam.fieldOfView).Num("near", cam.nearClipPlane)
                 .Num("pixelWidth", (long)cam.pixelWidth).Num("pixelHeight", (long)cam.pixelHeight).Bool("enabled", cam.enabled)
                 .Bool("override", _s1.Cam.OverrideActive).Num("depth", Ocean.GetDepthOf(cp)).EndObj();
            }
            j.Num("timeScale", Time.timeScale).Bool("uiHidden", _s1.Ui.UiHidden);
            DayNightCycle dnc = DayNightCycle.main;
            if (dnc != null)
            {
                j.Obj("time").Num("dayScalar", dnc.GetDayScalar()).Num("timePassed", dnc.timePassedAsDouble).Bool("debugFreeze", dnc.debugFreeze)
                 .Num("lightScalar", dnc.GetLightScalar()).EndObj();
                // the sun (S3.C9 needs the anti-sun side): the Light under DayNightCycle, as the game finds it, else RenderSettings.sun
                Light sun = dnc.GetComponentInChildren<Light>();
                if (sun == null) sun = RenderSettings.sun;
                if (sun != null)
                {
                    Vector3 f = sun.transform.forward;
                    Color c = sun.color;
                    j.Obj("sun").Str("name", sun.name).Vec("forward", f.x, f.y, f.z).Num("intensity", sun.intensity).Vec("colour", c.r, c.g, c.b)
                     .Bool("enabled", sun.enabled && sun.gameObject.activeInHierarchy).Str("type", sun.type.ToString()).EndObj();
                }
            }
            if (r.Block != null && r.Block.Alive) BlockJson(j, r.Block, cam);
            if (_seamoth != null)
            {
                Bounds b;
                j.Obj("seamoth").Vec("pos", _seamoth.transform.position.x, _seamoth.transform.position.y, _seamoth.transform.position.z)
                 .Quat4("rot", _seamoth.transform.rotation.x, _seamoth.transform.rotation.y, _seamoth.transform.rotation.z, _seamoth.transform.rotation.w);
                if (RenderBounds(_seamoth, out b))
                {
                    j.Vec("boundsMin", b.min.x, b.min.y, b.min.z).Vec("boundsMax", b.max.x, b.max.y, b.max.z);
                    if (cam != null) ScreenRect(j, cam, BoxCorners(b));
                }
                j.EndObj();
            }
            return j.ToString();
        }

        private static Vec3[] BoxCorners(Bounds b)
        {
            return MirrorPoseMath.Corners(new Vec3(b.min.x, b.min.y, b.min.z), new Vec3(b.max.x, b.max.y, b.max.z));
        }

        private void BlockJson(JsonOut j, MirrorGridView v, Camera cam)
        {
            Vec3[] seLocal, world;
            double dev;
            j.Obj("block").Str("entityId", DeLog.Hex(v.Id)).Num("tick", v.LastTick).Str("poseHow", v.LastHow.ToString());
            EntityPose p = v.LastPose;
            j.Obj("pose").Vec("o", p.Ox, p.Oy, p.Oz).Quat4("q", p.Qx, p.Qy, p.Qz, p.Qw).Num("s", p.S).Num("k", p.K).EndObj();
            j.Num("vertices", (long)v.Vertices).Num("triangles", (long)v.Triangles);
            if (_mirror.Placement(v, out seLocal, out world, out dev))
            {
                // the 8 drawn probe vertices (S3.C2): SE-local positions and their drawn SN positions; min/max = their box
                Vec3 mn = seLocal[0], mx = seLocal[0];
                foreach (Vec3 l in seLocal)
                {
                    mn = new Vec3(Math.Min(mn.X, l.X), Math.Min(mn.Y, l.Y), Math.Min(mn.Z, l.Z));
                    mx = new Vec3(Math.Max(mx.X, l.X), Math.Max(mx.Y, l.Y), Math.Max(mx.Z, l.Z));
                }
                j.Vec("seLocalMin", mn).Vec("seLocalMax", mx).Num("maxDevVsApplyM", dev);
                j.Arr("seLocalCorners");
                foreach (Vec3 c in seLocal) { j.Arr(null).Num(null, c.X).Num(null, c.Y).Num(null, c.Z).EndArr(); }
                j.EndArr();
                j.Arr("cornersSn");
                foreach (Vec3 c in world) { j.Arr(null).Num(null, c.X).Num(null, c.Y).Num(null, c.Z).EndArr(); }
                j.EndArr();
                Vec3 centre = Vec3.Zero;
                foreach (Vec3 c in world) centre = centre + c;
                j.Vec("centre", centre / 8.0);
                if (cam != null)
                {
                    ScreenRect(j, cam, world);
                    // S3.C5 analytic half: a camera ray to each buried corner must hit the terrain (layer 30) first
                    j.Arr("buried");
                    int layer = TerrainLayer();
                    Vector3 cp = cam.transform.position;
                    for (int i = 0; i < 8; i++)
                    {
                        RaycastHit ground;
                        Vec3 c = world[i];
                        bool hasGround = Seabed(c.X, c.Z, c.Y + 50, out ground);
                        bool buried = hasGround && ground.point.y > c.Y;
                        if (!buried) continue;
                        var target = new Vector3((float)c.X, (float)c.Y, (float)c.Z);
                        Vector3 dir = target - cp;
                        float dist = dir.magnitude;
                        RaycastHit hit;
                        bool terrainFirst = Physics.Raycast(cp, dir / Math.Max(dist, 1e-6f), out hit, dist, 1 << layer, QueryTriggerInteraction.Ignore) && hit.distance < dist - 0.01f;
                        j.Obj(null).Num("corner", (long)i).Vec("sn", c).Num("groundY", ground.point.y).Bool("terrainFirst", terrainFirst)
                         .Num("hitDist", terrainFirst ? hit.distance : double.NaN).Num("cornerDist", dist).EndObj();
                    }
                    j.EndArr();
                }
            }
            MeshRenderer r = v.AnyRenderer();
            if (r != null)
            {
                Material m = r.sharedMaterial;
                SkyApplier sky = r.GetComponent<SkyApplier>();
                j.Obj("material").Str("describe", MarmosetMaterials.Describe(m)).Num("layer", (long)r.gameObject.layer).Str("shadows", r.shadowCastingMode.ToString())
                 .Bool("skyApplier", sky != null).Str("applySky", sky != null && sky.InspectorApplySky != null ? sky.InspectorApplySky.name : null).EndObj();
            }
            j.EndObj();
        }

        /// <summary>The screen rectangle (pixels, y from the top) of world points; "partial" when some are behind the camera.</summary>
        private static void ScreenRect(JsonOut j, Camera cam, Vec3[] pts)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            bool behind = false;
            float h = cam.pixelHeight;
            foreach (Vec3 p in pts)
            {
                Vector3 s = cam.WorldToScreenPoint(new Vector3((float)p.X, (float)p.Y, (float)p.Z));
                if (s.z <= 0) { behind = true; continue; }
                x0 = Math.Min(x0, s.x); x1 = Math.Max(x1, s.x);
                y0 = Math.Min(y0, h - s.y); y1 = Math.Max(y1, h - s.y);
            }
            j.Arr("screenRect").Num(null, x0).Num(null, y0).Num(null, x1).Num(null, y1).EndArr().Bool("screenRectPartial", behind);
        }

        // ------------------------------------------------------------------------------------------ commands

 /// <summary>de.s3 and de.time; false for any other command.</summary>
        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now, bool worldReady)
        {
            if (req.Cmd == "de.time") { Time_(req, reply); return true; }
            if (req.Cmd != "de.s3") return false;
            Dictionary<string, object> a = S3Args.ToDict(req.Args);
            string action = S3Args.Str(a, "action", null);
            if (action == null) action = a.ContainsKey("lab") ? "lab" : "status";
            reply.Str("cmd", req.Cmd).Str("action", action);
            switch (action)
            {
                case "status": Status(reply.Bool("ok", true)); return true;
                case "probe": ProbeCmd(a, reply, now, worldReady); return true;
                case "lab": LabCmd(a, reply, now, worldReady); return true;
                case "teardown":
                {
                    bool clear = S3Args.Flag(a, "clear", false);
                    if (_run != null && _run.State != Step.Done && _run.State != Step.Failed) FailRun("de.s3 teardown");
                    if (_run != null && _run.Mode == "override") _s1.Cam.ClearOverride();
                    DestroySeamoth();
                    if (_shear != null) { _shear.View.Destroy(_mirror.Owned); _shear = null; }
                    _mirror.Suppress(clear);
                    DeLog.Info("DE.S3 TEARDOWN the mirror's world-loss path run (as a quit to menu: every mirror object destroyed" +
                               (_mirror.Mutants.M6 ? ", except S3.M6's preserved roots" : "") + "), Seamoth removed" +
                               (clear ? ", mirror data cleared" : " (data kept: de.s3 rebuild brings it back)") + " meshesAlive=" + _mirror.Owned.MeshesAlive);
                    reply.Bool("ok", true).Bool("cleared", clear).Num("meshesAlive", (long)_mirror.Owned.MeshesAlive);
                    return true;
                }
                case "rebuild":
                    _mirror.Resume();
                    reply.Bool("ok", true);
                    return true;
                case "visible":
                {
                    bool on = S3Args.Flag(a, "on", true);
                    _mirrorHidden = !on;
                    _mirror.SetHidden(!on);
                    reply.Bool("ok", true).Bool("visible", on);
                    return true;
                }
                case "frametime":
                {
                    int n = (int)S3Args.Num(a, "frames", 600);
                    if (n < 10 || n > 100000) { reply.Bool("ok", false).Str("error", "frames: 10..100000"); return true; }
                    _ftTarget = n; _ftCount = 0; _ftSum = 0; _ftMax = 0; _ftSamples = new List<float>(n); _ftThread0 = _ftProcess0 = -1;
                    reply.Bool("ok", true).Bool("pending", true).Num("frames", (long)n).Str("note", "logged as DE.S3 FRAMETIME; also in de.s3 status");
                    return true;
                }
                case "count": CountCmd(reply); return true;
                case "placement":
                {
                    int n = 0;
                    foreach (MirrorGridView v in _mirror.Views) { DeLog.Info(_mirror.PlacementLine(v)); n++; }
                    reply.Bool("ok", true).Num("entities", (long)n).Str("note", "DE.Mirror placed lines in the log");
                    return true;
                }
                case "shear":
                {
                    if (!worldReady) { reply.Bool("ok", false).Str("error", "not in the host world"); return true; }
                    double s = S3Args.Num(a, "s", 1.0284), k = S3Args.Num(a, "k", 1.0);
                    int frames = (int)S3Args.Num(a, "frames", 360);
                    double ax, ay, az;
                    object av;
                    Vec3 axis = a.TryGetValue("axis", out av) && S3Args.TryVec(av, out ax, out ay, out az) && !double.IsNaN(ay) ? new Vec3(ax, ay, az) : new Vec3(1, 1, 0);
                    if (s < 0.9 || s > 1.1 || k <= 0 || k > 10 || frames < 1 || frames > 3600 || axis.Length < 1e-6) { reply.Bool("ok", false).Str("error", "s in [0.9, 1.1], k in (0, 10], frames 1..3600, axis non-zero"); return true; }
                    string err = StartShear(s, k, axis, frames);
                    reply.Bool("ok", err == null).Bool("pending", err == null).Str("note", "logged as DE.S3 SHEAR at the end");
                    if (err != null) reply.Str("error", err);
                    return true;
                }
                case "config":
                {
                    bool rebuild, reload;
                    string error;
                    if (!_mirror.Options.TryApply(a, out rebuild, out reload, out error)) { reply.Bool("ok", false).Str("error", error); return true; }
                    _mirror.ApplyOptions(rebuild, reload);
                    DeLog.Info("DE.S3 CONFIG " + _mirror.Options.Describe());
                    reply.Bool("ok", true).Str("options", _mirror.Options.Describe()).Bool("meshesRebuilt", rebuild).Bool("texturesReloaded", reload);
                    return true;
                }
                default:
                    reply.Bool("ok", false).Str("error", "unknown action '" + action + "' (status, probe, lab, teardown, rebuild, visible, frametime, count, placement, shear, config)");
                    return true;
            }
        }

        private void ProbeCmd(Dictionary<string, object> a, JsonObjectWriter reply, ulong now, bool worldReady)
        {
            double x = S3Args.Num(a, "x", double.NaN), z = S3Args.Num(a, "z", double.NaN);
            if (double.IsNaN(x) || double.IsNaN(z)) { reply.Bool("ok", false).Str("error", "args {x, z[, fromY, y, warp]} (SN world m)"); return; }
            if (!worldReady) { reply.Bool("ok", false).Str("error", "not in the host world"); return; }
            double y = S3Args.Num(a, "y", double.NaN);
            double fromY = S3Args.Num(a, "fromY", DefaultFromY(y));
            bool warp = S3Args.Flag(a, "warp", false);
            var p = new Probe { X = x, Z = z, FromY = fromY, StartMs = now, Active = true, Warp = warp };
            _probe = p;
            if (StepProbe(p, now))
            {
                reply.Bool("ok", true).Bool("pending", false).Num("seabedY", p.SeabedY).Str("collider", p.Collider).Num("layer", (long)p.Layer);
                return;
            }
            if (warp)
            {
                float camY = (float)(double.IsNaN(y) ? fromY : y + 5);
                _s1.Cam.SetOverride(new Vector3((float)x, camY, (float)z), Quaternion.Euler(90f, 0f, 0f));
                DeLog.Info("DE.S3 PROBE warp: camera held at (" + F(x) + ", " + F(camY) + ", " + F(z) + ") looking down until the terrain collider exists");
            }
            reply.Bool("ok", true).Bool("pending", true).Str("note", "DE.S3 PROBE in the log and seabedY in de.s3 status when the collider appears (timeout " + ProbeTimeoutMs + " ms)");
        }

        private void LabCmd(Dictionary<string, object> a, JsonObjectWriter reply, ulong now, bool worldReady)
        {
            if (!worldReady) { reply.Bool("ok", false).Str("error", "not in the host world"); return; }
            if (_run != null && _run.State != Step.Done && _run.State != Step.Failed) { reply.Bool("ok", false).Str("error", "a lab run is in progress (de.s3 teardown stops it)"); return; }
            object lab;
            double x, y, z;
            if (!a.TryGetValue("lab", out lab) || !S3Args.TryVec(lab, out x, out y, out z))
            {
                x = S3Args.Num(a, "x", double.NaN);
                z = S3Args.Num(a, "z", double.NaN);
                y = S3Args.Num(a, "y", double.NaN);
            }
            if (double.IsNaN(x) || double.IsNaN(z)) { reply.Bool("ok", false).Str("error", "args {lab: [x, z] | {x, z}, seamoth, captures, frames, headingDeg, camera, session}"); return; }
            object caps;
            a.TryGetValue("captures", out caps);
            List<CaptureStep> steps;
            string error;
            if (!CaptureList.TryParse(caps, out steps, out error)) { reply.Bool("ok", false).Str("error", error); return; }
            string mode = S3Args.Str(a, "camera", "override");
            if (mode != "override" && mode != "c1") { reply.Bool("ok", false).Str("error", "camera: override | c1"); return; }
            if (mode == "override" && !_s1.Cam.Installed) { reply.Bool("ok", false).Str("error", "the camera driver is not installed"); return; }
            string session = S3Args.Str(a, "session", null) ?? "lab-" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string norm;
            if (!CapturePaths.TryNormalise("s3\\" + session + "\\x.png", CaptureService.MaxPathBytes - 40, out norm, out error)) { reply.Bool("ok", false).Str("error", "session: " + error); return; }
            ulong entity = 0;
            string es = S3Args.Str(a, "entity", null);
            if (es != null)
            {
                string hex = es.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? es.Substring(2) : es;
                if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out entity)) { reply.Bool("ok", false).Str("error", "entity: a hex id"); return; }
            }
            _run = new Run
            {
                X = x, Z = z, HeadingDeg = S3Args.Num(a, "headingDeg", 0), FromY = S3Args.Num(a, "fromY", DefaultFromY(y)),
                WantSeamoth = S3Args.Flag(a, "seamoth", true), RequireBlock = S3Args.Flag(a, "requireBlock", true), KeepCamera = S3Args.Flag(a, "keepCamera", false),
                Frames = (int)Math.Max(1, Math.Min(10000, S3Args.Num(a, "frames", 90))), Mode = mode, Session = session, EntityId = entity, Steps = steps,
                State = Step.Probe,
            };
            var labels = new List<string>();
            foreach (CaptureStep s in steps) labels.Add(s.Label);
            DeLog.Info("DE.S3 LAB start session=" + session + " lab=(" + F(x) + ", " + F(z) + ") headingDeg=" + F(_run.HeadingDeg) + " seamoth=" + _run.WantSeamoth +
                       " camera=" + mode + " frames=" + _run.Frames + " captures=" + string.Join(",", labels.ToArray()) + " mutants=" + _mirror.Mutants.Describe());
            reply.Bool("ok", true).Bool("pending", true).Str("session", session).Num("captures", (long)steps.Count)
                 .Str("dir", Path.Combine(CapturePaths.Dir(), "s3\\" + session))
                 .Str("note", "progress: DE.S3 LAB / PROBE / SEAMOTH / CAPTURE lines and de.s3 status; DE.S3 LAB plan gives the camera poses");
        }

        /// <summary>
 /// The probe ray's start height: 20 m above the given lab height, else 20 m above the camera (the producer's camera
 /// is normally at the lab spot; terrain colliders exist only near the camera), else −5 m.
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double DefaultFromY(double y)
        {
            if (!double.IsNaN(y)) return y + 20.0;
            Camera c = MainCamera.camera;
            return c != null ? c.transform.position.y + 20.0 : -5.0;
        }

        private void CountCmd(JsonObjectWriter reply)
        {
            int roots = 0, preserved = 0;
            foreach (MirrorGridView v in _mirror.Views) if (v.Alive) { roots++; if (v.Preserved) preserved++; }
            int strays = 0;
            int total = -1;
            try
            {
                foreach (MirrorRootMarker m in Resources.FindObjectsOfTypeAll<MirrorRootMarker>()) if (m != null && m.gameObject.scene.IsValid()) strays++;
                total = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            }
            catch (Exception) { }
            DeLog.Info("DE.S3 COUNT rootsAlive=" + roots + " rootMarkersInScenes=" + strays + " preserved=" + preserved + " seamoth=" + SeamothAlive +
                       " ourMeshesAlive=" + _mirror.Owned.MeshesAlive + " ourOtherObjectsAlive=" + _mirror.Owned.OthersAlive + " meshesTotal=" + total +
                       " (S3.C12: after quit to menu, roots 0 and our meshes 0; meshesTotal back to the baseline)");
            reply.Bool("ok", true).Num("rootsAlive", (long)roots).Num("rootMarkersInScenes", (long)strays).Num("preserved", (long)preserved).Bool("seamoth", SeamothAlive)
                 .Num("ourMeshesAlive", (long)_mirror.Owned.MeshesAlive).Num("ourOtherObjectsAlive", (long)_mirror.Owned.OthersAlive).Num("meshesTotal", (long)total)
                 .Num("meshesCreated", _mirror.Owned.MeshesCreated).Num("meshesDestroyed", _mirror.Owned.MeshesDestroyed).Num("rootsDestroyedByScene", _mirror.RootsDestroyedByScene);
        }

        private void Time_(ControlRequest req, JsonObjectWriter reply)
        {
            Dictionary<string, object> a = S3Args.ToDict(req.Args);
            double t = S3Args.Num(a, "t", double.NaN);
            if (double.IsNaN(t))
            {
                var list = S1Args.Unwrap(req.Args) as List<object>;
                if (list != null && list.Count > 0) S1Args.TryDouble(list[0], out t);
            }
            reply.Str("cmd", req.Cmd);
            DayNightCycle dnc = DayNightCycle.main;
            if (dnc == null) { reply.Bool("ok", false).Str("error", "DayNightCycle.main is null (not in the host world)"); return; }
            if (!double.IsNaN(t))
            {
                if (t < 0 || t > 1) { reply.Bool("ok", false).Str("error", "t: a day fraction in [0, 1] (0.5 noon, 0 midnight)"); return; }
                // S7a hook (CONTRACT §5.4 "Time drive"): refused while SE's sun holds Subnautica's clock
                if (DayNightApply.HoldsClock)
                {
                    DeLog.Info("DE.Time REFUSE de.time t=" + F(t) + ": the S7a time drive holds Subnautica's clock (SE's sun via C6; SE `de.s7 time sync off` first)");
                    reply.Bool("ok", false).Str("error", "DE.Time REFUSE: the S7a time drive holds Subnautica's clock (SE: de.s7 {time: {sync: off}} first)")
                         .Num("dayScalar", (double)dnc.GetDayScalar()).Num("timePassed", dnc.timePassedAsDouble);
                    return;
                }
                float before = dnc.GetDayScalar();
                dnc.SetDayNightTime((float)t);
                object fz;
                bool freeze;
                if (a.TryGetValue("freeze", out fz) && S3Args.TryBool(fz, out freeze)) dnc.debugFreeze = freeze;
                DeLog.Info("DE.S3 TIME t=" + F(t) + " dayScalar " + before.ToString("0.####", CultureInfo.InvariantCulture) + " -> " +
                           dnc.GetDayScalar().ToString("0.####", CultureInfo.InvariantCulture) + " timePassed=" + dnc.timePassedAsDouble.ToString("0.###", CultureInfo.InvariantCulture) +
                           " debugFreeze=" + dnc.debugFreeze + " (DayNightCycle.SetDayNightTime; never the console's daynight)");
            }
            reply.Bool("ok", true).Num("dayScalar", (double)dnc.GetDayScalar()).Num("timePassed", dnc.timePassedAsDouble).Bool("debugFreeze", dnc.debugFreeze);
        }

        public void Status(JsonObjectWriter w)
        {
            Probe p = _probe;
            if (p != null)
            {
                w.Str("probe", p.Active ? "pending" : p.Done ? "ok" : "failed").Num("probeX", p.X).Num("probeZ", p.Z).Num("seabedY", p.SeabedY)
                 .Str("probeCollider", p.Collider ?? "").Num("probeLayer", (long)p.Layer).Str("probeError", p.Error ?? "");
            }
            Run r = _run;
            if (r != null)
            {
                w.Str("lab", r.State.ToString()).Str("labSession", r.Session).Num("labIndex", (long)r.Index).Num("labSteps", (long)r.Steps.Count)
                 .Num("labOk", (long)r.Ok).Num("labFailed", (long)r.FailedCount).Str("labError", r.Error ?? "")
                 .Str("labBlock", r.Block != null ? DeLog.Hex(r.Block.Id) : "").Str("labLast", r.LastPng ?? "");
                if (r.Plan != null) w.Num("labBx", r.Plan.B.X).Num("labBy", r.Plan.B.Y).Num("labBz", r.Plan.B.Z);
            }
            w.Bool("seamoth", SeamothAlive).Str("seamothError", _seamothError ?? "").Str("lastShear", LastShear ?? (_shear != null ? "running" : ""))
             .Str("lastFrameTime", LastFrameTime ?? (_ftTarget > 0 ? "running" : ""));
            _mirror.Status(w);
        }

        /// <summary>The bridge stops or the world goes: the lab's own objects (the mirror tears down its own).</summary>
        public void Dispose()
        {
            try { DestroySeamoth(); } catch (Exception) { }
            try { if (_shear != null) _shear.View.Destroy(_mirror.Owned); } catch (Exception) { }
            _shear = null;
        }

        // ------------------------------------------------------------------------------------------ helpers

        private static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
        private static string V(Vector3 v) { return "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")"; }
        private static string Q(Quaternion q) { return "(" + q.x.ToString("0.####", CultureInfo.InvariantCulture) + "," + q.y.ToString("0.####", CultureInfo.InvariantCulture) + "," + q.z.ToString("0.####", CultureInfo.InvariantCulture) + "," + q.w.ToString("0.####", CultureInfo.InvariantCulture) + ")"; }
        private static string V3(Vec3 v) { return "(" + F(v.X) + "," + F(v.Y) + "," + F(v.Z) + ")"; }

        private static string Pose(Vec3 p, Quat q)
        {
            double yaw, pitch;
            LabPlan.YawPitch(q, out yaw, out pitch);
            return "pos=" + V3(p) + " yawDeg=" + F(yaw) + " pitchDeg=" + F(pitch);
        }
    }
}
