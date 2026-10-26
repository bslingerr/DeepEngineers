using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S13 "Character and animation v2", the Subnautica side (ROADMAP §14 "### S13"; CONTRACT §5.4 "S13 conventions"). Main
 /// thread only; every entry point guarded (a failure logs once and turns off only the S13 part it belongs to: the S5/S5a/
 /// S7/S7a behaviour then stays). Called from the hooks in S5aHost (arms, dive sounds, animation), CharacterMirror (sources,
 /// tool rows), LightMirror (near-field rules), GhostView (textured ghost), S5Host (rows at 1 Hz, de.s13). Game members
 /// VERIFIED <c>WaterAmbience.diveStart</c>, <c>diveStartSplash</c>,
 /// the private <c>PlayReachSurfaceSound</c> and the dive rule <c>-velocity.y &gt; 5.5 → diveStartSplash</c>:
 /// the ghost never fires them itself
 /// <c>FMOD_CustomEmitter.Play</c>; Unity <c>Light.shadowNearPlane</c>,
 /// <c>shadowBias</c>, <c>shadowNormalBias</c>; <c>Physics.Raycast</c>.
 /// Rows (sn-*.jsonl): <c>s13.char</c> (1 Hz: the per-frame counters of points 1, 2, 3, 5, 6), <c>s13.light</c> (1 Hz while
 /// the suit lamp casts: near plane, fit, the measured surface distance and the shadow a 0.1 m caster at the near plane
 /// throws on it), <c>s13.ghost</c> (1 Hz while the placement ghost exists: textured / flat renderers, camera inside).
 /// </summary>
    internal static class S13Sn
    {
        public const string Verb = "de.s13";

        // live switches (de.s13)
        public static bool GhostTextured = !string.Equals(Env("DEEPENGINEERS_S13_GHOST"), "flat", StringComparison.OrdinalIgnoreCase);
        public static bool LightFitOn = !string.Equals(Env("DEEPENGINEERS_S13_LIGHTFIT"), "off", StringComparison.OrdinalIgnoreCase);
        public static bool LightFitBlockSpots;
        public static float CharacterNearPlane = float.NaN;
        public static bool SoundsOn = true;

        public static readonly S13CharCounters Counters = new S13CharCounters();
        private static readonly HashSet<string> Off = new HashSet<string>(StringComparer.Ordinal);
        private static ulong _nextCharRowMs, _nextLightRowMs, _nextGhostRowMs;

        private static string Env(string n) { try { return Environment.GetEnvironmentVariable(n); } catch (Exception) { return null; } }

        private static void Disable(string what, Exception e)
        {
            if (Off.Add(what)) DeLog.Warn("DE.S13 " + what + " OFF after " + e.GetType().Name + ": " + e.Message + " (the earlier behaviour stays)");
        }

        // ============================================================================================= point 1: arms leak

 /// <summary>S5aHost hook, every LateUpdateLast frame: the diver's view model drawn under fpbody=se is the leak (A1).</summary>
        public static void ArmsFrame(bool visible, bool forceOff, bool firstPerson)
        {
            Counters.Frames++;
            if (!forceOff) return;
            Counters.ForceOffFrames++;
            if (firstPerson) Counters.FirstPersonForceOffFrames++;
            if (S13Arms.Leak(visible, forceOff)) Counters.LeakVisibleFrames++;   // the flag; the drawn state is PreCullArms'
        }

        /// <summary>
 /// S5aHost hook at onPreCull: under fpbody=se (gate ForceOff), a frame with any
 /// of the view model's renderers enabled and active in the hierarchy is a leak (A1), whatever set it.
 /// </summary>
        public static void PreCullArms(Renderer[] arms, bool forceOff, bool firstPerson)
        {
            if (Off.Contains("precull")) return;
            try
            {
                Counters.PreCullFrames++;
                if (!forceOff || arms == null) return;
                Counters.PreCullForceOffFrames++;
                int drawn = 0;
                for (int i = 0; i < arms.Length; i++)
                {
                    Renderer r = arms[i];
                    if (r != null && r.enabled && r.gameObject.activeInHierarchy) drawn++;
                }
                if (drawn == 0) return;
                Counters.LeakFrames++;
                if (Counters.LeakFrames == 1 || Counters.LeakFrames % 600 == 0)
                    DeLog.Warn("DE.S13 arms LEAK: " + drawn + " of the diver view model's renderers drawn under fpbody=se (" + (firstPerson ? "first" : "third") + " person; frames " + Counters.LeakFrames + ")");
            }
            catch (Exception e) { Disable("precull", e); }
        }

        // ============================================================================================= point 1: dive sounds

        private static bool _eyeUnderKnown, _eyeUnder;
        private static float _nextDiveS;
        private static WaterAmbience _ambience;
        private static MethodInfo _reachSurface;
        private static float _nextLookS;

        /// <summary>
 /// S5aHost hook, every frame: the character's eye crossing the surface in the water (C1 swim flags InWater, Underwater)
 /// plays Subnautica's own dive or surface sound (WaterAmbience), which the ghost never plays itself. The dive splash
 /// variant above 5.5 m/s downward as the game (C1 loco vUp), the surface variant by the game's own oxygen rule (the
 /// mirrored oxygen). Dives at most once a second (the eye bobbing at the surface).
 /// </summary>
        public static void SwimFrame(bool extValid, byte flags, bool locoValid, short vUpCm, double o2Fraction, bool firstPerson)
        {
            if (Off.Contains("sounds")) return;
            try
            {
                bool inWater = extValid && (flags & SwimWireFlags.InWater) != 0;
                if (!inWater) { _eyeUnderKnown = false; return; }
                bool under = (flags & SwimWireFlags.Underwater) != 0;
                if (!_eyeUnderKnown) { _eyeUnderKnown = true; _eyeUnder = under; return; }
                if (under == _eyeUnder) return;
                _eyeUnder = under;
                Counters.Splashes++;
                if (firstPerson) Counters.SplashesFirstPerson++;   //  the A1 row needs first-person crossings
                if (!SoundsOn) return;
                float now = Time.unscaledTime;
                WaterAmbience wa = Ambience(now);
                if (wa == null) return;
                if (under)
                {
                    if (now < _nextDiveS) return;
                    _nextDiveS = now + 1f;
                    double vy = locoValid ? vUpCm / 100.0 : 0.0;
                    FMOD_CustomEmitter e = -vy > 5.5 ? wa.diveStartSplash : wa.diveStart;
                    if (e != null) { e.Play(); Counters.SplashSounds++; }
                }
                else if (_reachSurface != null)
                {
                    _reachSurface.Invoke(wa, null);
                    Counters.SplashSounds++;
                }
            }
            catch (Exception e) { Disable("sounds", e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WaterAmbience Ambience(float now)
        {
            if (_ambience != null) return _ambience;
            if (now < _nextLookS) return null;
            _nextLookS = now + 5f;
            Player p = Player.main;
            WaterAmbience wa = p != null ? p.GetComponentInChildren<WaterAmbience>(true) : null;
            if (wa == null)
                foreach (WaterAmbience w in Resources.FindObjectsOfTypeAll<WaterAmbience>())
                    if (w != null && w.gameObject != null && w.gameObject.scene.IsValid()) { wa = w; break; }
            if (wa == null) { DeLog.WarnOnce("s13.ambience", "DE.S13 MISSING WaterAmbience (retried every 5 s): no dive/surface sound"); return null; }
            _ambience = wa;
            try { _reachSurface = typeof(WaterAmbience).GetMethod("PlayReachSurfaceSound", BindingFlags.Instance | BindingFlags.NonPublic); } catch (Exception) { _reachSurface = null; }
            DeLog.Info("DE.S13 dive sounds: WaterAmbience on '" + wa.gameObject.name + "' diveStart=" + (wa.diveStart != null) + " diveStartSplash=" + (wa.diveStartSplash != null) +
                       " PlayReachSurfaceSound=" + (_reachSurface != null ? "ok" : "MISSING"));
            return wa;
        }

        // ============================================================================================= point 5: animation

 /// <summary>S5aHost hook: the animator's target from the C1 S13 extension (false: S5a's input rule).</summary>
        public static bool AnimTarget(FrameReader r, out double x, out double y, out double z)
        {
            x = y = z = 0;
            if (r == null || Off.Contains("anim")) return false;
            S13CharWireState s = r.S13Char;
            bool ok = S13Anim.Target(r.S13Valid, ref s, DevMutants.IsOn(S13SnMutants.AnimInput), out x, out y, out z);
            if (ok) Counters.AnimFromExt++;
            return ok;
        }

        /// <summary>S5aHost hook: the agreement of the animator's horizontal direction with the body-relative velocity (A5).</summary>
        public static void AnimFrame(FrameReader r, double ax, double az, bool inWater)
        {
            if (r == null || !inWater) return;
            Counters.AnimFrames++;
            if (!r.S13Valid) return;
            S13CharWireState s = r.S13Char;
            if (!s.Has(S13Conventions.C1Flags.Valid)) return;
            Counters.NoteAgreement(S13Anim.Agreement(ax, az, s.RightMs, s.ForwardMs));
        }

        // ============================================================================================= points 2, 6: sources

        private static bool[] _mask;
        private static CanonSkeleton _maskSk;

        /// <summary>CharacterMirror hook: the Blend mask of the canonical skeleton (built once per skeleton).</summary>
        public static bool[] BlendMaskFor(CanonSkeleton sk)
        {
            if (sk == null) return null;
            if (!ReferenceEquals(sk, _maskSk) || _mask == null)
            {
                var names = new List<string>(sk.Count);
                foreach (CanonBone b in sk.Bones) names.Add(b.SeBone);
                _mask = S13Source.BlendMask(sk.Parents, names);
                _maskSk = sk;
                int n = 0;
                foreach (bool m in _mask) if (m) n++;
                DeLog.Info("DE.S13 blend mask: " + n + " of " + _mask.Length + " canon1 bones take the swim retarget (" + string.Join(", ", S13Conventions.BlendRoots) + " and below)");
            }
            return _mask;
        }

        /// <summary>CharacterMirror hook (every counted pose): the A2 and A6 counters.</summary>
        public static void NoteSource(ref CharSourceInputs si, bool swimValid, byte flags, byte mode, CharSource src, bool count)
        {
            if (!count) return;
            bool swimSource = src == CharSource.Retarget || src == CharSource.Blend;
            if (S13Source.SubmergedGrounded(swimValid, flags, mode, si.Seated || si.Ladder, si.Dead))
            {
                Counters.SubmergedGroundedFrames++;
                if (CharSourceRules.Swimming(ref si)) Counters.SubmergedGroundedSwim++;
                if (swimSource) Counters.SubmergedGroundedSourceSwim++;
            }
            if (si.ToolHeld && CharSourceRules.Swimming(ref si))
            {
                Counters.HeldSwimFrames++;
                if (src == CharSource.Blend) Counters.HeldSwimBlend++;
                else if (src == CharSource.SeBones) Counters.HeldSwimSeBones++;
            }
        }

        // ============================================================================================= point 3: tool rows

 /// <summary>
 /// CharacterMirror hook (each pose): a character child part (the held tool and its subparts) stays hidden until its
 /// first C2 row (before it, the S5 static local is identity: the tool sat at the character's feet for the frames
 /// between its PartAdd and its first row: the equip "break"); shown again with its slot once the row came.
 /// </summary>
        public static void GateToolChild(ChildPartView c, bool count)
        {
            if (c == null || Off.Contains("toolgate")) return;
            try
            {
                if (!c.RowSeen)
                {
                    bool shown = false;
                    foreach (ChildRenderer r in c.Renderers)
                        if (r.Renderer != null && r.Renderer.enabled) { r.Renderer.enabled = false; shown = true; }
                    if (count) { Counters.ToolFramesNoRow++; if (shown) Counters.ToolFramesShownNoRow++; }
                    return;
                }
                if (count) Counters.ToolFramesWithRow++;
                foreach (ChildRenderer r in c.Renderers)
                    if (r.Renderer != null && !r.Renderer.enabled && r.Slot != null && r.Slot.Mat != null && ReferenceEquals(r.Renderer.sharedMaterial, r.Slot.Mat)) r.Renderer.enabled = true;
            }
            catch (Exception e) { Disable("toolgate", e); }
        }

        // ============================================================================================= point 4: lights

        private static Light _suit;
        private static float _suitNear, _suitK = 1f, _suitI = 1f, _suitFalloff;
        private static bool _suitShadows;
        private static int _suitFrame = -1;
        public static long LightsTouched;

        /// <summary>LightMirror hook (each drawn row): SE's near-field shadow rules and the falloff fit of the kind.</summary>
        public static void LightLook(Light l, LightKind kind, float falloff, bool shadows)
        {
            if (l == null || Off.Contains("light")) return;
            try
            {
                bool mutant = DevMutants.IsOn(S13SnMutants.LightUnity);
                float k, ki;
                S13LightRules.Fit(kind, falloff, LightFitOn, LightFitBlockSpots, mutant, out k, out ki);
                if (k != 1f) l.range *= k;
                if (ki != 1f) l.intensity *= ki;
                if (shadows)
                {
                    float near = S13LightRules.NearPlane(kind, mutant, CharacterNearPlane);
                    if (Math.Abs(l.shadowNearPlane - near) > 1e-4f) l.shadowNearPlane = near;
                    if (Math.Abs(l.shadowBias - S13LightRules.ShadowBias) > 1e-5f) l.shadowBias = S13LightRules.ShadowBias;
                    if (Math.Abs(l.shadowNormalBias - S13LightRules.ShadowNormalBias) > 1e-5f) l.shadowNormalBias = S13LightRules.ShadowNormalBias;
                }
                LightsTouched++;
                if (kind == LightKind.CharacterSpot && _suitFrame != Time.frameCount)
                {
                    _suitFrame = Time.frameCount; // rows are nearest first: the first character spot is the player's lamp
                    _suit = l;
                    _suitNear = l.shadowNearPlane;
                    _suitK = k;
                    _suitI = ki;
                    _suitFalloff = falloff;
                    _suitShadows = shadows;
                }
            }
            catch (Exception e) { Disable("light", e); }
        }

        // ============================================================================================= point 7: ghost

        private sealed class GhostSlot
        {
            public Material Green, Red;
            public Texture Bump;
        }

        /// <summary>Review 4: the textured ghost's material source, chosen once by a render probe (Unknown until it ran).</summary>
        private enum TexSource { Unknown = 0, Standard = 1, Glass = 2, None = 3 }

        private static readonly Dictionary<Material, GhostSlot> GhostSlots = new Dictionary<Material, GhostSlot>();
        private static MirrorObjects _ghostOwned;
        private static Material _glass;
        private static Shader _standard;
        private static bool _standardLooked, _inside, _insideApplied;
        private static int _ghostTextured, _ghostFlat, _ghostFrame = -1;
        private static TexSource _tex = TexSource.Unknown;
        private static Texture _probeBump;
        private static string _probeLine = "not run", _texShader = "-";
        public static long GhostMaterialsMade, GhostProbes;

        /// <summary>GhostView hook (each apply): the camera inside the ghost's drawn bounds (fade), the glass source, the frame's counters.</summary>
        public static void GhostBegin(MirrorGridView v, MirrorObjects owned, Material glass)
        {
            if (Off.Contains("ghost")) return;
            try
            {
                _ghostOwned = owned;
                _glass = glass;
                if (_ghostFrame != Time.frameCount) { _ghostFrame = Time.frameCount; _ghostTextured = _ghostFlat = 0; }
                Camera cam = MainCamera.camera;
                bool inside = false;
                if (cam != null && v != null)
                {
                    Vector3 p = cam.transform.position;
                    bool any = false;
                    Bounds b = default(Bounds);
                    foreach (ChunkView c in v.Chunks.Values)
                        if (c.Renderer != null && c.HasMesh) { if (!any) { b = c.Renderer.bounds; any = true; } else b.Encapsulate(c.Renderer.bounds); }
                    foreach (ChildPartView ch in v.Children.Values)
                        foreach (ChildRenderer r in ch.Renderers)
                            if (r.Renderer != null) { if (!any) { b = r.Renderer.bounds; any = true; } else b.Encapsulate(r.Renderer.bounds); }
                    if (any) inside = S13GhostRules.Inside(p.x, p.y, p.z, b.min.x, b.min.y, b.min.z, b.max.x, b.max.y, b.max.z, 0.3f);
                }
                _inside = inside;
                if (_inside != _insideApplied)
                {
                    _insideApplied = _inside;
                    foreach (GhostSlot s in GhostSlots.Values) { Tint(s.Green, true); Tint(s.Red, false); }
                }
            }
            catch (Exception e) { Disable("ghost", e); }
        }

        /// <summary>
 /// GhostView hook (each renderer): the textured ghost material of the renderer's own slot
 /// on the source the render probe chose, else the flat S7a material. Albedo is not used: SE's <c>_cm</c> alpha is metalness.
 /// </summary>
        public static Material GhostMaterial(Material slotMat, bool allowed, Material flat)
        {
            if (Off.Contains("ghost") || slotMat == null) { _ghostFlat++; return flat; }
            try
            {
                Texture bump = slotMat.HasProperty("_BumpMap") ? slotMat.GetTexture("_BumpMap") : null;
                if (!S13GhostRules.Textured(GhostTextured, bump != null, DevMutants.IsOn(S13SnMutants.GhostFlat))) { _ghostFlat++; return flat; }
                if (_tex == TexSource.Unknown) { if (_probeBump == null) _probeBump = bump; _ghostFlat++; return flat; } // the probe runs in the next Update (S13Sn.Tick)
                if (_tex == TexSource.None) { _ghostFlat++; return flat; }
                GhostSlot s;
                if (!GhostSlots.TryGetValue(slotMat, out s) || s.Green == null || s.Red == null)
                {
                    s = new GhostSlot { Green = Track(Make(_tex, true, bump)), Red = Track(Make(_tex, false, bump)), Bump = bump };
                    GhostSlots[slotMat] = s;
                    GhostMaterialsMade += 2;
                }
                else if (!ReferenceEquals(s.Bump, bump))
                {
                    s.Bump = bump;
                    s.Green.SetTexture("_BumpMap", bump);
                    s.Red.SetTexture("_BumpMap", bump);
                }
                _ghostTextured++;
                return allowed ? s.Green : s.Red;
            }
            catch (Exception e) { Disable("ghost", e); _ghostFlat++; return flat; }
        }

        private static bool StandardOk()
        {
            if (!_standardLooked) { _standardLooked = true; try { _standard = Shader.Find("Standard"); } catch (Exception) { _standard = null; } }
            return _standard != null && _standard.isSupported;
        }

        private static Material Track(Material m) { return _ghostOwned != null && m != null ? _ghostOwned.Track(m) : m; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Material Make(TexSource src, bool allowed, Texture bump)
        {
            Material m;
            if (src == TexSource.Glass)
            {
                m = new Material(_glass) { name = "DE.S13 ghost " + (allowed ? "allowed" : "blocked") + " (Subnautica glass + the block's normal map)" };
                if (m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", bump);
                if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
            }
            else
            {
                m = new Material(_standard) { name = "DE.S13 ghost " + (allowed ? "allowed" : "blocked") + " (Standard Fade + the block's normal map)" };
                m.SetFloat("_Mode", 2f);
                m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.DisableKeyword("_ALPHATEST_ON");
                m.EnableKeyword("_ALPHABLEND_ON");
                m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                if (m.HasProperty("_BumpMap")) { m.SetTexture("_BumpMap", bump); m.EnableKeyword("_NORMALMAP"); }
                if (m.HasProperty("_BumpScale")) m.SetFloat("_BumpScale", 1f);
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.55f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            }
            m.enableInstancing = false;
            m.renderQueue = GhostRules.TransparentQueue;
            Tint(m, allowed);
            return m;
        }

        private static void Tint(Material m, bool allowed)
        {
            if (m == null) return;
            float r, g, b, a;
            S13GhostRules.Tint(allowed, _inside, out r, out g, out b, out a);
            if (m.HasProperty("_Color")) m.SetColor("_Color", new Color(r, g, b, a));
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", new Color(r * S13GhostRules.Emission, g * S13GhostRules.Emission, b * S13GhostRules.Emission, 1f));
            }
        }

        /// <summary>
 /// Review 4 (Update only, once per process): Subnautica's player build may strip the Standard variants the ghost needs
 /// (<c>_ALPHABLEND_ON</c>, <c>_NORMALMAP</c>, <c>_EMISSION</c>): <c>shader.isSupported</c> stays true and the ghost would draw
 /// opaque. Each candidate (Standard Fade, then the glass clone) draws its "allowed" material with <c>Graphics.Blit</c>
 /// over a 4x4 target cleared to (0, 0, 0, 0) and is kept when the centre texel is translucent and tinted
 /// (<see cref="S13GhostRules.ProbeOk"/>); none: the flat S7a material. One line <c>DE.S13 ghost shader …</c> with the
 /// shader, its keywords and the probe colours; the s13.ghost row carries the source and the shader.
 /// </summary>
        private static void ProbeGhost()
        {
            if (_tex != TexSource.Unknown || _probeBump == null) return;
            GhostProbes++;
            var sb = new System.Text.StringBuilder(256);
            TexSource pick = TexSource.None;
            if (StandardOk() && ProbeOne(TexSource.Standard, sb)) pick = TexSource.Standard;
            else if (_glass != null && _glass.shader != null && _glass.shader.isSupported && ProbeOne(TexSource.Glass, sb)) pick = TexSource.Glass;
            _tex = pick;
            _texShader = pick == TexSource.Standard ? (_standard != null ? _standard.name : "?")
                       : pick == TexSource.Glass && _glass != null && _glass.shader != null ? _glass.shader.name : "flat (S7a)";
            _probeLine = sb.ToString();
            DeLog.Info("DE.S13 ghost shader -> " + (pick == TexSource.None ? "none: the flat S7a material" : _texShader) + " (" + _probeLine + ")");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ProbeOne(TexSource src, System.Text.StringBuilder sb)
        {
            Material m = null;
            RenderTexture rt = null, prev = RenderTexture.active;
            Texture2D tex = null;
            Color c = new Color(-1f, -1f, -1f, -1f);
            bool ran = false;
            try
            {
                m = Make(src, true, _probeBump);
                rt = RenderTexture.GetTemporary(4, 4, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                RenderTexture.active = rt;
                GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
                Graphics.Blit(Texture2D.whiteTexture, rt, m, 0);
                RenderTexture.active = rt;
                tex = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
                tex.ReadPixels(new Rect(0, 0, 4, 4), 0, 0);
                tex.Apply(false);
                c = tex.GetPixel(2, 2);
                ran = true;
            }
            catch (Exception e) { sb.Append(src).Append(" probe failed ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append("; "); }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
            bool ok = ran && S13GhostRules.ProbeOk(c.r, c.g, c.b, c.a, src == TexSource.Glass);
            if (m != null)
            {
                string[] kw = m.shaderKeywords;
                sb.Append(src).Append(" shader=").Append(m.shader != null ? m.shader.name : "?").Append(" supported=").Append(m.shader != null && m.shader.isSupported)
                  .Append(" keywords=[").Append(kw != null ? string.Join(" ", kw) : "").Append("] probe rgba=(")
                  .Append(c.r.ToString("0.###", CultureInfo.InvariantCulture)).Append(',').Append(c.g.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                  .Append(c.b.ToString("0.###", CultureInfo.InvariantCulture)).Append(',').Append(c.a.ToString("0.###", CultureInfo.InvariantCulture)).Append(") ")
                  .Append(ok ? "kept" : "rejected (opaque or untinted: a stripped variant)").Append("; ");
                UnityEngine.Object.Destroy(m);
            }
            return ok;
        }

        // ============================================================================================= rows, world, de.s13

 /// <summary>S5Host hook (bridge Update): the 1 Hz rows; materials of a gone world released.</summary>
        public static void Tick(ulong now, MirrorConsumer m)
        {
            if (Off.Contains("rows")) return;
            try
            {
                if (m == null || !m.WorldReady) { if (GhostSlots.Count > 0) ReleaseGhost(); return; }
                if (_tex == TexSource.Unknown && _probeBump != null && !Off.Contains("ghost")) { try { ProbeGhost(); } catch (Exception e) { _tex = TexSource.None; Disable("ghostprobe", e); } }
                if (now >= _nextCharRowMs) { _nextCharRowMs = now + 1000; CharRow(m); ToolRows(m, now); }
                if (now >= _nextLightRowMs && _suit != null && _suitShadows && Time.frameCount - _suitFrame < 5) { _nextLightRowMs = now + 1000; LightRow(); }
                if (now >= _nextGhostRowMs && Time.frameCount - _ghostFrame < 5) { _nextGhostRowMs = now + 1000; GhostRow(); }
            }
            catch (Exception e) { Disable("rows", e); }
        }

        private static void ReleaseGhost()
        {
            foreach (GhostSlot s in GhostSlots.Values)
            {
                if (_ghostOwned != null) { if (s.Green != null) _ghostOwned.Destroy(s.Green); if (s.Red != null) _ghostOwned.Destroy(s.Red); }
            }
            GhostSlots.Clear();
        }

        private static void CharRow(MirrorConsumer m)
        {
            S13CharCounters c = Counters;
            JsonOut j = S5Rows.Begin("s13.char");
            j.Num("frames", c.Frames).Num("forceOffFrames", c.ForceOffFrames).Num("firstPersonForceOffFrames", c.FirstPersonForceOffFrames).Num("leakFrames", c.LeakFrames)
             .Num("submergedGrounded", c.SubmergedGroundedFrames).Num("submergedGroundedSwim", c.SubmergedGroundedSwim).Num("submergedGroundedSourceSwim", c.SubmergedGroundedSourceSwim)
             .Num("heldSwim", c.HeldSwimFrames).Num("heldSwimBlend", c.HeldSwimBlend).Num("heldSwimSeBones", c.HeldSwimSeBones)
             .Num("toolNoRow", c.ToolFramesNoRow).Num("toolShownNoRow", c.ToolFramesShownNoRow).Num("toolWithRow", c.ToolFramesWithRow)
             .Num("animFrames", c.AnimFrames).Num("animFromExt", c.AnimFromExt).Num("animAgreeSamples", c.AnimAgreeSamples).Num("animAgreeBad", c.AnimAgreeBad)
             .Num("animAgreeMin", double.IsNaN(c.AnimAgreeMin) ? -2.0 : c.AnimAgreeMin)
             .Num("splashes", c.Splashes).Num("splashSounds", c.SplashSounds).Num("splashesFirstPerson", c.SplashesFirstPerson)
             .Num("preCullFrames", c.PreCullFrames).Num("preCullForceOffFrames", c.PreCullForceOffFrames).Num("leakVisibleFrames", c.LeakVisibleFrames)
             .Str("source", m.Character != null ? CharSourceRules.Name(m.Character.Source) : "none").Str("mutants", ActiveMutants()).Str("off", string.Join(",", new List<string>(Off).ToArray()));
            S5Rows.End(j);
        }

        private static readonly Dictionary<ulong, ulong> ToolFirstSeenMs = new Dictionary<ulong, ulong>();

        /// <summary>
 /// Review 1: one s13.tooln row per held tool part per second: the colour Subnautica received (the PartAdd's H/S/V),
 /// whether it is SE's red key, and per renderer slot the T6 paint state (applied, pending, converting, none, approx,
 /// foreign) and the slot's colour, with the part's age (a pending bake older than 10 s fails S13.A3).
 /// </summary>
        private static void ToolRows(MirrorConsumer m, ulong now)
        {
            CharacterMirror ch = m.Character;
            if (ch == null) return;
            ColorMaskPaint paint = m.Textures != null ? m.Textures.ColorMask : null;
            bool bake = paint != null && paint.Settings != null && paint.Settings.Bake;
            var alive = new HashSet<ulong>();
            foreach (ChildPartView c in ch.Children)
            {
                if (c == null || c.Class != S5Conv.ClassTool) continue;
                alive.Add(c.PartKey ^ c.ModelKey);
                ulong first;
                if (!ToolFirstSeenMs.TryGetValue(c.PartKey ^ c.ModelKey, out first)) { first = now; ToolFirstSeenMs[c.PartKey ^ c.ModelKey] = now; }
                JsonOut j = S5Rows.Begin("s13.tooln");
                j.Str("part", "0x" + c.PartKey.ToString("x", CultureInfo.InvariantCulture)).Str("model", "0x" + c.ModelKey.ToString("x16", CultureInfo.InvariantCulture))
                 .Num("h", (double)c.H).Num("s", (double)c.S).Num("v", (double)c.V).Bool("red", S13ToolSlot.Red(c.H, c.S)).Bool("rowSeen", c.RowSeen)
                 .Num("ageS", (now - first) / 1000.0).Bool("bakeMode", bake);
                j.Arr("slots");
                foreach (ChildRenderer r in c.Renderers)
                {
                    MaterialSlot sl = r.Slot;
                    TextureSet set = sl != null ? sl.Set : null;
                    bool foreign = sl != null && r.Renderer != null && sl.Mat != null && !ReferenceEquals(r.Renderer.sharedMaterial, sl.Mat);
                    string st = S13ToolSlot.State(sl != null, foreign, bake, set != null, set != null && set.ConvertState == TextureSet.Conv.Ready,
                                                  set != null ? set.PaintMax : 0f, sl != null && sl.Mask != null && sl.Mask.Applied);
                    j.Obj(null).Str("state", st).Num("h", sl != null ? (double)sl.H : double.NaN).Num("s", sl != null ? (double)sl.S : double.NaN)
                     .Num("v", sl != null ? (double)sl.V : double.NaN).Num("paintMax", set != null ? (double)set.PaintMax : double.NaN).EndObj();
                }
                j.EndArr();
                S5Rows.End(j);
            }
            if (ToolFirstSeenMs.Count > alive.Count)
            {
                var gone = new List<ulong>();
                foreach (ulong k in ToolFirstSeenMs.Keys) if (!alive.Contains(k)) gone.Add(k);
                foreach (ulong k in gone) ToolFirstSeenMs.Remove(k);
            }
        }

        private static void LightRow()
        {
            Light l = _suit;
            double surface = double.NaN;
            try
            {
                Transform t = l.transform;
                RaycastHit hit;
                Vector3 o = t.position + t.forward * 0.3f;
                if (Physics.Raycast(o, t.forward, out hit, Math.Max(1f, l.range), ~0, QueryTriggerInteraction.Ignore)) surface = hit.distance + 0.3;
            }
            catch (Exception) { surface = double.NaN; }
            JsonOut j = S5Rows.Begin("s13.light");
            j.Str("kind", "characterSpot").Bool("shadows", _suitShadows).Num("nearPlane", (double)_suitNear).Num("rangeFactor", (double)_suitK).Num("intensityFactor", (double)_suitI)
             .Num("falloff", (double)_suitFalloff).Num("range", (double)l.range).Num("intensity", (double)l.intensity).Num("shadowBias", (double)l.shadowBias)
             .Num("shadowNormalBias", (double)l.shadowNormalBias).Num("surfaceM", double.IsNaN(surface) ? -1.0 : surface)
             .Num("shadowLen10cm", double.IsNaN(surface) ? -1.0 : S13LightRules.WorstShadowLength(_suitNear, surface))
             .Num("magnification", double.IsNaN(surface) || !(_suitNear > 0) ? -1.0 : surface / _suitNear)
             .Bool("fitOn", LightFitOn).Bool("mutant", DevMutants.IsOn(S13SnMutants.LightUnity)).Num("lightsTouched", LightsTouched);
            S5Rows.End(j);
        }

        private static void GhostRow()
        {
            JsonOut j = S5Rows.Begin("s13.ghost");
            j.Num("textured", (long)_ghostTextured).Num("flat", (long)_ghostFlat).Bool("inside", _inside).Bool("texturedOn", GhostTextured).Num("slots", (long)GhostSlots.Count)
             .Str("source", _tex.ToString().ToLowerInvariant()).Str("shader", _texShader).Str("probe", _probeLine)
             .Num("materialsMade", GhostMaterialsMade).Bool("mutant", DevMutants.IsOn(S13SnMutants.GhostFlat));
            GhostView gv = GhostView.Current;
            if (gv != null) j.Str("view", gv.Describe());
            S5Rows.End(j);
        }

        private static string ActiveMutants()
        {
            var on = new List<string>();
            foreach (string n in S13SnMutants.All) if (DevMutants.IsOn(n)) on.Add(n);
            return string.Join(",", on.ToArray());
        }

        /// <summary>
 /// <c>de.s13 {status} | {ghost: textured|flat} | {lightFit: on|off} | {blockFit: on|off} | {nearPlane: m|default} |
 /// {sounds: on|off}</c> (dev builds; chat <c>/de sn s13 ghost flat</c> etc.). S5Host hook.
 /// </summary>
        public static bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req == null || req.Cmd != Verb) return false;
            try
            {
                if (!BuildFlavor.Dev) { reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", UserCommandRules.RefusalText); return true; }
                Dictionary<string, object> a = S3Args.ToDict(req.Args);
                object v;
                List<string> words = null;
                if (a.TryGetValue("argv", out v) && v is System.Collections.IList)
                {
                    words = new List<string>();
                    foreach (object o in (System.Collections.IList)v) words.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
                    if (words.Count >= 2) a[words[0]] = words[1];
                    else if (words.Count == 1) a["action"] = words[0];
                }
                string error = null;
                if (a.TryGetValue("ghost", out v)) { string s = Lower(v); if (s == "textured" || s == "flat") GhostTextured = s == "textured"; else error = "ghost: textured|flat"; }
                if (a.TryGetValue("lightFit", out v)) { string s = Lower(v); if (s == "on" || s == "off") LightFitOn = s == "on"; else error = "lightFit: on|off"; }
                if (a.TryGetValue("blockFit", out v)) { string s = Lower(v); if (s == "on" || s == "off") LightFitBlockSpots = s == "on"; else error = "blockFit: on|off"; }
                if (a.TryGetValue("sounds", out v)) { string s = Lower(v); if (s == "on" || s == "off") SoundsOn = s == "on"; else error = "sounds: on|off"; }
                if (a.TryGetValue("nearPlane", out v))
                {
                    string s = Lower(v);
                    double d;
                    if (s == "default") CharacterNearPlane = float.NaN;
                    else if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 0.1 && d <= 10) CharacterNearPlane = (float)d;
                    else error = "nearPlane: 0.1..10 m | default";
                }
                if (error != null) { reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", error); return true; }
                _nextCharRowMs = 0;
                reply.Bool("ok", true).Str("cmd", req.Cmd).Str("ghost", GhostTextured ? "textured" : "flat").Bool("lightFit", LightFitOn).Bool("blockFit", LightFitBlockSpots)
                     .Num("nearPlane", float.IsNaN(CharacterNearPlane) ? S13Conventions.Light.NearPlaneCharacterSpotMm / 1000.0 : CharacterNearPlane).Bool("sounds", SoundsOn)
                     .Str("counters", Counters.Describe()).Str("mutants", ActiveMutants())
                     .Str("line", "DE.S13 ghost=" + (GhostTextured ? "textured" : "flat") + " lightFit=" + (LightFitOn ? "on" : "off") + " nearPlane=" +
                                  (float.IsNaN(CharacterNearPlane) ? "1.0 (default)" : CharacterNearPlane.ToString("0.##", CultureInfo.InvariantCulture)) + " sounds=" + (SoundsOn ? "on" : "off") + " | " + Counters.Describe());
                return true;
            }
            catch (Exception e)
            {
                reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", e.GetType().Name + ": " + e.Message);
                return true;
            }
        }

        private static string Lower(object v) { return (Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").Trim().ToLowerInvariant(); }
    }
}
