using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using mset;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S7 interiors, the Subnautica side (CONTRACT §5.4 "Interior chunks, sky, inside-walkable and clip proxies"; ROADMAP S7
 /// "SN plugin: Interiors"
 /// <list type="bullet">
 /// <item><b>Inside-walkable:</b> <see cref="InsideNow"/> = the newest C1 (≤ 100 ms old, the bridge driving the camera)
 /// says <c>InRoom</c> (<see cref="InsideRule"/>); <see cref="InteriorPatches"/>' postfix then makes
 /// <c>Player.IsInsideWalkable</c> true for the ghost <c>Player.main</c>: the fog gets <c>cameraInside</c>
 /// <c>CanBeAttacked</c> is false, <c>FMODGameParams</c> switches to the
 /// interior events, the other readers of S7.N2 follow. A/B switch <c>de.interior {patch: on|off}</c>; S7.M1 = off.</item>
 /// <item><b>Sky</b> (<c>format interior.sky</c>): <c>Inside</c> chunks
 /// <c>SkyApplier.SetCustomSky(MarmoSkies.main.GetSky(Skies.BaseInterior))</c> (+ <c>RefreshDirtySky</c>, so a re-applied
 /// equal sky reaches a rebuilt renderer); <c>Boundary</c> chunks the same while C1's <c>roomGridId</c> is the chunk's
 /// entity or its root, else <c>SetSky(Skies.Auto)</c> with <c>dynamic</c> and the component enabled (the biome sky, the
 /// S5 default); <c>Exterior</c> chunks keep S5's biome sky. Never <c>SetSky(Skies.BaseInterior)</c> alone: without a
 /// <c>SubRoot</c>/<c>MarmoLifepodSky</c> parent it finds no environment and falls back to the biome
 /// (<c>SkyApplier.GetSkyForEnvironment</c>; that is S7.M5). Re-applied after each rebuild of a cell (the consumer's
 /// <see cref="InteriorHooks.CellApplied"/>), verified every second (<c>InspectorApplySky</c>).</item>
 /// <item><b>Clip proxies:</b> per room box of an entity that straddles the sea surface in Subnautica (min y &lt; 0 &lt;
 /// max y), one <c>WaterClipProxy</c> child of the entity (shape Box, the box scaled from the unit cube, not immovable,
 /// <c>clipMaterial</c> and layer copied from the first <c>WaterClipProxy</c> found loaded in the game); none
 /// submerged or dry; no material: none and <c>DE.S7 interior clip OFF no material</c> once.</item>
 /// <item>Rows <c>s7.interior</c> (every change, 1 Hz while a room set or the inside flag exists), <c>s7.sky</c> (each
 /// sky application), <c>s7.inside</c> (the end of each <c>de.interior {action: trace}</c> window).</item>
 /// </list>
 /// Main thread. Each step has its own guard (its first exception logs once and stops that step only).
 /// </summary>
    internal sealed class S7Interior
    {
        public const ulong RowMs = 1000, VerifyMs = 1000, ClipMs = 500, ClipSearchMs = 15000;

        /// <summary>Read by the IsInsideWalkable postfix (main thread): the ghost is inside an airtight room of a mirrored grid.</summary>
        public static bool InsideNow;
        /// <summary>The A/B switch of S7.N1 (<c>de.interior {patch: on|off}</c>); S7.M1 forces the patch off.</summary>
        public static bool PatchOn = true;
        /// <summary>The N2 caller trace (the postfix counts callers only while it runs).</summary>
        public static readonly InsideTrace Trace = new InsideTrace();
        /// <summary>The frame's clock for the postfix (the trace window).</summary>
        public static ulong NowMs;
        public static S7Interior Current { get; private set; }

        private readonly S1Host _s1;
        private readonly MirrorConsumer _m;
        private readonly HashSet<string> _disabled = new HashSet<string>(StringComparer.Ordinal);
        private Sky _interiorSky;
        private bool _skyMissingLogged;
        // per chunk: the sky last applied (1 interior, 2 biome)
        private readonly Dictionary<ChunkView, byte> _applied = new Dictionary<ChunkView, byte>();
        // per chunk: the frame of the last interior application (a material assigned later gets the sky again)
        private readonly Dictionary<ChunkView, int> _appliedFrame = new Dictionary<ChunkView, int>();
        private readonly List<ChunkView> _scratch = new List<ChunkView>();
        private readonly Dictionary<ulong, List<GameObject>> _clips = new Dictionary<ulong, List<GameObject>>();
        /// <summary>
 /// Review F15: the room boxes each entity's proxies were built from. <c>InteriorStore.Serial</c> is
 /// global (any RoomSet, e.g. a door on another ship, bumped it and rebuilt every surfaced hull's proxies, the sea
 /// surface flashing inside them until <c>WaterClipProxy.LoadAsync</c> ran again); the store replaces an entity's array
 /// only when its own set changes, so the reference is the per-entity change test.
 /// </summary>
        private readonly Dictionary<ulong, CellBox[]> _clipBoxes = new Dictionary<ulong, CellBox[]>();
        private Material _clipMaterial;
        private int _clipLayer;
        private bool _clipOffLogged;
        private ulong _nextClipSearchMs, _nextClipMs, _nextVerifyMs, _nextRowMs;
        private bool _lastInside, _lastC1InRoom, _rowHas, _traceWasActive;
        private ulong _lastRoomGrid;
        private int _traceInsideFrames, _traceOutsideFrames;

        public long SkyInterior, SkyBiome, SkyFixes, SkyRows, ClipsCreated, ClipsDestroyed, Rows, InsideFrames;

        public S7Interior(S1Host s1, MirrorConsumer m)
        {
            _s1 = s1;
            _m = m;
            Current = this;
            InteriorHooks.CellApplied = OnCellApplied;
        }

        public void Dispose()
        {
            InsideNow = false;
            InteriorHooks.CellApplied = null;
            foreach (List<GameObject> l in _clips.Values) foreach (GameObject g in l) DestroySafe(g);
            _clips.Clear();
            _applied.Clear();
            _appliedFrame.Clear();
            if (ReferenceEquals(Current, this)) Current = null;
        }

        public void OnNewSePeer()
        {
            InsideNow = false;
            _applied.Clear(); // the mirror drops every view: chunks come back new
            _appliedFrame.Clear();
            foreach (List<GameObject> l in _clips.Values) foreach (GameObject g in l) DestroySafe(g);
            _clips.Clear();
            _clipBoxes.Clear();
        }

        // ============================================================================================ per frame

        public void Tick(ulong now)
        {
            NowMs = now;
            Guard("inside", () => UpdateInside(now));
            if (now >= _nextVerifyMs || C1Changed()) Guard("sky", () => { _nextVerifyMs = now + VerifyMs; ApplySkies(); });
            if (now >= _nextClipMs) Guard("clip", () => { _nextClipMs = now + ClipMs; UpdateClips(now); });
            Guard("rows", () => Rows_(now));
        }

        private bool C1Driving
        {
            get { return _s1 != null && _s1.InWorld && _s1.Cam != null && _s1.Cam.Installed && _s1.Cam.Gate.Open; }
        }

        private void UpdateInside(ulong now)
        {
            FrameReader r = _s1 != null && _s1.Cam != null ? _s1.Cam.Reader : null;
            bool inside = r != null && InsideRule.Inside(PatchOn, S7Mutants.M1, C1Driving, r.InteriorSeen, r.InteriorInRoom, r.InteriorMs, now);
            InsideNow = inside;
            if (inside) InsideFrames++;
            bool active = Trace.Active(now);
            if (active)
            {
                if (inside) _traceInsideFrames++; else _traceOutsideFrames++;
            }
            if (_traceWasActive && !active) Guard("trace", () => TraceRow("end"));
            _traceWasActive = active;
        }

        private bool C1Changed()
        {
            FrameReader r = _s1 != null && _s1.Cam != null ? _s1.Cam.Reader : null;
            bool inRoom = r != null && r.InteriorInRoom;
            ulong grid = r != null && r.InteriorValid ? r.Interior.RoomGridId : 0;
            return inRoom != _lastC1InRoom || grid != _lastRoomGrid;
        }

        // ============================================================================================ sky

 /// <summary>The consumer built and uploaded a cell (MirrorConsumer.ApplyCell): its chunks get their sky again.</summary>
        private void OnCellApplied(MirrorGridView v, CellKey cell)
        {
            if (cell.C == S7Conventions.InteriorClass.Exterior || Disabled("sky")) return;
            Guard("sky", () =>
            {
                FrameReader r = _s1 != null && _s1.Cam != null ? _s1.Cam.Reader : null;
                bool inRoom = r != null && r.InteriorInRoom;
                ulong grid = r != null && r.InteriorValid ? r.Interior.RoomGridId : 0;
                foreach (ChunkView c in v.Chunks.Values)
                {
                    if (!c.Key.Cell.Equals(cell)) continue;
                    _applied.Remove(c);
                    _appliedFrame.Remove(c);
                    ApplyChunk(v, c, inRoom, grid, "rebuild");
                }
            });
        }

        private void ApplySkies()
        {
            FrameReader r = _s1 != null && _s1.Cam != null ? _s1.Cam.Reader : null;
            bool inRoom = r != null && r.InteriorInRoom;
            ulong grid = r != null && r.InteriorValid ? r.Interior.RoomGridId : 0;
            _lastC1InRoom = inRoom;
            _lastRoomGrid = grid;
            // forget chunks that are gone (destroyed views or rebuilt keys)
            _scratch.Clear();
            foreach (ChunkView c in _applied.Keys) if (c.Go == null) _scratch.Add(c);
            foreach (ChunkView c in _scratch) { _applied.Remove(c); _appliedFrame.Remove(c); }
            foreach (MirrorGridView v in _m.ViewMap.Values)
            {
                if (!v.Alive) continue;
                foreach (ChunkView c in v.Chunks.Values)
                {
                    if (c.Key.Cell.C == S7Conventions.InteriorClass.Exterior) continue;
                    ApplyChunk(v, c, inRoom, grid, "verify");
                }
            }
        }

        private void ApplyChunk(MirrorGridView v, ChunkView c, bool inRoom, ulong roomGrid, string why)
        {
            if (c.Sky == null || c.Go == null) return;
            byte cls = c.Key.Cell.C;
            ulong root = v.IsSubgrid ? v.ParentId : 0;
            bool want = InteriorRules.InteriorSky(cls, inRoom, roomGrid, v.Id, root);
            bool camInThisRoom = InteriorRules.InteriorSky(S7Conventions.InteriorClass.Boundary, inRoom, roomGrid, v.Id, root);
            Sky interior = S12Sn.InteriorSky(InteriorSkyNow()); // S12 hook: the interior look's sky copy while it applies (format look.preset; the original otherwise)
            byte state;
            bool known = _applied.TryGetValue(c, out state);
            if (want)
            {
                bool ok = interior != null && c.Sky.InspectorApplySky == interior;
                if (known && state == 1 && (ok || S7Mutants.M5))
                {
                    // the S5 material step assigned the material after the sky: refresh the property block once more
                    int at;
                    if (ok && _appliedFrame.TryGetValue(c, out at) && c.MaterialFrame >= at)
                    {
                        c.Sky.RefreshDirtySky();
                        _appliedFrame[c] = Time.frameCount;
                    }
                    return;
                }
                if (interior == null) return; // MarmoSkies not ready: next pass
                if (S7Mutants.M5) c.Sky.SetSky(Skies.BaseInterior); // S7.M5: the base-interior anchor without a custom sky (finds no environment: biome)
                else
                {
                    c.Sky.SetCustomSky(interior);
                    c.Sky.RefreshDirtySky();
                }
                if (known && state == 1) SkyFixes++;
                _applied[c] = 1;
                _appliedFrame[c] = Time.frameCount;
                SkyInterior++;
                SkyRow(v, c, cls, why, camInThisRoom);
            }
            else
            {
                if (known && state == 2) return;
                if (!known && cls == S7Conventions.InteriorClass.Exterior) return;
                if (known || cls == S7Conventions.InteriorClass.Boundary)
                {
                    c.Sky.SetSky(Skies.Auto);
                    c.Sky.dynamic = true;
                    if (!c.Sky.enabled) c.Sky.enabled = true; // SkyApplierUpdater follows a moving hull again
                    _applied[c] = 2;
                    SkyBiome++;
                    if (known) SkyRow(v, c, cls, why, camInThisRoom);
                }
            }
        }

        private Sky InteriorSkyNow()
        {
            if (_interiorSky != null) return _interiorSky;
            MarmoSkies ms = MarmoSkies.main;
            if (ms != null) _interiorSky = ms.GetSky(Skies.BaseInterior);
            if (_interiorSky == null && !_skyMissingLogged && ms != null)
            {
                _skyMissingLogged = true;
                DeLog.Warn("DE.S7 interior sky SOURCE MISSING MarmoSkies.main.GetSky(BaseInterior) returned null: interior chunks keep the biome sky");
            }
            return _interiorSky;
        }

        private void SkyRow(MirrorGridView v, ChunkView c, byte cls, string why, bool cameraInRoom)
        {
            if (!S10Sn.TakeRow(S10Sn.RowSky)) return; // S10 hook (round 2 D): the row under a per-second budget (the collector)
            Sky interior = _interiorSky;
            bool isInterior = interior != null && c.Sky.InspectorApplySky == interior;
            JsonOut j = S5Rows.Begin("s7.sky");
            j.Num("snFrame", (long)Time.frameCount).Str("entity", DeLog.Hex(v.Id)).Str("chunk", c.Key.ToString()).Str("class", S7Conventions.InteriorClass.Name(cls))
             .Str("applied", isInterior ? "interior" : "biome").Bool("ok", c.Sky.InspectorApplySky != null).Str("why", why)
             .Str("anchor", c.Sky.anchorSky.ToString()).Bool("dynamic", c.Sky.dynamic).Bool("m5", S7Mutants.M5).Bool("cameraInRoom", cameraInRoom);
            S5Rows.End(j);
            SkyRows++;
        }

        // ============================================================================================ clip proxies

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void UpdateClips(ulong now)
        {
            MirrorState st = _m.State;
            // entities whose rooms are gone, or whose view is gone
            List<ulong> drop = null;
            foreach (KeyValuePair<ulong, List<GameObject>> kv in _clips)
            {
                MirrorGridView v;
                if (st.Rooms.BoxesOf(kv.Key) == null || !_m.ViewMap.TryGetValue(kv.Key, out v) || !v.Alive) (drop = drop ?? new List<ulong>()).Add(kv.Key);
            }
            if (drop != null) foreach (ulong id in drop) DropClips(id);
            if (st.Rooms.Count == 0) return;
            if (_clipMaterial == null && now >= _nextClipSearchMs) FindClipMaterial(now);
            foreach (KeyValuePair<ulong, CellBox[]> kv in st.Rooms.Sets)
            {
                MirrorGridView v;
                GridRecord g;
                if (!_m.ViewMap.TryGetValue(kv.Key, out v) || !v.Alive || !v.HasPose || v.EntityT == null) continue;
                if (!st.Grids.TryGetValue(kv.Key, out g)) continue;
                UpdateClipsOf(v, g, kv.Value);
            }
        }

        private void UpdateClipsOf(MirrorGridView v, GridRecord g, CellBox[] boxes)
        {
            double gs = InteriorChunks.GridSize(g);
            List<GameObject> list;
            if (!_clips.TryGetValue(v.Id, out list)) { list = new List<GameObject>(); _clips[v.Id] = list; }
            CellBox[] built;
            if (_clipBoxes.TryGetValue(v.Id, out built) && !ReferenceEquals(built, boxes) && !SameBoxes(built, boxes))
            {
                // this entity's room set changed: rebuild its proxies
                foreach (GameObject o in list) if (o != null) { DestroySafe(o); ClipsDestroyed++; }
                list.Clear();
            }
            _clipBoxes[v.Id] = boxes;
            while (list.Count < boxes.Length) list.Add(null);
            for (int i = 0; i < boxes.Length; i++)
            {
                CellBox b = boxes[i];
                // the box in SE-local metres (cells c·g ± g/2), mirrored into Unity-local (z negated)
                var lo = new Vector3((float)((b.MinX - 0.5) * gs), (float)((b.MinY - 0.5) * gs), (float)(-(b.MaxZ + 0.5) * gs));
                var hi = new Vector3((float)((b.MaxX + 0.5) * gs), (float)((b.MaxY + 0.5) * gs), (float)(-(b.MinZ - 0.5) * gs));
                float minY = float.MaxValue, maxY = float.MinValue;
                for (int k = 0; k < 8; k++)
                {
                    var p = new Vector3((k & 1) != 0 ? hi.x : lo.x, (k & 2) != 0 ? hi.y : lo.y, (k & 4) != 0 ? hi.z : lo.z);
                    float y = v.EntityT.TransformPoint(p).y;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
                bool want = InteriorRules.StraddlesSurface(minY, maxY) && _clipMaterial != null;
                GameObject go = list[i];
                if (want && go == null)
                {
                    go = new GameObject("DE.S7.Clip." + DeLog.Hex(v.Id) + "." + i.ToString(CultureInfo.InvariantCulture));
                    go.layer = _clipLayer;
                    go.transform.SetParent(v.EntityT, false);
                    go.transform.localPosition = (lo + hi) * 0.5f;
                    go.transform.localRotation = Quaternion.identity;
                    go.transform.localScale = hi - lo;
                    WaterClipProxy wcp = go.AddComponent<WaterClipProxy>();
                    wcp.shape = WaterClipProxy.Shape.Box;
                    wcp.immovable = false;
                    wcp.clipMaterial = _clipMaterial;
                    list[i] = go;
                    ClipsCreated++;
                    DeLog.Info("DE.S7 interior clip proxy " + go.name + " (room box " + b + " straddles the surface: y " + minY.ToString("0.##", CultureInfo.InvariantCulture) + ".." +
                               maxY.ToString("0.##", CultureInfo.InvariantCulture) + ")");
                }
                else if (!want && go != null)
                {
                    DestroySafe(go);
                    list[i] = null;
                    ClipsDestroyed++;
                }
            }
        }

        /// <summary>Review F15: an unchanged set resent (a new GridAdd, a stream restart) arrives as a new array: no rebuild.</summary>
        private static bool SameBoxes(CellBox[] a, CellBox[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i].MinX != b[i].MinX || a[i].MinY != b[i].MinY || a[i].MinZ != b[i].MinZ ||
                    a[i].MaxX != b[i].MaxX || a[i].MaxY != b[i].MaxY || a[i].MaxZ != b[i].MaxZ) return false;
            return true;
        }

        private void FindClipMaterial(ulong now)
        {
            _nextClipSearchMs = now + ClipSearchMs;
            WaterClipProxy[] all = Resources.FindObjectsOfTypeAll<WaterClipProxy>();
            foreach (WaterClipProxy p in all)
            {
                if (p == null || p.clipMaterial == null) continue;
                _clipMaterial = p.clipMaterial;
                _clipLayer = p.gameObject.layer;
                DeLog.Info("DE.S7 interior clip material from " + p.gameObject.name + " (shape " + p.shape + ", layer " + _clipLayer + ", material " + _clipMaterial.name + ")");
                return;
            }
            if (!_clipOffLogged)
            {
                _clipOffLogged = true;
                DeLog.Warn("DE.S7 interior clip OFF no material (no WaterClipProxy loaded in the game yet; searched again every " + (ClipSearchMs / 1000) + " s)");
            }
        }

        private void DropClips(ulong id)
        {
            List<GameObject> l;
            if (_clips.TryGetValue(id, out l)) foreach (GameObject g in l) if (g != null) { DestroySafe(g); ClipsDestroyed++; }
            _clips.Remove(id);
            _clipBoxes.Remove(id);
        }

        /// <summary>
 /// Review F15: a clip proxy with what <c>WaterClipProxy</c> made for it: the <c>new Mesh</c> of its
 /// <c>CreateBoxMesh</c> (the MeshFilter's <c>sharedMesh</c>) and the material instance its <c>LoadAsync</c> made
 /// (<c>meshRenderer.material = clipMaterial</c>; the renderer's <c>sharedMaterial</c>, never the source material
 /// we copied); its own <c>OnDestroy</c> releases only the distance field
 ///.
 /// </summary>
        private void DestroySafe(GameObject g)
        {
            if (g == null) return;
            try
            {
                MeshFilter mf = g.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) UnityEngine.Object.Destroy(mf.sharedMesh);
                MeshRenderer mr = g.GetComponent<MeshRenderer>();
                if (mr != null && mr.sharedMaterial != null && !ReferenceEquals(mr.sharedMaterial, _clipMaterial)) UnityEngine.Object.Destroy(mr.sharedMaterial);
            }
            catch (Exception) { }
            try { UnityEngine.Object.Destroy(g); } catch (Exception) { }
        }

        public int ClipCount
        {
            get { int n = 0; foreach (List<GameObject> l in _clips.Values) foreach (GameObject g in l) if (g != null) n++; return n; }
        }

        // ============================================================================================ rows

        private void Rows_(ulong now)
        {
            FrameReader r = _s1 != null && _s1.Cam != null ? _s1.Cam.Reader : null;
            ulong grid = r != null && r.InteriorValid ? r.Interior.RoomGridId : 0;
            bool changed = !_rowHas || InsideNow != _lastInside || grid != _lastRowGrid;
            bool periodic = (InsideNow || _m.State.Rooms.Count > 0) && now >= _nextRowMs;
            if (!changed && !periodic) return;
            _rowHas = true;
            _lastInside = InsideNow;
            _lastRowGrid = grid;
            _nextRowMs = now + RowMs;
            InteriorRow(changed ? "change" : "periodic");
        }

        private ulong _lastRowGrid;

        public void InteriorRow(string why)
        {
            FrameReader r = _s1 != null && _s1.Cam != null ? _s1.Cam.Reader : null;
            int ex = 0, bd = 0, ins = 0, skyIn = 0, skyBiome = 0;
            Sky interior = _interiorSky;
            foreach (MirrorGridView v in _m.ViewMap.Values)
            {
                if (!v.Alive) continue;
                foreach (ChunkView c in v.Chunks.Values)
                {
                    byte cls = c.Key.Cell.C;
                    if (cls == S7Conventions.InteriorClass.Inside) ins++;
                    else if (cls == S7Conventions.InteriorClass.Boundary) bd++;
                    else ex++;
                    if (cls == S7Conventions.InteriorClass.Exterior || c.Sky == null) continue;
                    if (interior != null && c.Sky.InspectorApplySky == interior) skyIn++; else skyBiome++;
                }
            }
            JsonOut j = S5Rows.Begin("s7.interior");
            j.Str("why", why).Num("snFrame", (long)Time.frameCount).Num("c1Tick", r != null ? r.InteriorTick : 0UL).Bool("inside", InsideNow)
             .Bool("c1InRoom", r != null && r.InteriorInRoom).Bool("c1Extension", r != null && r.InteriorValid).Num("c1Flags", r != null ? (long)r.Interior.Flags : 0L)
             .Bool("patched", PatchOn && !S7Mutants.M1 && InteriorPatches.InsideWalkablePatched).Bool("patchOn", PatchOn).Bool("m1", S7Mutants.M1)
             .Str("roomGridId", r != null && r.InteriorValid ? DeLog.Hex(r.Interior.RoomGridId) : "0x0").Bool("driving", C1Driving)
             .Obj("chunks").Num("inside", (long)ins).Num("boundary", (long)bd).Num("exterior", (long)ex).EndObj()
             .Obj("skies").Num("interior", (long)skyIn).Num("biome", (long)skyBiome).EndObj()
             .Num("clipProxies", (long)ClipCount).Num("roomSets", (long)_m.State.Rooms.Count).Num("overrides", InteriorPatches.Overrides)
             .Num("unstuckSkipped", InteriorPatches.UnstuckSkipped);
            S5Rows.End(j);
            Rows++;
        }

        /// <summary>The s7.inside row of a finished (or asked) trace window.</summary>
        public void TraceRow(string why)
        {
            bool insideWindow = _traceInsideFrames >= _traceOutsideFrames && _traceInsideFrames > 0;
            Player p = Player.main;
            FrameReader fr0 = _s1 != null && _s1.Cam != null ? _s1.Cam.Reader : null; // the row's SE tick (the analyzer picks rows by the launcher's windows)
            JsonOut j = S5Rows.Begin("s7.inside");
            j.Str("why", why).Str("window", insideWindow ? "inside" : "outside").Num("insideFrames", (long)_traceInsideFrames).Num("outsideFrames", (long)_traceOutsideFrames)
             .Num("calls", Trace.Calls).Num("unknown", Trace.Unknown);
            Dictionary<string, long> direct = insideWindow ? Trace.InsideDirect : Trace.OutsideDirect;
            Dictionary<string, long> via = insideWindow ? Trace.InsideVia : Trace.OutsideVia;
            Counts(j, "readers", direct);
            Counts(j, "via", via);
            Counts(j, "readersInside", Trace.InsideDirect);
            Counts(j, "readersOutside", Trace.OutsideDirect);
            Counts(j, "viaInside", Trace.InsideVia);
            Counts(j, "viaOutside", Trace.OutsideVia);
            j.Str("notCalled", string.Join(",", Trace.NotCalled().ToArray()));
            if (p != null)
            {
                // the ghost unchanged by the cycle (S7.N2.ghost): O2 as a fraction beside C1's mirrored fraction (S5a), health,
 // and the distance to the camera rule's target (GhostPlayer.Target: no unstuck teleport)
                Vector3 pos = p.transform.position;
                float cap = p.GetOxygenCapacity();
                FrameReader fr = _s1 != null && _s1.Cam != null ? _s1.Cam.Reader : null;
                j.Obj("ghost").Num("o2", cap > 0f ? (double)(p.GetOxygenAvailable() / cap) : double.NaN).Num("o2Units", (double)p.GetOxygenAvailable())
                 .Num("c1O2", fr != null && fr.SwimValid ? fr.Swim.O2 / 255.0 : double.NaN)
                 .Num("health", p.liveMixin != null ? (double)p.liveMixin.health : double.NaN).Vec("pos", pos.x, pos.y, pos.z)
                 .Num("posErrM", _s1 != null && _s1.Ghost != null ? (double)Vector3.Distance(pos, _s1.Ghost.Target) : double.NaN).EndObj();
                j.Bool("canBeAttacked", p.CanBeAttacked());
                // review F21: Player.CanBeAttacked is !GameModeUtils.IsInvisible outside
 // the analyzer skips the outside half when true
                j.Bool("noAggression", GameModeUtils.IsInvisible());
            }
            if (fr0 != null) j.Num("c1Tick", fr0.InteriorTick);
            long music, bright, cc;
            direct.TryGetValue("FMODGameParams", out music);
            direct.TryGetValue("uGUI_AdaptiveBrightness", out bright);
            direct.TryGetValue("ColorCorrection", out cc);
            j.Num("music", music).Num("brightness", bright + cc).Num("unstuckSkipped", InteriorPatches.UnstuckSkipped).Bool("m1", S7Mutants.M1).Bool("patchOn", PatchOn);
            S5Rows.End(j);
            Rows++;
        }

        private static void Counts(JsonOut j, string name, Dictionary<string, long> d)
        {
            j.Obj(name);
            foreach (KeyValuePair<string, long> kv in d) j.Num(kv.Key, kv.Value);
            j.EndObj();
        }

        // ============================================================================================ C11

 /// <summary><c>de.interior {action: status | trace, seconds} | {patch: on | off}</c>.</summary>
        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req.Cmd != "de.interior") return false;
            Dictionary<string, object> a = S3Args.ToDict(req.Args);
            reply.Str("cmd", req.Cmd);
            object v;
            if (a.TryGetValue("patch", out v))
            {
                bool on;
                string s = v as string;
                if (s != null) on = s.Trim().ToLowerInvariant() == "on";
                else if (!S3Args.TryBool(v, out on)) { reply.Bool("ok", false).Str("error", "patch: on|off"); return true; }
                PatchOn = on;
                DeLog.Warn("DE.S7 interior patch " + (on ? "ON" : "OFF") + " (de.interior A/B)");
                Status(reply.Bool("ok", true));
                return true;
            }
            string action = S3Args.Str(a, "action", "status").Trim().ToLowerInvariant();
            switch (action)
            {
                case "status":
                    InteriorRow("status");
                    Status(reply.Bool("ok", true));
                    return true;
                case "trace":
                {
                    double seconds = S3Args.Num(a, "seconds", 10.0);
                    _traceInsideFrames = _traceOutsideFrames = 0;
                    Trace.Start(now, seconds);
                    _traceWasActive = true;
                    DeLog.Info("DE.S7 interior trace " + seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s (IsInsideWalkable callers counted; inside=" + InsideNow + ")");
                    Status(reply.Bool("ok", true));
                    reply.Num("traceSeconds", seconds);
                    return true;
                }
                default:
                    reply.Bool("ok", false).Str("error", "de.interior: unknown action '" + action + "' (status | trace; or {patch: on|off})");
                    return true;
            }
        }

        public void Status(JsonObjectWriter reply)
        {
            FrameReader r = _s1 != null && _s1.Cam != null ? _s1.Cam.Reader : null;
            reply.Bool("inside", InsideNow).Bool("patchOn", PatchOn).Bool("m1", S7Mutants.M1).Bool("m5", S7Mutants.M5)
                 .Bool("postfix", InteriorPatches.InsideWalkablePatched).Bool("unstuckPrefix", InteriorPatches.UnstuckPatched)
                 .Bool("c1InRoom", r != null && r.InteriorInRoom).Bool("c1Extension", r != null && r.InteriorValid)
                 .Str("roomGridId", r != null && r.InteriorValid ? DeLog.Hex(r.Interior.RoomGridId) : "0x0")
                 .Num("roomSets", (long)_m.State.Rooms.Count).Num("roomSetsApplied", _m.State.Rooms.Applied).Num("roomSetsIgnored", _m.State.Rooms.VersionIgnored)
                 .Num("roomSetsMalformed", _m.State.Rooms.Malformed).Num("partsMoved", _m.State.Rooms.PartsMoved)
                 .Num("skyInterior", SkyInterior).Num("skyBiome", SkyBiome).Num("skyFixes", SkyFixes).Bool("interiorSkyFound", _interiorSky != null)
                 .Num("clipProxies", (long)ClipCount).Bool("clipMaterial", _clipMaterial != null).Num("overrides", InteriorPatches.Overrides)
                 .Num("unstuckSkipped", InteriorPatches.UnstuckSkipped).Bool("tracing", Trace.Active(NowMs)).Num("traceCalls", Trace.Calls)
                 .Str("disabled", string.Join(",", new List<string>(_disabled).ToArray()))
                 .Str("line", "DE.S7 interior inside=" + InsideNow + " patch=" + (PatchOn ? "on" : "off") + " roomSets=" + _m.State.Rooms.Count + " clip=" + ClipCount);
        }

        // ============================================================================================ guards

        private bool Disabled(string step) { return _disabled.Contains(step); }

        private void Guard(string step, Action a)
        {
            if (_disabled.Contains(step)) return;
            try { a(); }
            catch (Exception e)
            {
                _disabled.Add(step);
                DeLog.Error("DE.S7 interior " + step + " DISABLED after an exception (the rest of S7 keeps running): " + e);
                if (step == "inside") InsideNow = false;
            }
        }
    }

    /// <summary>Hooks the mirror calls without knowing S7 (null when S7 interiors are not running).</summary>
    internal static class InteriorHooks
    {
        /// <summary>MirrorConsumer.ApplyCell: a cell's chunks were rebuilt and uploaded.</summary>
        public static Action<MirrorGridView, CellKey> CellApplied;
    }
}
