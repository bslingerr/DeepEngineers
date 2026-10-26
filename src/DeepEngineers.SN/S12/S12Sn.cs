using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using mset;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S12 Interiors v2, the Subnautica side (CONTRACT §5.4 "S12 conventions"; main thread). Each frame (the Bridge's S12 hook):
 /// <list type="bullet">
 /// <item><b>Flood planes</b> (format flood.plane): the C6 block at +0x800 (S12Wire, its own seqlock); one water plane per
 /// box of each flood row under the mirrored grid's Entity (InteriorView's mapping: Unity-local = SE-local, z negated), the
 /// level eased toward the row's; BaseWaterPlane's own material
 /// found in a loaded
 /// <c>BaseWaterPlane</c> (<c>Resources.FindObjectsOfTypeAll</c>), else a transparent fallback; one splash per start
 /// (S12StartReader): S7's <c>FxSound.Splash</c> and bubbles through S7's effect player and its caps (S12EffectsPlayer.cs).</item>
 /// <item><b>Vent bubbles</b> (format vent.event): a burst per start, then <c>Vent.BubblesPerS</c> at the vent's mouth
 /// along its forward, through S7p's per-frame bubble budget; no sound of ours (SE plays its vent's own).</item>
 /// <item><b>The interior look</b> (formats look.when, look.preset) while the camera is in an airtight room under water:
 /// <c>WaterscapeVolume.causticsAmount</c>
 /// <c>WaterSunShaftsOnCamera.enabled</c>
 /// a copy of
 /// <c>MarmoSkies.main.GetSky(Skies.BaseInterior)</c> with <c>DiffIntensity</c>/<c>SpecIntensity</c>
 /// scaled and its <c>SH.c</c> tinted
 /// given to the interior chunks
 /// through InteriorView's sky (the S12 hook <see cref="InteriorSky"/>), and a floor bounce point light.</item>
 /// </list>
 /// Rows <c>s12.site</c>, <c>s12.plane</c>, <c>s12.fx</c>, <c>s12.look</c>; <c>de.s12 {status | look: on|off}</c> (dev).
 /// </summary>
    internal static unsafe class S12Sn
    {
        public const string Verb = "de.s12";
        public static volatile bool LookOn = true;
        /// <summary>de.s12 water fallback (dev): the transparent plane even when a BaseWaterPlane material is loaded.</summary>
        public static volatile bool ForceFallback;

        private sealed class PlaneSet
        {
            public long GridId;
            public uint TrackId;
            public readonly List<GameObject> Planes = new List<GameObject>();
            public readonly List<Mesh> Meshes = new List<Mesh>();
            public readonly S12StartReader Starts = new S12StartReader();
            public double Shown = double.NaN;
            public S12Row Row;
            public float RowTime;
            public bool Seen;
            public float NextRowS;
            public int Visible;
        }

        private sealed class VentFx
        {
            public long GridId;
            public uint Id;
            public readonly S12StartReader Starts = new S12StartReader();
            public readonly S12Stream Stream = new S12Stream();
            public S12Row Row;
            public bool Seen;
            public long Emitted, Asked;
            public float NextRowS;
        }

        private static readonly S12Header _h = new S12Header();
        private static readonly S12Row[] _rows = new S12Row[S12Conventions.C6.MaxRows];
        private static readonly Dictionary<ulong, PlaneSet> _planes = new Dictionary<ulong, PlaneSet>();
        private static readonly Dictionary<ulong, VentFx> _vents = new Dictionary<ulong, VentFx>();
        private static readonly double[] _corners = new double[12];
        private static readonly List<ulong> _gone = new List<ulong>();
        private static GameObject _root;
        private static Material _water;
        private static string _waterSource = "none";
        private static float _nextWaterSearchS;
        private static bool _siteLogged, _inWorldLast, _failed;
        private static uint _epoch;
        private static float _lastFrameS;
        public static long Frames, Reads, Splashes, SplashBubbles, Bursts, Streamed, PlanesBuilt, Errors;
        private static float _nextErrorS;

        // the look
        private static bool _lookApplied, _lookWantLast;
        private static WaterscapeVolume _volume;
        private static float _causticsBefore = float.NaN;
        private static WaterSunShaftsOnCamera _shafts;
        private static bool _shaftsBefore;
        private static Sky _skySource, _skyCopy;
        private static Light _bounce;
        private static float _nextBounceS, _nextLookRowS, _bounceLamp;
        private static GameObject _lightsRoot;
        private static float _nextLightsSearchS;

        /// <summary>The S12 hook of InteriorView (its sky for the interior chunks): the preset's copy while the look applies.</summary>
        public static Sky InteriorSky(Sky original)
        {
            try
            {
                if (!_lookApplied || original == null) return original;
                if (_skyCopy == null || !ReferenceEquals(_skySource, original)) MakeSky(original);
                return _skyCopy != null ? _skyCopy : original;
            }
            catch (Exception) { return original; }
        }

        /// <summary>The Bridge's per-frame hook (main thread). Never throws.</summary>
        public static void Frame(ulong nowMs, bool inWorld, bool seAlive, byte* sectionBase, MirrorConsumer mirror, S7Effects fx, FrameReader c1)
        {
            if (_failed) return;
            try
            {
                float now = Time.unscaledTime;
                float dt = _lastFrameS > 0 ? Mathf.Clamp(now - _lastFrameS, 0f, 0.25f) : 0f;
                _lastFrameS = now;
                if (!inWorld)
                {
                    if (_inWorldLast) Reset("left the world");
                    _inWorldLast = false;
                    return;
                }
                _inWorldLast = true;
                Frames++;
                if (!_siteLogged) Site();
                int n = 0;
                if (seAlive && sectionBase != null)
                {
                    uint seq;
                    if (S12Wire.TryRead(Layout.RegionPtr(sectionBase, RegionId.C6SeTime), _h, _rows, out n, out seq) == SeqlockReadStatus.Fresh) Reads++;
                    else n = -1; // torn or never: keep the last rows this frame
                }
                if (n >= 0)
                {
                    if (_h.Version == S12Conventions.C6.Version && _h.WorldEpoch != _epoch) { _epoch = _h.WorldEpoch; ResetReaders(); }
                    Apply(n, mirror, fx, now);
                }
                UpdatePlanes(mirror, now, dt);
                StreamVents(mirror, fx, dt, now);
                Look(c1, mirror, nowMs, now);
            }
            catch (Exception e)
            {
                Errors++;
                float t = Time.unscaledTime;
                if (t >= _nextErrorS) { _nextErrorS = t + 10f; DeLog.Warn("DE.Error S12 frame " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace); }
                if (Errors > 200 && !_failed) { _failed = true; Reset("too many errors"); DeLog.Warn("DE.S12 OFF after 200 errors (planes and the look removed)"); }
            }
        }

        // ============================================================================================ rows in

        private static void Apply(int n, MirrorConsumer mirror, S7Effects fx, float now)
        {
            foreach (PlaneSet p in _planes.Values) p.Seen = false;
            foreach (VentFx v in _vents.Values) v.Seen = false;
            for (int i = 0; i < n; i++)
            {
                S12Row r = _rows[i];
                ulong key = ((ulong)r.GridId * 31UL) ^ r.TrackId ^ ((ulong)r.Kind << 60);
                if (r.Kind == S12Conventions.RowKind.Flood)
                {
                    PlaneSet p;
                    if (!_planes.TryGetValue(key, out p)) { p = new PlaneSet { GridId = r.GridId, TrackId = r.TrackId }; _planes[key] = p; }
                    p.Seen = true;
                    p.Row = Copy(r);
                    p.RowTime = now;
                    bool fresh = r.State == S12Conventions.FloodState.Rising && r.Level < S12StartReader.FreshLevel;
                    if (p.Starts.Step(_h.WorldEpoch, r.StartSeq, fresh) > 0) Splash(p, mirror, fx);
                }
                else if (r.Kind == S12Conventions.RowKind.Vent)
                {
                    VentFx v;
                    if (!_vents.TryGetValue(key, out v)) { v = new VentFx { GridId = r.GridId, Id = r.TrackId }; _vents[key] = v; }
                    v.Seen = true;
                    v.Row = Copy(r);
                    if (v.Starts.Step(_h.WorldEpoch, r.StartSeq, true) > 0) Burst(v, mirror, fx);
                }
            }
            _gone.Clear();
            foreach (KeyValuePair<ulong, PlaneSet> kv in _planes) if (!kv.Value.Seen) _gone.Add(kv.Key);
            foreach (ulong k in _gone) { DropPlanes(_planes[k], "row gone"); _planes.Remove(k); }
            _gone.Clear();
            foreach (KeyValuePair<ulong, VentFx> kv in _vents) if (!kv.Value.Seen) _gone.Add(kv.Key);
            foreach (ulong k in _gone) { FxRow("ventEnd", _vents[k].GridId, _vents[k].Id, _vents[k].Row.StartSeq, 0, 0, false, _vents[k].Asked, _vents[k].Emitted); _vents.Remove(k); }
        }

        private static S12Row Copy(S12Row r)
        {
            var c = r;
            c.Boxes = r.Boxes != null ? (short[])r.Boxes.Clone() : null;
            return c;
        }

        private static void ResetReaders()
        {
            foreach (PlaneSet p in _planes.Values) p.Starts.Reset();
            foreach (VentFx v in _vents.Values) v.Starts.Reset();
        }

        // ============================================================================================ planes

        private static MirrorGridView View(MirrorConsumer mirror, long gridId)
        {
            if (mirror == null) return null;
            MirrorGridView v;
            if (!mirror.ViewMap.TryGetValue((ulong)gridId, out v) || v == null || !v.Alive || !v.HasPose || v.EntityT == null) return null;
            return v;
        }

        private static void UpdatePlanes(MirrorConsumer mirror, float now, float dt)
        {
            bool off = DevMutants.IsOn(S12SnMutants.PlaneOff);
            foreach (PlaneSet p in _planes.Values)
            {
                S12Row r = p.Row;
                p.Shown = S12Ease.Toward(p.Shown, r.Level, r.RatePerS, now - p.RowTime, dt);
                MirrorGridView v = View(mirror, p.GridId);
                if (v == null || off) { HidePlanes(p); PlaneRow(p, v, now, off); continue; }
                double g = S12PlaneRules.GridSize(r.GridSize);
                var boxes = new List<CellBox>();
                for (int i = 0; i < r.BoxCount; i++) boxes.Add(r.Box(i));
                CellBox union = S12Boxes.Union(boxes);
                double h = S12PlaneRules.Height(union, g, r.UpAxis, p.Shown);
                EnsureWater(now);
                while (p.Planes.Count < boxes.Count) NewPlane(p, v);
                int visible = 0;
                for (int i = 0; i < p.Planes.Count; i++)
                {
                    GameObject go = p.Planes[i];
                    if (go == null) continue;
                    if (go.transform.parent != v.EntityT) go.transform.SetParent(v.EntityT, false);
                    bool show = i < boxes.Count && S12PlaneRules.Corners(boxes[i], g, r.UpAxis, h, _corners);
                    if (show)
                    {
                        var verts = new Vector3[4];
                        for (int k = 0; k < 4; k++) verts[k] = new Vector3((float)_corners[k * 3], (float)_corners[k * 3 + 1], (float)-_corners[k * 3 + 2]);
                        Mesh m = p.Meshes[i];
                        m.vertices = verts;
                        m.RecalculateBounds();
                        visible++;
                    }
                    if (go.activeSelf != show) go.SetActive(show);
                }
                p.Visible = visible;
                PlaneRow(p, v, now, false);
            }
        }

        private static void NewPlane(PlaneSet p, MirrorGridView v)
        {
            Root();
            var go = new GameObject("DE.S12.Water." + DeLog.Hex((ulong)p.GridId) + "." + p.TrackId.ToString(CultureInfo.InvariantCulture) + "." + p.Planes.Count.ToString(CultureInfo.InvariantCulture));
            go.transform.SetParent(v.EntityT, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            var m = new Mesh { name = go.name };
            m.vertices = new Vector3[4];
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            m.triangles = new[] { 0, 2, 1, 1, 2, 3, 0, 1, 2, 1, 3, 2 }; // both faces: seen from above and from under water
            go.AddComponent<MeshFilter>().sharedMesh = m;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _water;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            go.SetActive(false);
            p.Planes.Add(go);
            p.Meshes.Add(m);
            PlanesBuilt++;
        }

        private static void HidePlanes(PlaneSet p)
        {
            foreach (GameObject go in p.Planes) if (go != null && go.activeSelf) go.SetActive(false);
            p.Visible = 0;
        }

        private static void DropPlanes(PlaneSet p, string why)
        {
            for (int i = 0; i < p.Planes.Count; i++)
            {
                try { if (i < p.Meshes.Count && p.Meshes[i] != null) UnityEngine.Object.Destroy(p.Meshes[i]); } catch (Exception) { }
                try { if (p.Planes[i] != null) UnityEngine.Object.Destroy(p.Planes[i]); } catch (Exception) { }
            }
            p.Planes.Clear();
            p.Meshes.Clear();
            JsonOut j = S5Rows.Begin("s12.plane");
            j.Str("grid", p.GridId.ToString(CultureInfo.InvariantCulture)).Num("track", (long)p.TrackId).Str("why", why).Num("planes", 0L).Num("visible", 0L)
             .Num("level", 0.0).Num("shown", 0.0).Str("state", "none");
            S5Rows.End(j);
        }

        /// <summary>s12.plane: once a second per flood (and on a level change of 2 %): the drawn planes, their world heights, the eased level.</summary>
        private static void PlaneRow(PlaneSet p, MirrorGridView v, float now, bool off)
        {
            if (now < p.NextRowS) return;
            p.NextRowS = now + 1f;
            float worldY = float.NaN, expectY = float.NaN;
            if (v != null && p.Visible > 0)
            {
                foreach (GameObject go in p.Planes)
                    if (go != null && go.activeSelf)
                    {
                        Mesh m = go.GetComponent<MeshFilter>().sharedMesh;
                        Vector3 c = go.transform.TransformPoint(m.bounds.center);
                        worldY = c.y;
                        break;
                    }
            }
            if (v != null)
            {
                // the expected plane (the row's own level, not the eased one) at the union's centre, mapped through the Entity
                double g = S12PlaneRules.GridSize(p.Row.GridSize);
                var boxes = new List<CellBox>();
                for (int i = 0; i < p.Row.BoxCount; i++) boxes.Add(p.Row.Box(i));
                CellBox u = S12Boxes.Union(boxes);
                double h = S12PlaneRules.Height(u, g, p.Row.UpAxis, p.Row.Level);
                double cx = (u.MinX + u.MaxX) * 0.5 * g, cy = (u.MinY + u.MaxY) * 0.5 * g, cz = (u.MinZ + u.MaxZ) * 0.5 * g;
                bool neg = p.Row.UpAxis == S12Conventions.UpAxis.NegX || p.Row.UpAxis == S12Conventions.UpAxis.NegY || p.Row.UpAxis == S12Conventions.UpAxis.NegZ;
                double a = neg ? -h : h;
                if (p.Row.UpAxis <= S12Conventions.UpAxis.NegX) cx = a; else if (p.Row.UpAxis >= S12Conventions.UpAxis.PosZ) cz = a; else cy = a;
                expectY = v.EntityT.TransformPoint(new Vector3((float)cx, (float)cy, (float)-cz)).y;
            }
            JsonOut j = S5Rows.Begin("s12.plane");
            j.Str("grid", p.GridId.ToString(CultureInfo.InvariantCulture)).Num("track", (long)p.TrackId).Str("state", S12Conventions.FloodState.Name(p.Row.State))
             .Num("level", (double)p.Row.Level).Num("shown", double.IsNaN(p.Shown) ? -1.0 : p.Shown).Num("rate", (double)p.Row.RatePerS).Num("startSeq", (long)p.Row.StartSeq)
             .Num("planes", (long)p.Planes.Count).Num("visible", (long)p.Visible).Num("worldY", float.IsNaN(worldY) ? -99999.0 : (double)worldY)
             .Num("expectWorldY", float.IsNaN(expectY) ? -99999.0 : (double)expectY).Str("material", _waterSource).Bool("view", v != null).Bool("m7", off)
             .Num("boxes", (long)p.Row.BoxCount).Num("up", (long)p.Row.UpAxis);
            S5Rows.End(j);
        }

        private static void EnsureWater(float now)
        {
            if (_water != null && (ForceFallback ? _waterSource == "fallback" : _waterSource == "base")) return;
            if (now < _nextWaterSearchS && _water != null) return;
            _nextWaterSearchS = now + 10f;
            Material found = ForceFallback ? null : BaseWaterMaterial();
            if (found != null)
            {
                Material m = new Material(found) { name = "DE.S12.FloodWater (" + found.name + ")" };
                if (m.HasProperty("_ClipedValue")) m.SetFloat("_ClipedValue", 0f);
                SwapWater(m, "base");
                DeLog.Info("DE.S12 flood water material from BaseWaterPlane.waterRender: " + found.name + " (shader " + (found.shader != null ? found.shader.name : "?") + ")");
                return;
            }
            if (_water == null || (ForceFallback && _waterSource != "fallback"))
            {
                Shader s = null;
                try { s = Shader.Find("Sprites/Default"); } catch (Exception) { }
                if (s == null) try { s = Shader.Find("Hidden/Internal-Colored"); } catch (Exception) { }
                var m = new Material(s) { name = "DE.S12.FloodWater (fallback)", color = new Color(0.22f, 0.42f, 0.52f, 0.55f) };
                m.renderQueue = 3000;
                SwapWater(m, "fallback");
                DeLog.Info("DE.S12 flood water: no loaded BaseWaterPlane yet, a transparent fallback (" + (s != null ? s.name : "no shader") + "); searched again every 10 s");
            }
        }

        private static void SwapWater(Material m, string source)
        {
            Material old = _water;
            _water = m;
            _waterSource = source;
            foreach (PlaneSet p in _planes.Values)
                foreach (GameObject go in p.Planes)
                    if (go != null) { MeshRenderer mr = go.GetComponent<MeshRenderer>(); if (mr != null) mr.sharedMaterial = m; }
            if (old != null) try { UnityEngine.Object.Destroy(old); } catch (Exception) { }
        }

        private static FieldInfo _waterRenderField;
        private static bool _waterFieldLooked;

        /// <summary>BaseWaterPlane's private <c>waterRender</c> Renderer of any loaded BaseWaterPlane.</summary>
        private static Material BaseWaterMaterial()
        {
            if (!_waterFieldLooked)
            {
                _waterFieldLooked = true;
                _waterRenderField = typeof(BaseWaterPlane).GetField("waterRender", BindingFlags.Instance | BindingFlags.NonPublic);
                if (_waterRenderField == null) DeLog.Warn("DE.S12 MISSING BaseWaterPlane.waterRender: the fallback water plane only");
            }
            if (_waterRenderField == null) return null;
            BaseWaterPlane[] all = Resources.FindObjectsOfTypeAll<BaseWaterPlane>();
            foreach (BaseWaterPlane b in all)
            {
                if (b == null) continue;
                var r = _waterRenderField.GetValue(b) as Renderer;
                if (r != null && r.sharedMaterial != null) return r.sharedMaterial;
            }
            return null;
        }

        // ============================================================================================ splash and bubbles

        private static Vector3 WorldOf(MirrorGridView v, float x, float y, float z)
        {
            return v.EntityT.TransformPoint(new Vector3(x, y, -z));
        }

        private static void Splash(PlaneSet p, MirrorConsumer mirror, S7Effects fx)
        {
            Splashes++;
            MirrorGridView v = View(mirror, p.GridId);
            int emitted = 0;
            bool sound = false;
            if (v != null && fx != null)
            {
                S12Row r = p.Row;
                double g = S12PlaneRules.GridSize(r.GridSize);
                var boxes = new List<CellBox>();
                for (int i = 0; i < r.BoxCount; i++) boxes.Add(r.Box(i));
                CellBox u = S12Boxes.Union(boxes);
                double floor, top;
                S12Boxes.FloorTop(u, g, r.UpAxis, out floor, out top);
                // the splash on the floor under the room's centre (the water comes in there first)
                float px = r.Px, py = r.Py, pz = r.Pz;
                bool neg = r.UpAxis == S12Conventions.UpAxis.NegX || r.UpAxis == S12Conventions.UpAxis.NegY || r.UpAxis == S12Conventions.UpAxis.NegZ;
                float a = (float)(neg ? -(floor + 0.2) : floor + 0.2);
                if (r.UpAxis <= S12Conventions.UpAxis.NegX) px = a; else if (r.UpAxis >= S12Conventions.UpAxis.PosZ) pz = a; else py = a;
                Vector3 at = WorldOf(v, px, py, pz);
                float width = (float)((Math.Max(u.MaxX - u.MinX, u.MaxZ - u.MinZ) + 1) * g);
                sound = fx.S12SplashSound(at, 1f);
                emitted = fx.S12Bubbles(at, width * 0.35f, S12Conventions.Vent.SplashBubbles, 0.8f);
                SplashBubbles += emitted;
            }
            FxRow("splash", p.GridId, p.TrackId, p.Row.StartSeq, emitted, S12Conventions.Vent.SplashBubbles, sound, 0, 0);
        }

        private static void Burst(VentFx vf, MirrorConsumer mirror, S7Effects fx)
        {
            Bursts++;
            MirrorGridView v = View(mirror, vf.GridId);
            int emitted = 0;
            if (v != null && fx != null)
            {
                Vector3 at, dir;
                VentMouth(v, vf.Row, out at, out dir);
                emitted = fx.S12Bubbles(at + dir * 0.3f, 0.3f, S12Conventions.Vent.BurstBubbles, 0.6f);
            }
            vf.Asked += S12Conventions.Vent.BurstBubbles;
            vf.Emitted += emitted;
            FxRow("burst", vf.GridId, vf.Id, vf.Row.StartSeq, emitted, S12Conventions.Vent.BurstBubbles, false, vf.Asked, vf.Emitted);
        }

        private static void VentMouth(MirrorGridView v, S12Row r, out Vector3 at, out Vector3 dir)
        {
            at = WorldOf(v, r.Px, r.Py, r.Pz);
            Vector3 d = v.EntityT.TransformDirection(new Vector3(r.Dx, r.Dy, -r.Dz));
            dir = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.up;
            float half = (float)(S12PlaneRules.GridSize(r.GridSize) * 0.5);
            at += dir * half;
        }

        private static void StreamVents(MirrorConsumer mirror, S7Effects fx, float dt, float now)
        {
            foreach (VentFx vf in _vents.Values)
            {
                int k = vf.Stream.Take(S12Conventions.Vent.BubblesPerS, dt);
                if (k <= 0) continue;
                MirrorGridView v = View(mirror, vf.GridId);
                int emitted = 0;
                if (v != null && fx != null)
                {
                    Vector3 at, dir;
                    VentMouth(v, vf.Row, out at, out dir);
                    emitted = fx.S12Bubbles(at + dir * 0.2f, 0.15f, k, vf.Row.State == S12Conventions.VentState.Depressurizing ? 0.2f : 0.7f);
                }
                vf.Asked += k;
                vf.Emitted += emitted;
                Streamed += emitted;
                if (now >= vf.NextRowS)
                {
                    vf.NextRowS = now + 1f;
                    FxRow("stream", vf.GridId, vf.Id, vf.Row.StartSeq, emitted, k, false, vf.Asked, vf.Emitted);
                }
            }
        }

        private static void FxRow(string kind, long grid, uint id, uint seq, int emitted, int asked, bool sound, long totalAsked, long totalEmitted)
        {
            JsonOut j = S5Rows.Begin("s12.fx");
            j.Str("kind", kind).Str("grid", grid.ToString(CultureInfo.InvariantCulture)).Num("id", (long)id).Num("startSeq", (long)seq).Num("emitted", (long)emitted)
             .Num("asked", (long)asked).Bool("sound", sound).Num("totalAsked", totalAsked).Num("totalEmitted", totalEmitted).Num("budget", (long)S7Effects.BubblesPerFrame);
            S5Rows.End(j);
            if (kind == "splash" || kind == "burst")
                DeLog.Info("DE.S12 fx " + kind + " grid " + grid + " id " + id + " seq=" + seq + " bubbles " + emitted + "/" + asked + (kind == "splash" ? " sound=" + (sound ? "1" : "0") : ""));
        }

        // ============================================================================================ the look

        private static void Look(FrameReader c1, MirrorConsumer mirror, ulong nowMs, float now)
        {
            Camera cam = MainCamera.camera;
            bool inRoom = c1 != null && InsideRule.Inside(true, false, true, c1.InteriorSeen, c1.InteriorInRoom, c1.InteriorMs, nowMs);
            float camY = cam != null ? cam.transform.position.y : 1f;
            bool want = S12LookRules.Applies(LookOn && !DevMutants.IsOn(S12SnMutants.LookOff), inRoom, camY);
            if (want != _lookApplied)
            {
                if (want) LookApply(cam); else LookRestore("look.when false");
            }
            if (_lookApplied)
            {
                if (_shafts != null && S12Conventions.Look.SunShafts == 0 && _shafts.enabled) _shafts.enabled = false; // a quality change re-enables it
                if (now >= _nextBounceS) { _nextBounceS = now + 0.5f; Bounce(c1, mirror, cam); }
            }
            if (want != _lookWantLast || now >= _nextLookRowS)
            {
                _lookWantLast = want;
                _nextLookRowS = now + 2f;
                LookRow(want, inRoom, camY);
            }
        }

        private static void LookApply(Camera cam)
        {
            _lookApplied = true;
            if (cam != null)
            {
                WaterscapeVolumeOnCamera wv = cam.GetComponent<WaterscapeVolumeOnCamera>();
                _volume = wv != null ? wv.settings : null;
                if (_volume != null)
                {
                    _causticsBefore = _volume.causticsAmount;
                    _volume.causticsAmount = _causticsBefore * S12Conventions.Look.CausticsPermille / 1000f;
                }
                _shafts = cam.GetComponent<WaterSunShaftsOnCamera>();
                if (_shafts != null)
                {
                    _shaftsBefore = _shafts.enabled;
                    if (S12Conventions.Look.SunShafts == 0) _shafts.enabled = false;
                }
            }
            if (_bounce != null) _bounce.enabled = true;
            DeLog.Info("DE.S12 look ON (caustics " + Fmt(_causticsBefore) + " -> " + (_volume != null ? Fmt(_volume.causticsAmount) : "-") + ", sun shafts " +
                       (_shafts != null ? (_shaftsBefore ? "on" : "off") + " -> off" : "-") + ", the interior sky copy, the floor bounce)");
        }

        private static void LookRestore(string why)
        {
            if (!_lookApplied) return;
            _lookApplied = false;
            try { if (_volume != null && !float.IsNaN(_causticsBefore)) _volume.causticsAmount = _causticsBefore; } catch (Exception) { }
            try { if (_shafts != null) _shafts.enabled = _shaftsBefore; } catch (Exception) { }
            try { if (_bounce != null) _bounce.enabled = false; } catch (Exception) { }
            DeLog.Info("DE.S12 look OFF (" + why + "): caustics " + Fmt(_causticsBefore) + ", sun shafts " + (_shafts != null ? (_shaftsBefore ? "on" : "off") : "-") + " restored");
        }

        private static string Fmt(float v) { return float.IsNaN(v) ? "-" : v.ToString("0.###", CultureInfo.InvariantCulture); }

        private static void MakeSky(Sky src)
        {
            if (_skyCopy != null) try { UnityEngine.Object.Destroy(_skyCopy.gameObject); } catch (Exception) { }
            _skyCopy = null;
            _skySource = src;
            Root();
            GameObject go = UnityEngine.Object.Instantiate(src.gameObject, _root.transform, false);
            go.name = "DE.S12.InteriorSky";
            Sky s = go.GetComponent<Sky>();
            if (s == null) { UnityEngine.Object.Destroy(go); return; }
            s.CustomSH = null; // its OnEnable must keep the tinted coefficients
            s.DiffIntensity = src.DiffIntensity * S12Conventions.Look.AmbientPermille / 1000f;
            s.SpecIntensity = src.SpecIntensity * S12Conventions.Look.SpecPermille / 1000f;
            if (s.SH != null && src.SH != null)
            {
                s.SH.copyFrom(src.SH);
                for (int i = 0; i < 9; i++)
                    for (int ch = 0; ch < 3; ch++)
                        s.SH.c[i * 3 + ch] *= (float)S12LookRules.Channel(S12Conventions.Look.TintRgb, ch);
                s.SH.copyToBuffer();
            }
            s.Dirty = true;
            _skyCopy = s;
            DeLog.Info("DE.S12 interior sky copy of " + src.name + ": diffuse " + Fmt(src.DiffIntensity) + " -> " + Fmt(s.DiffIntensity) + ", specular " + Fmt(src.SpecIntensity) +
                       " -> " + Fmt(s.SpecIntensity) + ", SH tint #" + S12Conventions.Look.TintRgb.ToString("X6", CultureInfo.InvariantCulture));
        }

        /// <summary>The floor bounce (format look.preset): under the camera on its room box's floor, lit by the brightest mirrored lamp nearby.</summary>
        private static void Bounce(FrameReader c1, MirrorConsumer mirror, Camera cam)
        {
            if (cam == null || c1 == null || mirror == null) return;
            ulong gid = c1.InteriorValid ? c1.Interior.RoomGridId : 0;
            MirrorGridView v = gid != 0 ? View(mirror, (long)gid) : null;
            CellBox[] boxes = gid != 0 ? mirror.State.Rooms.BoxesOf(gid) : null;
            if (v == null || boxes == null || boxes.Length == 0) { if (_bounce != null) _bounce.enabled = false; return; }
            GridRecord gr;
            double g = mirror.State.Grids.TryGetValue(gid, out gr) ? InteriorChunks.GridSize(gr) : 2.5;
            Vector3 lu = v.EntityT.InverseTransformPoint(cam.transform.position);
            double sx = lu.x, sy = lu.y, sz = -lu.z; // SE-local
            Vector3 upU = v.EntityT.InverseTransformDirection(Vector3.up);
            int up = S12Boxes.UpAxisOf(upU.x, upU.y, -upU.z);
            CellBox? hit = null;
            int cx = S12AirVolume.Cell(sx, g), cy = S12AirVolume.Cell(sy, g), cz = S12AirVolume.Cell(sz, g);
            foreach (CellBox b in boxes) if (b.Contains(cx, cy, cz)) { hit = b; break; }
            if (!hit.HasValue) { if (_bounce != null) _bounce.enabled = false; return; }
            double floor, top;
            S12Boxes.FloorTop(hit.Value, g, up, out floor, out top);
            double hb = floor + S12Conventions.Look.BounceHeightMm / 1000.0;
            bool neg = up == S12Conventions.UpAxis.NegX || up == S12Conventions.UpAxis.NegY || up == S12Conventions.UpAxis.NegZ;
            double a = neg ? -hb : hb;
            if (up <= S12Conventions.UpAxis.NegX) sx = a; else if (up >= S12Conventions.UpAxis.PosZ) sz = a; else sy = a;
            Vector3 at = v.EntityT.TransformPoint(new Vector3((float)sx, (float)sy, (float)-sz));
            float range = (float)(S12Conventions.Look.BounceRangeCells * g);
            _bounceLamp = BrightestLamp(at, range);
            if (_bounce == null)
            {
                Root();
                var go = new GameObject("DE.S12.Bounce");
                go.transform.SetParent(_root.transform, false);
                _bounce = go.AddComponent<Light>();
                _bounce.type = LightType.Point;
                _bounce.shadows = LightShadows.None;
                int rgb = S12Conventions.Look.BounceRgb;
                _bounce.color = new Color((float)S12LookRules.Channel(rgb, 0), (float)S12LookRules.Channel(rgb, 1), (float)S12LookRules.Channel(rgb, 2));
            }
            _bounce.transform.position = at;
            _bounce.range = range;
            _bounce.intensity = (float)S12LookRules.BounceIntensity(_bounceLamp);
            _bounce.enabled = _bounce.intensity > 0f;
        }

        private static float BrightestLamp(Vector3 at, float range)
        {
            float now = Time.unscaledTime;
            if (_lightsRoot == null && now >= _nextLightsSearchS) { _nextLightsSearchS = now + 5f; _lightsRoot = GameObject.Find("DE.S7.Lights"); }
            if (_lightsRoot == null) return 0f;
            float best = 0f;
            foreach (Light l in _lightsRoot.GetComponentsInChildren<Light>(false))
            {
                if (l == null || !l.enabled || ReferenceEquals(l, _bounce)) continue;
                if ((l.transform.position - at).sqrMagnitude > range * range) continue;
                if (l.intensity > best) best = l.intensity;
            }
            return best;
        }

        private static void LookRow(bool want, bool inRoom, float camY)
        {
            JsonOut j = S5Rows.Begin("s12.look");
            j.Bool("want", want).Bool("applied", _lookApplied).Bool("on", LookOn).Bool("inRoom", inRoom).Num("camY", (double)camY)
             .Num("causticsBefore", float.IsNaN(_causticsBefore) ? -1.0 : (double)_causticsBefore).Num("causticsNow", _volume != null ? (double)_volume.causticsAmount : -1.0)
             .Bool("shaftsBefore", _shaftsBefore).Bool("shaftsNow", _shafts != null && _shafts.enabled).Bool("skyCopy", _skyCopy != null)
             .Num("skyDiff", _skyCopy != null ? (double)_skyCopy.DiffIntensity : -1.0).Num("bounce", _bounce != null && _bounce.enabled ? (double)_bounce.intensity : 0.0)
             .Num("lamp", (double)_bounceLamp).Bool("m6", DevMutants.IsOn(S12SnMutants.LookOff));
            S5Rows.End(j);
        }

        // ============================================================================================ life cycle, rows, commands

        private static void Root()
        {
            if (_root != null) return;
            _root = new GameObject("DE.S12");
            UnityEngine.Object.DontDestroyOnLoad(_root);
        }

        private static void Site()
        {
            _siteLogged = true;
            JsonOut j = S5Rows.Begin("s12.site");
            j.Str("header", S12Conventions.Header).Str("hash", "0x" + S12Conventions.Hash().ToString("x16", CultureInfo.InvariantCulture)).Bool("look", LookOn);
            S5Rows.End(j);
            DeLog.Info("DE.S12 " + S12Conventions.Header + " 0x" + S12Conventions.Hash().ToString("x16", CultureInfo.InvariantCulture) + " (flood planes, vent bubbles, the interior look)");
        }

        private static void Reset(string why)
        {
            foreach (PlaneSet p in _planes.Values) DropPlanes(p, why);
            _planes.Clear();
            _vents.Clear();
            LookRestore(why);
            try { if (_skyCopy != null) UnityEngine.Object.Destroy(_skyCopy.gameObject); } catch (Exception) { }
            _skyCopy = null;
            _skySource = null;
            _volume = null;
            _shafts = null;
            _causticsBefore = float.NaN;
            _siteLogged = false;
            _lightsRoot = null;
        }

        public static bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req == null || req.Cmd != Verb) return false;
            try
            {
                if (!BuildFlavor.Dev) { reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", UserCommandRules.RefusalText); return true; }
                Dictionary<string, object> a = S3Args.ToDict(req.Args);
                object v;
                S12SnArgs.Words(req.Args, a); // the chat's words (/de sn s12 look off)
                if (a.TryGetValue("water", out v))
                {
                    // dev: the flood water's material (base: BaseWaterPlane's own; fallback: the transparent plane)
                    string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
                    if (s != "base" && s != "fallback") { reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", "water: base|fallback"); return true; }
                    ForceFallback = s == "fallback";
                    _nextWaterSearchS = 0f;
                    DeLog.Info("DE.S12 water material " + s + " (de.s12)");
                }
                if (a.TryGetValue("look", out v))
                {
                    string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
                    if (s != "on" && s != "off") { reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", "look: on|off"); return true; }
                    LookOn = s == "on";
                    DeLog.Info("DE.S12 look " + s + " (de.s12)");
                }
                reply.Bool("ok", true).Str("cmd", req.Cmd).Bool("look", LookOn).Bool("lookApplied", _lookApplied).Num("planes", (long)_planes.Count).Num("vents", (long)_vents.Count)
                     .Str("water", _waterSource).Num("splashes", Splashes).Num("bursts", Bursts).Num("streamed", Streamed).Num("reads", Reads).Num("errors", Errors)
                     .Str("conventions", S12Conventions.Header + " 0x" + S12Conventions.Hash().ToString("x16", CultureInfo.InvariantCulture))
                     .Str("line", "DE.S12 look " + (LookOn ? "on" : "off") + (_lookApplied ? " (applied)" : "") + ", planes " + _planes.Count + " (" + _waterSource + "), vents " + _vents.Count);
                LookRow(_lookWantLast, false, MainCamera.camera != null ? MainCamera.camera.transform.position.y : 1f);
                return true;
            }
            catch (Exception e)
            {
                reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", e.GetType().Name + ": " + e.Message);
                return true;
            }
        }
    }
}
