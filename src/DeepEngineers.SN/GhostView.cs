using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeepEngineers.SN
{
    /// <summary>
 /// SE's block-placement ghost drawn in Subnautica (ROADMAP S7a "Placement ghost"; CONTRACT §5.4 "S7a conventions",
 /// "Placement ghost"). The ghost arrives as an ordinary mirrored grid (GridAdd with the <c>Ghost</c> bit, id
 /// <see cref="S7aGhost.EntityId"/>, one model part or a few cube parts) and is built and posed like any S5 grid
 /// (chunks, §2.8). This view only changes how its renderers draw: every renderer of the ghost gets one of two shared
 /// translucent materials, textures ignored: green <c>(0.25, 1.0, 0.35, 0.40)</c> while the newest C2 row has
 /// <c>ghostAllowed</c>, red <c>(1.0, 0.25, 0.20, 0.40)</c> otherwise; enabled only while the row has
 /// <c>ghostShown</c> (and the S5 cull keeps the chunk); <c>shadowCastingMode</c> Off, <c>receiveShadows</c> false; no
 /// collider, no SkyApplier change. Material source (first available with <c>shader.isSupported</c>; <c>de.s7
 /// {ghostMaterial}</c> overrides): the S5 vanilla glass clone (<c>S5Mirror.GlassSource</c>, tinted through
 /// <c>_Color</c>, <c>_Cull</c> 0 when the shader has it), <c>Shader.Find("Standard")</c> in Fade mode, <c>Shader.Find("Sprites/Default")</c> (two-sided); logged <c>DE.S7a ghost material=&lt;name&gt;</c>.
 /// Rows: <c>s7a.ghost</c> {snFrame, presentedSeTick, c2Tick, present, shown, allowed, material, renderers, root {o, q},
 /// alpha, poseErrMm, firstPresentedTick, …} on every change and at 10 Hz while present. Main thread; every step
 /// guarded (an exception disables the ghost view only, logged once).
 /// </summary>
    internal sealed class GhostView
    {
        /// <summary>The consumer's ghost view (for the de.s7 handler); null before the first consumer.</summary>
        public static GhostView Current { get; private set; }

        private readonly MirrorConsumer _m;
        private GhostMaterialSource _requested = GhostMaterialSource.Auto, _current = GhostMaterialSource.None;
        private Material _green, _red;
        private Shader _standard, _sprite;
        private bool _shadersLooked;
        private bool _shown, _allowed, _hasRow;
        private uint _flags;
        private ulong _c2Tick;
        private readonly GhostPresentTracker _tracker = new GhostPresentTracker();
        private readonly List<Renderer> _scratch = new List<Renderer>();
        private bool _disabled;
        private string _disabledWhy;

        // the last presenting sample
        private ulong _presented, _newestAtSample;
        private double _alpha = double.NaN, _poseErrMm = double.NaN, _poseErrDeg = double.NaN;   // F5: the angle is written too
        private readonly long[] _latWindow = new long[128];
        private int _latN, _latNext;
        private long _latMax = -1, _latLast = -1;

        // rows
        private GhostViewState _lastState;
        private bool _rowWritten;
        private ulong _nextRowMs;

        public long Builds, MaterialSwaps, MaterialsMade, RowsWritten, Frames;

        public GhostView(MirrorConsumer m)
        {
            _m = m;
            Current = this;
        }

        /// <summary><c>auto|glass|standard|sprite</c> (<c>de.s7 {ghostMaterial}</c>); a change re-makes the two materials.</summary>
        public string MaterialSource
        {
            get { return GhostRules.Name(_requested); }
            set
            {
                GhostMaterialSource s;
                if (!GhostRules.TryParseSource(value, out s)) throw new ArgumentException("ghostMaterial must be auto, glass, standard or sprite");
                _requested = s;
                DestroyMaterials();
                DeLog.Info("DE.S7a ghost material request=" + GhostRules.Name(s) + " (owner look; re-made at the next frame)");
            }
        }

        /// <summary>The source in use (none until the ghost was drawn once).</summary>
        public string MaterialInUse { get { return GhostRules.Name(_current); } }
        public bool Disabled { get { return _disabled; } }

        // ------------------------------------------------------------------------------------------ hooks

 /// <summary>
 /// A cell of an entity was uploaded (the S5 chunk path): for the ghost, its renderers take the ghost material at
 /// once (no frame is presented with the opaque Marmoset material).
 /// </summary>
        public void OnEntityBuilt(ulong id, uint gridFlags, IList<Renderer> renderers)
        {
            if (_disabled || !GhostRules.IsGhost(id, gridFlags)) return;
            try
            {
                Builds++;
                ReadNewestRow();
                EnsureMaterials();
                if (renderers != null) for (int i = 0; i < renderers.Count; i++) ApplyRenderer(renderers[i], true, false);
            }
            catch (Exception e) { Disable("build", e); }
        }

        /// <summary>The ghost's C2 row flags (newest snapshot): ghostShown, ghostAllowed.</summary>
        public void OnC2Row(ulong id, uint flags)
        {
            if (id != S7aGhost.EntityId) return;
            _flags = flags;
            _shown = S7aGhost.Shown(flags);
            _allowed = S7aGhost.Allowed(flags);
            _hasRow = true;
        }

        /// <summary>Every frame (the consumer's Update, after its materials and the S5 step): materials, colour, visibility, rows.</summary>
        public void ApplyFrame()
        {
            if (_disabled) return;
            try { ApplyFrameCore(); }
            catch (Exception e) { Disable("frame", e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ApplyFrameCore()
        {
            Frames++;
            MirrorGridView v = View();
            if (v == null)
            {
                if (_hasRow || _lastState.Present) { _tracker.Reset(); _hasRow = false; _shown = _allowed = false; }
                Row(null, false);
                return;
            }
            ReadNewestRow();
            EnsureMaterials();
            ApplyTo(v);
            Row(v, false);
        }

        /// <summary>
 /// The camera sample (after the mirror's poses; <paramref name="preCull"/> = the final placement of this frame): the
 /// newest row's colour and visibility before the render, and the presented-tick bookkeeping of the pose changes.
 /// </summary>
        public void OnCameraSample(CamBracket br, bool preCull)
        {
            if (_disabled) return;
            try
            {
                // review F6: the presented tick is kept even once the ghost view is gone, so the row that reports
 // present=false carries a tick at or past SE's off tick (S7a.N3.lifecycle "removed" timing)
                if (preCull && br.Valid)
                {
                    _presented = br.Newer;
                    _newestAtSample = br.NewestTick;
                }
                MirrorGridView v = View();
                if (v == null) return;
                ReadNewestRow();
                if (_green != null && _red != null) ApplyTo(v);
                if (!preCull || !br.Valid) return;
                _presented = br.Newer;
                _newestAtSample = br.NewestTick;
                _alpha = br.Alpha;
                PoseErr(v, br, out _poseErrMm, out _poseErrDeg);
                if (_tracker.OnPresented(br.Newer, br.NewestTick))
                {
                    long lat = br.NewestTick >= _tracker.LastChangeTick ? (long)(br.NewestTick - _tracker.LastChangeTick) : 0;
                    _latLast = lat;
                    if (lat > _latMax) _latMax = lat;
                    _latWindow[_latNext] = lat;
                    _latNext = (_latNext + 1) % _latWindow.Length;
                    if (_latN < _latWindow.Length) _latN++;
                }
            }
            catch (Exception e) { Disable("camera sample", e); }
        }

        /// <summary>The renderers of an entity's view (chunks and children), for <see cref="OnEntityBuilt"/>; reused list.</summary>
        public IList<Renderer> RenderersOf(MirrorGridView v)
        {
            _scratch.Clear();
            if (v == null) return _scratch;
            foreach (ChunkView c in v.Chunks.Values) if (c.Renderer != null) _scratch.Add(c.Renderer);
            foreach (ChildPartView ch in v.Children.Values) foreach (ChildRenderer r in ch.Renderers) if (r.Renderer != null) _scratch.Add(r.Renderer);
            return _scratch;
        }

        /// <summary>de.s7 {action: "ghost"}: one row now.</summary>
        public void RowNow()
        {
            try { Row(View(), true); }
            catch (Exception e) { DeLog.WarnOnce("s7a.ghostrow", "DE.S7a ghost row not written: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>A one-line status for de.s7 status.</summary>
        public string Describe()
        {
            MirrorGridView v = View();
            return "ghost " + (v != null ? "present" : "absent") + " shown=" + _shown + " allowed=" + _allowed + " material=" + GhostRules.Name(_current) +
                   " (request " + GhostRules.Name(_requested) + ") builds=" + Builds + " swaps=" + MaterialSwaps + " rows=" + RowsWritten +
                   " latencyP95Ticks=" + LatP95() + (_disabled ? " DISABLED (" + _disabledWhy + ")" : "");
        }

        // ------------------------------------------------------------------------------------------ internals

        private MirrorGridView View()
        {
            MirrorGridView v;
            return _m.ViewMap.TryGetValue(S7aGhost.EntityId, out v) && v.Alive ? v : null;
        }

        private void ReadNewestRow()
        {
            EntitySnapshot s = _m.C2.History.Newest;
            EntityPose p;
            if (s == null || !s.TryGet(S7aGhost.EntityId, out p)) return;
            OnC2Row(p.Id, p.Flags);
            _c2Tick = s.Tick;
            _tracker.NoteRow(s.Tick, p.Ox, p.Oy, p.Oz, p.Qx, p.Qy, p.Qz, p.Qw);
        }

        private void ApplyTo(MirrorGridView v)
        {
            S13Sn.GhostBegin(v, _m.Owned, _m.S5.GlassSource); // S13 hook: the camera inside the ghost's bounds (fade), the per-slot textured materials
            foreach (ChunkView c in v.Chunks.Values) if (c.Renderer != null) ApplyRenderer(c.Renderer, c.HasMesh, c.Culled, c.Slot != null ? c.Slot.Mat : null);
            foreach (ChildPartView ch in v.Children.Values) foreach (ChildRenderer r in ch.Renderers) if (r.Renderer != null) ApplyRenderer(r.Renderer, true, false, r.Slot != null ? r.Slot.Mat : null);
        }

        private void ApplyRenderer(Renderer r, bool hasMesh, bool culled, Material slotMat = null)
        {
            Material want = S13Sn.GhostMaterial(slotMat, _allowed, _allowed ? _green : _red); // S13 hook (point 7): the real block's normal map, tinted, else the flat S7a colour
            if (want != null && r.sharedMaterial != want) { r.sharedMaterial = want; MaterialSwaps++; }
            if (r.shadowCastingMode != ShadowCastingMode.Off) r.shadowCastingMode = ShadowCastingMode.Off;
            if (r.receiveShadows) r.receiveShadows = false;
            bool on = want != null && GhostRules.Visible(_shown, hasMesh, culled);
            if (r.enabled != on) r.enabled = on;
        }

        /// <summary>The two shared materials of the chosen source (made again when the source changes or Unity destroyed them).</summary>
        private void EnsureMaterials()
        {
            Material glass = _m.S5.GlassSource;
            bool glassOk = glass != null && glass.shader != null && glass.shader.isSupported;
            if (!_shadersLooked)
            {
                _shadersLooked = true;
                try { _standard = Shader.Find("Standard"); } catch (Exception) { _standard = null; }
                try { _sprite = Shader.Find("Sprites/Default"); } catch (Exception) { _sprite = null; }
            }
            bool standardOk = _standard != null && _standard.isSupported;
            bool spriteOk = _sprite != null && _sprite.isSupported;
            GhostMaterialSource want = GhostRules.Choose(_requested, glassOk, standardOk, spriteOk);
            if (want == _current && _green != null && _red != null) return;
            DestroyMaterials();
            _current = want;
            if (want == GhostMaterialSource.None)
            {
                DeLog.WarnOnce("s7a.ghostnone", "DE.S7a ghost material=none (no glass clone, Standard or Sprites/Default shader supported yet): the ghost stays hidden");
                return;
            }
            _green = Make(want, true, glass);
            _red = Make(want, false, glass);
            MaterialsMade += 2;
            DeLog.Info("DE.S7a ghost material=" + GhostRules.Name(want) + " (request " + GhostRules.Name(_requested) + "; glass " + (glassOk ? glass.name : "unavailable") +
                       ", standard " + (standardOk ? "ok" : "unavailable") + ", sprite " + (spriteOk ? "ok" : "unavailable") + "; " + MarmosetMaterials.Describe(_green) + ")");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private Material Make(GhostMaterialSource src, bool allowed, Material glass)
        {
            float r, g, b, a;
            GhostRules.Colour(allowed, out r, out g, out b, out a);
            var col = new Color(r, g, b, a);
            string name = "DE.S7a ghost " + (allowed ? "allowed" : "blocked") + " " + GhostRules.Name(src);
            Material m;
            switch (src)
            {
                case GhostMaterialSource.Glass:
                    m = new Material(glass) { name = name };
                    break;
                case GhostMaterialSource.Standard:
                    m = new Material(_standard) { name = name };
                    // Unity's Standard shader in Fade mode (what its editor GUI sets for "Fade")
                    m.SetFloat("_Mode", 2f);
                    m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                    m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                    m.SetInt("_ZWrite", 0);
                    m.DisableKeyword("_ALPHATEST_ON");
                    m.EnableKeyword("_ALPHABLEND_ON");
                    m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    break;
                default:
                    m = new Material(_sprite) { name = name };
                    break;
            }
            m.enableInstancing = false;
            if (m.HasProperty("_Color")) m.SetColor("_Color", col);
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f); // two-sided where the shader allows it
            m.renderQueue = GhostRules.TransparentQueue;
            return _m.Owned.Track(m);
        }

        private void DestroyMaterials()
        {
            if (!ReferenceEquals(_green, null)) _m.Owned.Destroy(_green);
            if (!ReferenceEquals(_red, null)) _m.Owned.Destroy(_red);
            _green = _red = null;
            _current = GhostMaterialSource.None;
        }

        /// <summary>
 /// The drawn root vs the C2 pose of the presented tick, when the bracket draws that snapshot exactly (alpha ≥ 0.999):
 /// the distance in mm and the angle in degrees between the drawn rotation and the C2 quaternion.
 /// </summary>
        private void PoseErr(MirrorGridView v, CamBracket br, out double mm, out double deg)
        {
            mm = deg = double.NaN;
            if (!(br.Alpha >= 0.999) || !v.HasPose || v.FrameT == null) return;
            EntitySnapshot s = _m.C2.History.Exactly(br.Newer);
            EntityPose p;
            if (s == null || !s.TryGet(S7aGhost.EntityId, out p)) return;
            Vector3 o = v.FrameT.position;
            mm = GhostRules.ErrMm(o.x, o.y, o.z, p.Ox, p.Oy, p.Oz);
            if (v.EntityT != null)
            {
                Quaternion q = v.EntityT.rotation;
                double d = Math.Abs(q.x * p.Qx + q.y * p.Qy + q.z * p.Qz + q.w * p.Qw);
                if (d > 1.0) d = 1.0;
                deg = 2.0 * Math.Acos(d) * 180.0 / Math.PI;
            }
        }

        private long LatP95()
        {
            if (_latN == 0) return -1;
            var a = new long[_latN];
            Array.Copy(_latWindow, a, _latN);
            Array.Sort(a);
            int rank = (int)Math.Ceiling(0.95 * _latN) - 1;
            return a[Math.Max(0, Math.Min(_latN - 1, rank))];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Row(MirrorGridView v, bool force)
        {
            int renderers = 0, enabled = 0;
            if (v != null)
            {
                foreach (ChunkView c in v.Chunks.Values) if (c.Renderer != null) { renderers++; if (c.Renderer.enabled) enabled++; }
                foreach (ChildPartView ch in v.Children.Values) foreach (ChildRenderer r in ch.Renderers) if (r.Renderer != null) { renderers++; if (r.Renderer.enabled) enabled++; }
            }
            var st = new GhostViewState { Present = v != null, Shown = v != null && _shown, Allowed = v != null && _allowed, Material = GhostRules.Name(_current), Renderers = renderers };
            ulong now = Win32.TickCount64();
            bool changed = !_rowWritten || !st.SameAs(_lastState);
            if (!force && !changed && !(st.Present && now >= _nextRowMs)) return;
            _rowWritten = true;
            _lastState = st;
            _nextRowMs = now + GhostRules.PresentRowMs;
            JsonOut j = S5Rows.Begin("s7a.ghost");
            j.Str("entity", GhostRules.Hex(S7aGhost.EntityId)).Num("snFrame", (long)DeLog.Frame).Num("presentedSeTick", _presented).Num("c2Tick", _c2Tick)
             .Bool("present", st.Present).Bool("shown", st.Shown).Bool("allowed", st.Allowed).Num("c2Flags", (long)_flags)
             .Str("material", st.Material).Str("materialRequest", GhostRules.Name(_requested)).Num("renderers", (long)renderers).Num("renderersEnabled", (long)enabled);
            if (v != null && v.HasPose && v.FrameT != null && v.EntityT != null)
            {
                Vector3 o = v.FrameT.position;
                Quaternion q = v.EntityT.rotation;
                j.Obj("root").Vec("o", o.x, o.y, o.z).Quat4("q", q.x, q.y, q.z, q.w).EndObj();
                j.Obj("pose").Vec("o", v.LastPose.Ox, v.LastPose.Oy, v.LastPose.Oz).Quat4("q", v.LastPose.Qx, v.LastPose.Qy, v.LastPose.Qz, v.LastPose.Qw)
                 .Num("s", v.LastPose.S).Num("tick", v.LastTick).Str("how", v.LastHow.ToString()).EndObj();
            }
            if (!double.IsNaN(_alpha)) j.Num("alpha", _alpha);
            if (!double.IsNaN(_poseErrMm)) j.Num("poseErrMm", _poseErrMm);
            if (!double.IsNaN(_poseErrDeg)) j.Num("poseErrDeg", _poseErrDeg);   // review F5
            j.Num("newestTick", _newestAtSample).Num("changeTick", _tracker.LastChangeTick).Num("firstPresentedTick", _tracker.FirstPresentedTick)
             .Num("newestAtPresent", _tracker.NewestAtPresent).Num("changes", _tracker.Changes).Num("presentedChanges", _tracker.Presented)
             .Num("latencyLastTicks", _latLast).Num("latencyP95Ticks", LatP95()).Num("latencyMaxTicks", _latMax)
             .Num("builds", Builds).Num("swaps", MaterialSwaps);
            S5Rows.End(j);
            RowsWritten++;
        }

        private void Disable(string step, Exception e)
        {
            _disabled = true;
            _disabledWhy = step + ": " + e.GetType().Name + ": " + e.Message;
            DeLog.Error("DE.S7a FEATURE ghost view DISABLED after " + _disabledWhy + " (the ghost is drawn as an ordinary mirrored grid; the rest of the mirror keeps running)");
        }
    }
}
