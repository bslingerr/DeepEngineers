using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S4 "Planet + terrain", the Subnautica side (ROADMAP S4 "SN plugin"): a raycast service for the oracles S4.A-T2 and
 /// S4.WM-6 over the C11 control channel (dev builds only, like every C11 command; no wire traffic, no layout change).
 /// <list type="bullet">
 /// <item><c>de.s4 {probe: [[x, z] | [x, z, yHint], …], hold?: true, fromY?, holdAbove?, descend?, minY?, hintSpan?,
 /// timeoutMs?, mesh?: true, id?}</c> starts a batch (reply pending). Column by column, the S1 camera override holds
 /// Subnautica's camera above the column, looking down (terrain colliders exist only within about ±16 m of the camera,
 /// S1.N3), and after <c>Physics.SyncTransforms</c> a layer-30 (TerrainCollider) ray is cast down at (x, z) through
 /// <see cref="LabScene.Seabed"/>; the hold ladder, the "built" checks of the collision clipmap and the acceptance rule
 /// are <see cref="S4Rules"/>. With <c>mesh</c>, the hit is also measured against the render mesh of the clipmap chunk
 /// that owns the collider (its <c>hiFilters</c>; a vertical ray/triangle test in world space through each filter's own
 /// transform): <c>renderY</c> − <c>y</c> is the collider-vs-render deviation (the native budget of S4.A-T2).</item>
 /// <item><c>de.s4 {status: true, from?, max?, all?}</c>: the finished columns from <c>from</c> (pending/holding columns are not
 /// listed unless <c>all: true</c>; "current" is the column in progress). <c>de.s4 {cancel: true}</c> stops the batch; <c>de.s4 {teardown: true}</c>
 /// also releases the camera (the override is released at the end of every batch anyway).</item>
 /// </list>
 /// Per column: the log line <c>DE.S4 PROBE …</c> and the JSONL row <c>s4.probe</c>; per batch <c>DE.S4 BATCH …</c> and
 /// <c>s4.batch</c>. The service never moves the Player itself (the S1 ghost follows the camera override, as for
 /// de.warpcam and the S3 probe warp), never saves, never touches the UI. Its steps run in one feature guard: the first
 /// exception disables S4 only (logged once), the batch fails and the camera is released.
 /// <para>
 /// Members: <c>Physics.SyncTransforms</c>, <c>Physics.Raycast</c>
 /// (LabScene.Seabed), <c>LargeWorldStreamer.main</c>, <c>LargeWorldStreamer.streamerV2</c> (field, WorldStreamer),
 /// <c>WorldStreamer.clipmapStreamer</c>, <c>ClipmapStreamer.IsRangeActiveAndBuilt(Int3.Bounds)</c> (the collision level;
 /// a cell counts once <c>ActivateChunkAndCollider</c> has activated its collider), <c>LargeWorldStreamer.GetBlock(Vector3)</c>,
 /// <c>Int3.MinMax(Int3, Int3)</c>; <c>WorldStreaming.ClipmapChunk</c> fields <c>collision</c> (MeshCollider),
 /// <c>hiFilters</c> (List&lt;MeshFilter&gt), <c>hiRenders</c> (List&lt;MeshRenderer&gt); Mesh <c>isReadable</c>,
 /// <c>subMeshCount</c>, <c>GetTopology(int)</c>, <c>GetVertices(List&lt;Vector3&gt)</c>, <c>GetIndices(List&lt;int&gt;, int)</c>;
 /// <c>Transform.localToWorldMatrix</c>, <c>Matrix4x4.MultiplyPoint3x4</c>. The collider-to-chunk link: the clipmap path
 /// (<c>VoxelandCollisionMeshSimplifier.AttachTo</c>) takes a pooled "ChunkCollider" piece from <c>TerrainPoolManager</c>,
 /// which parents it to the chunk only when <c>useFlatHierarchy</c> is false (default true: the piece stays under the
 /// pool manager); <c>VoxelandChunk.EnsureCollision</c>/<c>AttachCollision</c> parent it to the chunk. So the chunk is
 /// the collider's parent when that holds ("renderVia": "parent"), else the active <c>ClipmapChunk</c> whose
 /// <c>collision</c> is the hit collider ("scan").
 /// </para>
 /// </summary>
    internal sealed class S4Host : IDisposable
    {
        private readonly S1Host _s1;
        private readonly HashSet<string> _disabled = new HashSet<string>(StringComparer.Ordinal);
        private bool _started;
        private bool _ownOverride;
        private static int _seq;

        private S4Batch _batch;

        // the column in progress
        private bool _colActive;
        private int _col = -1;
        private int _rung;
        private int _builtFrames;
        private double _camY, _originY;
        private ulong _colStartMs, _rungStartMs;

        // per-frame step over fields (no closure allocated per frame); the render pass reads _hit/_hitResult
        private ulong _now;
        private RaycastHit _hit;
        private S4ColumnResult _hitResult;
        private readonly Action _stepProbe, _stepRender;

        // render pass buffers (reused)
        private readonly List<Vector3> _verts = new List<Vector3>(4096);
        private readonly List<int> _idx = new List<int>(16384);
        private Vector3[] _world = new Vector3[4096];
        private readonly S4RenderPick _pick = new S4RenderPick();

        private static readonly Quaternion LookDown = Quaternion.Euler(90f, 0f, 0f);

        public S4Host(S1Host s1)
        {
            _s1 = s1;
            _stepProbe = Step;
            _stepRender = () => RenderPass(_hitResult, _hit);
        }

        public void Start()
        {
            _started = true;
            DeLog.Info("DE.S4 start: raycast service de.s4 (layer-30 TerrainCollider rays after Physics.SyncTransforms, camera holds through the S1 " +
                       "override, render-mesh rays for the native budget; " + S4Rules.Usage + ")");
        }

        // ------------------------------------------------------------------------------------------ per frame

        public void Tick(ulong now)
        {
            if (!_started) return;
            S4Batch b = _batch;
            if (b == null || !b.Running) return;
            _now = now;
            Guard("probe", _stepProbe);
            if (_disabled.Contains("probe") && b.Running)
            {
                EndBatch(b, "failed", "the S4 probe feature is disabled after an exception (DE.S4 FEATURE in the log)", now);
            }
        }

        /// <summary>The host world is up: S1's ghost world set up, the Player and LargeWorld exist, no main menu.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool WorldReady()
        {
            if (_s1 == null || !_s1.InWorld) return false;
            return Player.main != null && LargeWorld.main != null && uGUI_MainMenu.main == null && MainCamera.camera != null;
        }

        private bool CameraUsable { get { return _s1 != null && _s1.Cam.Installed && !_s1.Cam.Failed; } }

        private void Step()
        {
            S4Batch b = _batch;
            ulong now = _now;
            if (!WorldReady()) { EndBatch(b, "failed", "the host world went away", now); return; }
            if (b.Options.Hold && !CameraUsable) { EndBatch(b, "failed", "the S1 camera driver is not installed or failed: no camera hold", now); return; }
            if (!_colActive)
            {
                if (b.Next >= b.Columns.Count) { EndBatch(b, "done", null, now); return; }
                StartColumn(b, b.Next, now);
            }
            S4Column c = b.Columns[_col];
            S4ColumnResult r = b.Results[_col];
            S4Options o = b.Options;

            double camY = o.Hold ? _camY : CameraY();
            if (double.IsNaN(camY)) { FinishMiss(b, r, "no main camera", now); return; }

            Physics.SyncTransforms(); // colliders follow this frame's activations and moves before the ray
            RaycastHit hit;
            bool got = LabScene.Seabed(c.X, c.Z, _originY, out hit);
            double hy = got ? hit.point.y : double.NaN;
            bool boxBuilt = ClipBuilt(c.X, c.Z, camY - S4Rules.BoxHalf, camY + S4Rules.BoxHalf);
            _builtFrames = boxBuilt ? _builtFrames + 1 : 0;
            bool spanBuilt = false;
            if (got)
            {
                double lo, hi;
                S4Rules.SpanY(hy, camY, out lo, out hi);
                spanBuilt = ClipBuilt(c.X, c.Z, lo, hi);
            }
            double next;
            bool last = !S4Rules.TryNextCamY(c, o, _camY, out next);
            ulong elapsed = now - _rungStartMs;
            S4Step st = S4Rules.Decide(got, hy, camY, boxBuilt, spanBuilt, _builtFrames, elapsed, o.TimeoutMs, last);
            switch (st)
            {
                case S4Step.Wait:
                    return;
                case S4Step.Accept:
                case S4Step.AcceptUnverified:
                    FinishHit(b, r, hit, camY, st == S4Step.Accept, now);
                    return;
                case S4Step.NextRung:
                {
                    string why = got ? "hit " + S4Rules.F(hy) + " below the box, span not built" : boxBuilt ? "box built and empty" : "timeout " + elapsed + " ms, box not built";
                    DeLog.Info(S4Rules.DescendLine(b.Id, c, _rung, _camY, next, why));
                    _rung++;
                    _camY = next;
                    _rungStartMs = now;
                    _builtFrames = 0;
                    Hold(c.X, _camY, c.Z);
                    return;
                }
                default:
                    FinishMiss(b, r, "no TerrainCollider under (" + S4Rules.F(c.X) + ", " + S4Rules.F(c.Z) + ")" +
                                     (o.Hold ? " with the camera held from " + S4Rules.F(S4Rules.FirstCamY(c, o)) + " down to " + S4Rules.F(_camY) + " m (" + (_rung + 1) + " holds of up to " + o.TimeoutMs + " ms)"
                                             : " within " + o.TimeoutMs + " ms (hold: false, the camera stays where it is: colliders exist only within about 16 m of it)"), now);
                    return;
            }
        }

        private void StartColumn(S4Batch b, int index, ulong now)
        {
            S4Column c = b.Columns[index];
            S4ColumnResult r = b.Results[index];
            _col = index;
            _colActive = true;
            _rung = 0;
            _builtFrames = 0;
            _camY = S4Rules.FirstCamY(c, b.Options);
            _originY = S4Rules.OriginY(c, b.Options);
            _colStartMs = _rungStartMs = now;
            r.State = "holding";
            r.OriginY = _originY;
            if (b.Options.Hold) Hold(c.X, _camY, c.Z);
        }

        private void Hold(double x, double y, double z)
        {
            _s1.Cam.SetOverride(new Vector3((float)x, (float)y, (float)z), LookDown);
            _ownOverride = true;
        }

        private void FinishHit(S4Batch b, S4ColumnResult r, RaycastHit hit, double camY, bool verified, ulong now)
        {
            r.Hit = true;
            r.Verified = verified;
            r.Y = hit.point.y;
            r.Collider = hit.collider != null ? hit.collider.name : "?";
            r.Layer = hit.collider != null ? hit.collider.gameObject.layer : -1;
            r.CamY = camY;
            r.Rungs = _rung + 1;
            r.WaitMs = (long)(now - _colStartMs);
            r.State = "ok";
            if (b.Options.Mesh)
            {
                if (_disabled.Contains("render")) r.RenderNote = "render pass disabled after an exception";
                else
                {
                    _hit = hit;
                    _hitResult = r;
                    Guard("render", _stepRender);
                    if (_disabled.Contains("render") && r.RenderNote == null) r.RenderNote = "render pass threw (DE.S4 FEATURE render DISABLED)";
                    _hitResult = null;
                }
            }
            else r.RenderNote = "mesh: false";
            Emit(b, r);
            b.Complete(r);
            _colActive = false;
        }

        private void FinishMiss(S4Batch b, S4ColumnResult r, string error, ulong now)
        {
            r.Hit = false;
            r.State = "failed";
            r.Error = error;
            r.CamY = b.Options.Hold ? _camY : CameraY();
            r.Rungs = _rung + 1;
            r.WaitMs = (long)(now - _colStartMs);
            Emit(b, r);
            b.Complete(r);
            _colActive = false;
        }

        private static void Emit(S4Batch b, S4ColumnResult r)
        {
            string line = S4Rules.ProbeLine(b.Id, r);
            if (r.Hit) DeLog.Info(line); else DeLog.Warn(line);
            Row(S4Rules.ProbeRow(b.Id, r, Utc(), Win32.TickCount64(), DeLog.Frame));
        }

        private void EndBatch(S4Batch b, string state, string error, ulong now)
        {
            if (b == null || !b.Running) return;
            if (_colActive && _col >= 0 && _col < b.Results.Length)
            {
                S4ColumnResult r = b.Results[_col];
                if (!r.Finished) { r.State = "cancelled"; r.Error = state + (error != null ? ": " + error : ""); r.CamY = _camY; r.Rungs = _rung + 1; r.WaitMs = (long)(now - _colStartMs); }
            }
            _colActive = false;
            b.End(state, error, now);
            string line = b.EndLine();
            if (state == "done") DeLog.Info(line); else DeLog.Warn(line);
            Row(b.BatchRow("end", Utc(), Win32.TickCount64(), DeLog.Frame));
            Release(state);
        }

        /// <summary>Releases the camera override if this service set it (C1 drives the camera again).</summary>
        private void Release(string why)
        {
            if (!_ownOverride) return;
            _ownOverride = false;
            try
            {
                if (_s1 != null) _s1.Cam.ClearOverride();
                DeLog.Info("DE.S4 camera released (" + why + "): the S1 override is cleared, C1 drives the camera again");
            }
            catch (Exception e)
            {
                DeLog.Warn("DE.S4 camera release: " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ------------------------------------------------------------------------------------------ clipmap and camera

 /// <summary>
 /// The collision clipmap level reports every cell over [x ± 1, lo..hi, z ± 1] loaded (its colliders activated), as
 /// S1Telemetry asks it for C7 areaBuilt. False when the streamer is not there.
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ClipBuilt(double x, double z, double lo, double hi)
        {
            LargeWorldStreamer lws = LargeWorldStreamer.main;
            if (lws == null || lws.land == null) return false; // GetBlock reads land.transform
            global::WorldStreaming.WorldStreamer ws = lws.streamerV2;
            if (ws == null || ws.clipmapStreamer == null) return false;
            var min = new Vector3((float)(x - 1.0), (float)Math.Min(lo, hi), (float)(z - 1.0));
            var max = new Vector3((float)(x + 1.0), (float)Math.Max(lo, hi), (float)(z + 1.0));
            return ws.clipmapStreamer.IsRangeActiveAndBuilt(Int3.MinMax(lws.GetBlock(min), lws.GetBlock(max)));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double CameraY()
        {
            Camera c = MainCamera.camera;
            return c != null ? c.transform.position.y : double.NaN;
        }

        // ------------------------------------------------------------------------------------------ render pass

 /// <summary>
 /// The mesh-only ray: the vertical line at the column against the render triangles of the clipmap chunk that owns
 /// the hit collider (every hi filter = one per used block type, the blend layers repeat the surface), in world space
 /// through each filter's own transform (flat hierarchy or not). renderY = the crossing nearest to the collider hit.
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RenderPass(S4ColumnResult r, RaycastHit hit)
        {
            Collider col = hit.collider;
            if (col == null) { r.RenderNote = "no collider on the hit"; return; }
            string via;
            global::WorldStreaming.ClipmapChunk chunk = ChunkOf(col, out via);
            if (chunk == null)
            {
                Transform p = col.transform.parent;
                r.RenderNote = "no active ClipmapChunk owns the collider (parent " + (p != null ? p.name : "none") + ")";
                return;
            }
            r.RenderVia = via;
            r.RenderVisible = chunk.hiRenders.Count > 0 && chunk.hiRenders[0] != null && chunk.hiRenders[0].enabled;
            _pick.Reset(r.OriginY, r.Y);
            int filters = 0, unreadable = 0, other = 0;
            double px = r.X, pz = r.Z;
            for (int f = 0; f < chunk.hiFilters.Count; f++)
            {
                MeshFilter mf = chunk.hiFilters[f];
                if (mf == null) continue;
                Mesh m = mf.sharedMesh;
                if (m == null) continue;
                filters++;
                if (!m.isReadable) { unreadable++; continue; }
                _verts.Clear();
                m.GetVertices(_verts);
                int nv = _verts.Count;
                if (_world.Length < nv) _world = new Vector3[Math.Max(nv, _world.Length * 2)];
                Matrix4x4 l2w = mf.transform.localToWorldMatrix;
                for (int v = 0; v < nv; v++) _world[v] = l2w.MultiplyPoint3x4(_verts[v]);
                for (int s = 0; s < m.subMeshCount; s++)
                {
                    if (m.GetTopology(s) != MeshTopology.Triangles) { other++; continue; }
                    _idx.Clear();
                    m.GetIndices(_idx, s);
                    for (int t = 0; t + 2 < _idx.Count; t += 3)
                    {
                        int ia = _idx[t], ib = _idx[t + 1], ic = _idx[t + 2];
                        if ((uint)ia >= (uint)nv || (uint)ib >= (uint)nv || (uint)ic >= (uint)nv) continue;
                        Vector3 a = _world[ia], bb = _world[ib], cc = _world[ic];
                        // XZ bounding-box reject before the barycentric test
                        if ((px < a.x && px < bb.x && px < cc.x) || (px > a.x && px > bb.x && px > cc.x) ||
                            (pz < a.z && pz < bb.z && pz < cc.z) || (pz > a.z && pz > bb.z && pz > cc.z)) continue;
                        _pick.AddTriangle(px, pz, a.x, a.y, a.z, bb.x, bb.y, bb.z, cc.x, cc.y, cc.z);
                    }
                }
            }
            r.RenderFilters = filters;
            r.RenderHits = _pick.Hits;
            r.RenderUp = _pick.Up;
            r.RenderDown = _pick.Down;
            r.RenderY = _pick.NearestY;
            r.RenderFirstY = _pick.FirstY;
            if (filters == 0) r.RenderNote = "the chunk has no render mesh";
            else if (unreadable == filters) r.RenderNote = "render meshes not readable (" + unreadable + ")";
            else if (_pick.Hits == 0) r.RenderNote = "no render triangle under the column in " + filters + " filters" + (other > 0 ? " (" + other + " non-triangle submeshes)" : "");
            else if (unreadable > 0 || other > 0) r.RenderNote = unreadable + " unreadable filters, " + other + " non-triangle submeshes skipped";
        }

        /// <summary>The clipmap chunk whose collider is <paramref name="col"/>: its parent (EnsureCollision, non-flat pools), else a scan of the active chunks.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static global::WorldStreaming.ClipmapChunk ChunkOf(Collider col, out string via)
        {
            int id = col.GetInstanceID();
            Transform p = col.transform.parent;
            if (p != null)
            {
                global::WorldStreaming.ClipmapChunk c = p.GetComponent<global::WorldStreaming.ClipmapChunk>();
                if (c != null && c.collision != null && c.collision.GetInstanceID() == id) { via = "parent"; return c; }
            }
            foreach (global::WorldStreaming.ClipmapChunk c in UnityEngine.Object.FindObjectsOfType<global::WorldStreaming.ClipmapChunk>())
            {
                if (c != null && c.collision != null && c.collision.GetInstanceID() == id) { via = "scan"; return c; }
            }
            via = null;
            return null;
        }

        // ------------------------------------------------------------------------------------------ C11

        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req.Cmd != S4Rules.Cmd) return false;
            S4Request q;
            string error;
            if (!S4Rules.TryParse(req.Args, out q, out error))
            {
                reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", error);
                return true;
            }
            if (q.Kind == S4Kind.Probe && _disabled.Contains("probe"))
            {
                // status, cancel and teardown still answer: the last batch's results stay readable
                reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", "the S4 raycast service is disabled after an exception (see DE.S4 FEATURE in the log)");
                return true;
            }
            switch (q.Kind)
            {
                case S4Kind.Probe: StartCmd(q, reply, now); break;
                case S4Kind.Status: StatusCmd(q, reply, now); break;
                default: CancelCmd(q.Kind == S4Kind.Teardown, reply, now); break;
            }
            return true;
        }

        private void StartCmd(S4Request q, JsonObjectWriter reply, ulong now)
        {
            S4Batch cur = _batch;
            if (cur != null && cur.Running)
            {
                reply.Bool("ok", false).Str("cmd", S4Rules.Cmd).Str("batch", cur.Id).Str("error", "batch " + cur.Id + " is running (de.s4 {\"cancel\": true} stops it)");
                return;
            }
            bool ready;
            try { ready = WorldReady(); }
            catch (Exception) { ready = false; }
            if (!ready)
            {
                reply.Bool("ok", false).Str("cmd", S4Rules.Cmd).Str("error", "not in the host world (the S1 ghost world, the Player and LargeWorld are needed)");
                return;
            }
            if (q.Options.Hold && !CameraUsable)
            {
                reply.Bool("ok", false).Str("cmd", S4Rules.Cmd).Str("error", "the S1 camera driver is not installed or failed: no camera hold (hold: false casts near the current camera only)");
                return;
            }
            string id = q.Options.Id ?? "s4-" + (++_seq).ToString(CultureInfo.InvariantCulture);
            var b = new S4Batch(id, q.Options, q.Columns, now);
            b.OverrideWasActive = _s1 != null && _s1.Cam.OverrideActive && !_ownOverride;
            _batch = b;
            _colActive = false;
            _col = -1;
            if (b.OverrideWasActive)
                DeLog.Warn("DE.S4 the camera override was already set (de.warpcam or de.s3 warp): " +
                           (q.Options.Hold ? "this batch takes it over and clears it at the end" : "hold: false leaves it as it is (the rays are cast near that camera)"));
            DeLog.Info(b.StartLine());
            Row(b.BatchRow("start", Utc(), Win32.TickCount64(), DeLog.Frame));
            b.WriteStart(reply);
        }

        private void StatusCmd(S4Request q, JsonObjectWriter reply, ulong now)
        {
            S4Batch b = _batch;
            if (b == null) { S4Rules.WriteIdle(reply); return; }
            string current = null;
            if (b.Running && _colActive && _col >= 0 && _col < b.Columns.Count)
                current = S4Rules.CurrentJson(b.Columns[_col], _rung, b.Options.Hold ? _camY : double.NaN, _originY, now - _colStartMs, _builtFrames);
            b.WriteStatus(reply, q.From, q.Max, now, current, q.All);
            reply.Bool("cameraHeld", _ownOverride).Str("featuresDisabled", _disabled.Count == 0 ? "none" : string.Join(",", new List<string>(_disabled).ToArray()));
        }

        private void CancelCmd(bool teardown, JsonObjectWriter reply, ulong now)
        {
            S4Batch b = _batch;
            bool was = b != null && b.Running;
            bool held = _ownOverride;
            if (was) EndBatch(b, "cancelled", teardown ? "de.s4 teardown" : "de.s4 cancel", now); // releases the camera
            if (teardown) Release("de.s4 teardown");
            reply.Bool("ok", true).Str("cmd", S4Rules.Cmd).Str("action", teardown ? "teardown" : "cancel").Bool("cancelled", was)
                 .Str("batch", b != null ? b.Id : null).Num("done", b != null ? (long)b.Done : 0L).Bool("released", held && !_ownOverride)
                 .Bool("overrideActive", _s1 != null && _s1.Cam.OverrideActive);
        }

        // ------------------------------------------------------------------------------------------ lifecycle

 /// <summary>The bridge stops or the world goes: the batch fails and the camera is released.</summary>
        public void Dispose()
        {
            try
            {
                S4Batch b = _batch;
                if (b != null && b.Running) EndBatch(b, "failed", "the S4 service stopped (bridge teardown)", Win32.TickCount64());
            }
            catch (Exception) { }
            try { Release("dispose"); } catch (Exception) { }
            _started = false;
        }

        private static string Utc() { return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture); }

        /// <summary>One structured JSONL row (JSONL only, not the BepInEx log).</summary>
        private static void Row(string json)
        {
            var sb = new System.Text.StringBuilder(json.Length + 1);
            sb.Append(json).Append('\n');
            DeLog.AppendJsonlBatch(sb);
        }

        /// <summary>Runs one feature step; its first exception disables that feature only (logged once); the camera is released.</summary>
        private void Guard(string feature, Action a)
        {
            if (_disabled.Contains(feature)) return;
            try
            {
                a();
            }
            catch (Exception e)
            {
                _disabled.Add(feature);
                DeLog.Error("DE.S4 FEATURE " + feature + " DISABLED after " + e.GetType().Name + ": " + e.Message + " (the rest of the bridge keeps running)\n" + e);
                if (feature == "probe") { try { Release("feature disabled"); } catch (Exception) { } }
            }
        }
    }
}
