using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S15 part C, SE's planets in Subnautica's sky (ROADMAP §14 "### S15 addenda", 2; CONTRACT §5.4 "S15 conventions", formats
 /// c6.s15frame, c6.s15list, moons.hide, sky.disc, sky.dir, sky.size). Main thread (S15Sn.Frame). Every game member VERIFIED
 /// in the decompiled build:
 /// <list type="bullet">
 /// <item><b>Subnautica's two sky bodies</b> are drawn by uSky's sky shader from <c>uSkyManager</c>'s fields: the planet by
 /// <c>planetRadius</c> at <c>PlanetPos</c> (<c>planetZenith</c>, <c>planetOrbitSpeed</c>,
 /// <c>planetDistance</c>), sent every frame by <c>SetVaryingMaterialProperties</c> (<c>_PlanetRadius</c>), its coronas
 /// <c>planetInnerCorona</c>/<c>planetOuterCorona</c> (/); the moon by <c>MoonTexture</c> as <c>_MoonSampler</c>
 /// and its coronas <c>MoonInnerCorona</c>/<c>MoonOuterCorona</c> (/), sent by <c>SetConstantMaterialProperties</c>
 /// to the skybox (<c>SkyboxMaterial</c>), the sky map
 /// the fog and the water surface
 /// only at their Start/OnEnable.
 /// Hidden (format moons.hide): <c>planetRadius</c> 0.001 m, the planet held opposite the sun on its great circle
 /// (<c>planetOrbitSpeed</c> 0, <c>planetZenith</c> from <c>SunDir</c>) so <c>Eclipse</c> stays 0, the coronas'
 /// rgb 0 (alpha kept: their w is 400 / a), <c>MoonTexture</c> a transparent 4 x 4 of ours, and the constant properties sent
 /// again to the four materials; captured before the first write, restored exactly when the sky goes off. Measured back from
 /// the skybox material each row
 /// </item>
 /// <item><b>The bodies</b>: SE's list and frame in C6's region space (Wire <see cref="S15SkyWire"/>); per body except the
 /// bound planet a camera-facing disc at 0.9 of the main camera's far plane, radius D tan(alpha), its vertex colours the flat palette
 /// lit by uSky's <c>SunDir</c> as a sphere (<see cref="S15Sky.Light"/>), the limb toward the palette's limb colour for a body
 /// with an atmosphere; shown while the camera is above the sea and the frame is fresh (3 s).</item>
 /// <item><b>Rows</b>: <c>s15.sky</c> on every presented SE tick that is a multiple of 60 (each body's direction in the camera
 /// frame from the camera's and the disc's transforms, and its angular radius from the disc's scale), <c>s15.skylist</c> on
 /// a new list, <c>s15.moons</c> every 2 s.</item>
 /// </list>
 /// </summary>
    internal static unsafe class S15SnSky
    {
        public static bool On = true;
        public static int Shown;
        public static bool MoonsHidden;
        public static long Frames, StaleFrames, ListReads, Rows, Errors;

        private sealed class Disc
        {
            public GameObject Go;
            public Mesh Mesh;
            public Transform T;
            public Color32[] Colours;
            public string Name;
            public int Palette;
            public bool Atmosphere;
            public double AngRadius, DistM;
            public Vec3 Dir;
            public bool Visible;
            public int RecolourIn;
        }

        private static readonly List<S15Body> Bodies = new List<S15Body>();
        private static readonly List<Disc> Discs = new List<Disc>();
        private static S15SkyFrame _frame;
        private static uint _frameSeq, _listSeqHave = uint.MaxValue;
        private static float _frameChangedS = -1f;
        /// <summary> the seq found first is a baseline (a frame left by an earlier SE session is never fresh).</summary>
        private static bool _seqBased;
        private static ulong _lastRowTick;
        private static float _nextMoonRowS, _nextAssertS;
        private static Material _mat;
        private static bool _matTried;
        private static Vector2[] _unit;
        private static int[] _tris;

        // the hidden-moon state
        private static uSkyManager _sky;
        private static bool _captured;
        private static float _oRadius, _oZenith, _oOrbit;
        private static Texture2D _oMoonTex, _blank;
        private static Color _oMoonIn, _oMoonOut, _oPlanetIn, _oPlanetOut;
        private static AccessTools.FieldRef<WaterSurface, Material> _waterMat;
        private static bool _waterTried;

        /// <summary>Called by S15Sn.Frame every frame (in or out of the world). Never throws.</summary>
        public static void Frame(bool inWorld, bool seAlive, byte* sectionBase, ulong presentedSeTick, uint worldEpoch = 0, bool epochValid = false)
        {
            try { FrameCore(inWorld, seAlive, sectionBase, presentedSeTick, worldEpoch, epochValid); }
            catch (Exception e)
            {
                Errors++;
                DeLog.Warn("DE.Error S15 sky " + e.GetType().Name + ": " + e.Message);
                try { Teardown("error"); } catch (Exception) { }
                On = false;
                DeLog.Warn("DE.S15 sky OFF after the error (Subnautica's own sky restored); /de sn s15 sky on to retry");
            }
        }

        private static void FrameCore(bool inWorld, bool seAlive, byte* sectionBase, ulong presentedSeTick, uint worldEpoch, bool epochValid)
        {
            float now = Time.unscaledTime;
            if (!inWorld || !seAlive || sectionBase == null || !On)
            {
                if (!inWorld || !seAlive || sectionBase == null) { _seqBased = false; _frameChangedS = -1f; }
                Teardown(!inWorld ? "world gone" : !seAlive ? "SE gone" : !On ? "sky off" : "detached");
                return;
            }
            Frames++;
            byte* c6 = Layout.RegionPtr(sectionBase, RegionId.C6SeTime);
            S15SkyFrame f;
            uint seq;
            if (S15SkyWire.TryReadFrame(c6, out f, out seq) == SeqlockReadStatus.Fresh)
            {
                if (!_seqBased) { _seqBased = true; _frameSeq = seq; }
                else if (seq != _frameSeq) { _frameSeq = seq; _frameChangedS = now; }
                _frame = f;
            }
            // fresh: changed within 3 s since the baseline, valid, and of this world
            bool fresh = _frameChangedS >= 0f && now - _frameChangedS <= 3f && _frame.Valid && (!epochValid || _frame.WorldEpoch == worldEpoch);
            if (!fresh)
            {
                StaleFrames++;
                Teardown(_frame.Valid ? "frame stale" : "SE's sky off");
                return;
            }
            if (_frame.ListSeq != _listSeqHave)
            {
                uint ls, ep, lseq;
                if (S15SkyWire.TryReadList(c6, Bodies, out ls, out ep, out lseq) == SeqlockReadStatus.Fresh && ls == _frame.ListSeq)
                {
                    _listSeqHave = ls;
                    ListReads++;
                    Rebuild();
                    JsonOut j = S5Rows.Begin("s15.skylist");
                    j.Num("listSeq", (long)ls).Num("count", (long)Bodies.Count).Arr("names");
                    foreach (S15Body b in Bodies) j.Str(null, b.Name + (b.Bound ? " (bound)" : ""));
                    j.EndArr();
                    S5Rows.End(j);
                    DeLog.Info("DE.S15 sky list " + ls + ": " + Bodies.Count + " bodies, " + Discs.Count + " disc(s)");
                }
            }
            HideMoons(now);
            Camera cam = MainCamera.camera;
            if (cam == null) return;
            Vector3 cp = cam.transform.position;
            double d = S15SnSkyRules.DiscDistance(cam.farClipPlane);
            bool above = cp.y > 0f;
            Vector3 sun = SunDir();
            bool day = sun.y > 0f;
            bool sizeMutant = DevMutants.IsOn(S15SnMutants.SizeDiameter);
            bool unmapped = DevMutants.IsOn(S15SnMutants.Unmapped);
            Shown = 0;
            for (int i = 0; i < Discs.Count; i++)
            {
                Disc disc = Discs[i];
                S15SnBodyView v = S15SnSkyRules.View(ref _frame, Bodies[i], i, cp.y, sizeMutant, unmapped);
                disc.Visible = v.Visible && above;
                if (disc.Go == null) continue;
                if (!disc.Visible) { if (disc.Go.activeSelf) disc.Go.SetActive(false); continue; }
                var dir = new Vector3((float)v.Place.X, (float)v.Place.Y, (float)v.Place.Z);   // = the mapped direction unless S15.M3
                disc.T.position = cp + dir * (float)d;
                disc.T.rotation = Quaternion.LookRotation(dir, Mathf.Abs(dir.y) > 0.99f ? Vector3.forward : Vector3.up);
                float r = (float)S15SnSkyRules.DiscRadius(d, v.AngRadius);
                disc.T.localScale = new Vector3(r, r, r);
                disc.AngRadius = v.AngRadius;
                disc.DistM = v.DistM;
                disc.Dir = v.Dir;
                if (!disc.Go.activeSelf) { disc.Go.SetActive(true); disc.RecolourIn = 0; }
                if (--disc.RecolourIn <= 0) { Recolour(disc, dir, sun, day); disc.RecolourIn = 10; }
                Shown++;
            }
            if (presentedSeTick != 0 && presentedSeTick / (ulong)S15Conventions.Sky.RowTicks != _lastRowTick / (ulong)S15Conventions.Sky.RowTicks)
            {
                _lastRowTick = presentedSeTick;
                SkyRow(cam, presentedSeTick, d, sun);
            }
            if (now >= _nextMoonRowS) { _nextMoonRowS = now + 2f; MoonRow(); }
        }

        private static Vector3 SunDir()
        {
            uSkyManager sky = uSkyManager.main;
            return sky != null ? sky.SunDir.normalized : Vector3.up;
        }

        // ------------------------------------------------------------------------------------------------ the discs

        private static void Rebuild()
        {
            DestroyDiscs();
            Material m = Mat();
            for (int i = 0; i < Bodies.Count; i++)
            {
                S15Body b = Bodies[i];
                var disc = new Disc { Name = b.Name, Palette = b.Palette, Atmosphere = b.HasAtmosphere };
                if (!b.Bound && m != null)
                {
                    disc.Go = new GameObject("DE.S15.Sky." + b.Name);
                    UnityEngine.Object.DontDestroyOnLoad(disc.Go);
                    disc.T = disc.Go.transform;
                    disc.Mesh = DiscMesh(out disc.Colours);
                    disc.Go.AddComponent<MeshFilter>().sharedMesh = disc.Mesh;
                    var mr = disc.Go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = m;
                    mr.shadowCastingMode = ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                    disc.Go.SetActive(false);
                }
                Discs.Add(disc);
            }
        }

        private static Mesh DiscMesh(out Color32[] colours)
        {
            int seg = S15Conventions.Sky.DiscSegments, rings = S15Conventions.Sky.DiscRings;
            if (_unit == null)
            {
                var pts = new List<Vector2> { Vector2.zero };
                for (int r = 1; r <= rings; r++)
                {
                    float rr = r == rings ? 1f : (float)Math.Sin(r / (double)rings * Math.PI / 2); // denser toward the limb
                    for (int s = 0; s < seg; s++)
                    {
                        double a = s * 2 * Math.PI / seg;
                        pts.Add(new Vector2((float)(rr * Math.Cos(a)), (float)(rr * Math.Sin(a))));
                    }
                }
                _unit = pts.ToArray();
                var tris = new List<int>();
                for (int s = 0; s < seg; s++) { tris.Add(0); tris.Add(1 + (s + 1) % seg); tris.Add(1 + s); }
                for (int r = 1; r < rings; r++)
                {
                    int a0 = 1 + (r - 1) * seg, b0 = 1 + r * seg;
                    for (int s = 0; s < seg; s++)
                    {
                        int s1 = (s + 1) % seg;
                        tris.Add(a0 + s); tris.Add(a0 + s1); tris.Add(b0 + s);
                        tris.Add(a0 + s1); tris.Add(b0 + s1); tris.Add(b0 + s);
                    }
                }
                _tris = tris.ToArray();
            }
            var verts = new Vector3[_unit.Length];
            for (int i = 0; i < _unit.Length; i++) verts[i] = new Vector3(_unit[i].x, _unit[i].y, 0f);
            colours = new Color32[_unit.Length];
            var m = new Mesh { name = "DE.S15.SkyDisc" };
            m.MarkDynamic();
            m.vertices = verts;
            m.triangles = _tris;
            m.colors32 = colours;
            m.bounds = new Bounds(Vector3.zero, new Vector3(2f, 2f, 2f));
            return m;
        }

        /// <summary>format sky.disc: the palette lit as a sphere by the sun, the limb toward the limb colour.</summary>
        private static void Recolour(Disc disc, Vector3 dir, Vector3 sun, bool day)
        {
            S15Conventions.PaletteRow p = S15Palette.Row(disc.Palette);
            Vector3 right = disc.T.right, up = disc.T.up;
            var tb = new Vec3(dir.x, dir.y, dir.z);
            var rv = new Vec3(right.x, right.y, right.z);
            var uv = new Vec3(up.x, up.y, up.z);
            var sv = new Vec3(sun.x, sun.y, sun.z);
            byte alpha = (byte)Math.Round(255.0 * (day ? S15Conventions.Sky.DayAlphaPermille : S15Conventions.Sky.NightAlphaPermille) / 1000.0);
            for (int i = 0; i < _unit.Length; i++)
            {
                double a = _unit[i].x, b = _unit[i].y;
                double light = S15Sky.Light(a, b, tb, rv, uv, sv);
                double lw = S15Sky.LimbWeight(Math.Sqrt(a * a + b * b));
                disc.Colours[i] = new Color32(S15SnSkyRules.Channel(p.Rgb.R, p.Limb.R, light, lw, disc.Atmosphere), S15SnSkyRules.Channel(p.Rgb.G, p.Limb.G, light, lw, disc.Atmosphere),
                                              S15SnSkyRules.Channel(p.Rgb.B, p.Limb.B, light, lw, disc.Atmosphere), alpha);
            }
            disc.Mesh.colors32 = disc.Colours;
        }

        private static Material Mat()
        {
            if (_matTried) return _mat;
            _matTried = true;
            Shader s = null;
            bool colored = false;
            try { s = Shader.Find("Sprites/Default"); } catch (Exception) { }
            if (s != null && !s.isSupported) s = null;
            if (s == null)
            {
                try { s = Shader.Find("Hidden/Internal-Colored"); } catch (Exception) { }
                if (s != null && !s.isSupported) s = null;
                colored = s != null;
            }
            if (s == null) { DeLog.Warn("DE.S15 MISSING Sprites/Default and Hidden/Internal-Colored: no sky discs (Subnautica's moons stay hidden)"); return null; }
            _mat = new Material(s) { name = "DE.S15 sky disc (" + s.name + ")" };
            if (colored)
            {
                _mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                _mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                _mat.SetInt("_ZWrite", 0);
                _mat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
                _mat.SetInt("_Cull", (int)CullMode.Off);
            }
            if (_mat.HasProperty("_Color")) _mat.SetColor("_Color", Color.white);
            _mat.renderQueue = S15Conventions.Sky.RenderQueue;
            DeLog.Info("DE.S15 sky disc material " + s.name + " queue " + _mat.renderQueue);
            return _mat;
        }

        private static void DestroyDiscs()
        {
            foreach (Disc d in Discs)
            {
                if (d.Go != null) UnityEngine.Object.Destroy(d.Go);
                if (d.Mesh != null) UnityEngine.Object.Destroy(d.Mesh);
            }
            Discs.Clear();
            Shown = 0;
        }

        // ------------------------------------------------------------------------------------------------ Subnautica's moons

        private static Material[] SkyMaterials(uSkyManager sky)
        {
            var list = new List<Material>(4);
            if (sky.SkyboxMaterial != null) list.Add(sky.SkyboxMaterial);
            uSky.uSkymapRenderer map = sky.GetComponent<uSky.uSkymapRenderer>();
            if (map != null && map.m_skymapMaterial != null) list.Add(map.m_skymapMaterial);
            uSky.uSkyFog fog = sky.GetComponent<uSky.uSkyFog>();
            if (fog != null && fog.FogMaterial != null) list.Add(fog.FogMaterial);
            WaterSurface ws = WaterSurface.Get();
            if (ws != null)
            {
                if (!_waterTried)
                {
                    _waterTried = true;
                    try { _waterMat = AccessTools.FieldRefAccess<WaterSurface, Material>("surfaceMaterial"); }
                    catch (Exception) { DeLog.Warn("DE.S15 MISSING WaterSurface.surfaceMaterial: the moon may stay in the sea's reflection"); }
                }
                Material wm = _waterMat != null ? _waterMat(ws) : null;
                if (wm != null) list.Add(wm);
            }
            return list.ToArray();
        }

        private static void HideMoons(float now)
        {
            uSkyManager sky = uSkyManager.main;
            if (sky == null) return;
            if (!ReferenceEquals(sky, _sky)) { _sky = sky; _captured = false; }
            if (!_captured)
            {
                _oRadius = sky.planetRadius; _oZenith = sky.planetZenith; _oOrbit = sky.planetOrbitSpeed;
                _oMoonTex = sky.MoonTexture; _oMoonIn = sky.MoonInnerCorona; _oMoonOut = sky.MoonOuterCorona;
                _oPlanetIn = sky.planetInnerCorona; _oPlanetOut = sky.planetOuterCorona;
                _captured = true;
                _nextAssertS = 0f;
                DeLog.Info("DE.S15 Subnautica's sky bodies captured: planetRadius " + _oRadius.ToString("0.###", CultureInfo.InvariantCulture) + " zenith " +
                           _oZenith.ToString("0.#", CultureInfo.InvariantCulture) + " orbit " + _oOrbit.ToString("0.###", CultureInfo.InvariantCulture) + " moon " + (_oMoonTex != null ? _oMoonTex.name : "none"));
            }
            // the planet: a radius no ray hits, held opposite the sun on its great circle (no eclipse); every frame (the sun moves)
            sky.planetRadius = S15Conventions.Moons.PlanetRadiusMicro / 1e6f;
            sky.planetOrbitSpeed = 0f;
            Vector3 s = SunDir();
            float h = Mathf.Sqrt(s.x * s.x + s.y * s.y);
            sky.planetZenith = h > 1e-4f ? Mathf.Atan2(-s.x, -s.y) * Mathf.Rad2Deg : 90f;
            if (now < _nextAssertS) return;
            _nextAssertS = now + 2f;
            sky.planetInnerCorona = new Color(0f, 0f, 0f, _oPlanetIn.a);
            sky.planetOuterCorona = new Color(0f, 0f, 0f, _oPlanetOut.a);
            if (!DevMutants.IsOn(S15SnMutants.MoonOn))
            {
                if (_blank == null)
                {
                    int n = S15Conventions.Moons.TextureSize;
                    _blank = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "DE.S15.NoMoon", hideFlags = HideFlags.DontSave };
                    var px = new Color32[n * n];
                    _blank.SetPixels32(px);
                    _blank.Apply(false, false);
                }
                sky.MoonTexture = _blank;
                sky.MoonInnerCorona = new Color(0f, 0f, 0f, _oMoonIn.a);
                sky.MoonOuterCorona = new Color(0f, 0f, 0f, _oMoonOut.a);
            }
            else
            {
                sky.MoonTexture = _oMoonTex;
                sky.MoonInnerCorona = _oMoonIn;
                sky.MoonOuterCorona = _oMoonOut;
            }
            foreach (Material m in SkyMaterials(sky)) sky.SetConstantMaterialProperties(m);
        }

        private static void RestoreMoons(string why)
        {
            uSkyManager sky = _sky;
            if (!_captured || sky == null) { _captured = false; return; }
            _captured = false;
            if (sky == null || !sky) return;
            sky.planetRadius = _oRadius; sky.planetZenith = _oZenith; sky.planetOrbitSpeed = _oOrbit;
            sky.MoonTexture = _oMoonTex; sky.MoonInnerCorona = _oMoonIn; sky.MoonOuterCorona = _oMoonOut;
            sky.planetInnerCorona = _oPlanetIn; sky.planetOuterCorona = _oPlanetOut;
            foreach (Material m in SkyMaterials(sky)) sky.SetConstantMaterialProperties(m);
            DeLog.Info("DE.S15 Subnautica's sky bodies restored (" + why + ")");
        }

        private static void Teardown(string why)
        {
            if (Discs.Count > 0) DestroyDiscs();
            _listSeqHave = uint.MaxValue;
            if (_captured) RestoreMoons(why);
            MoonsHidden = false;
        }

        // ------------------------------------------------------------------------------------------------ rows

 /// <summary>The skybox material as uSky's shader reads it now (format moons.hide; measured, not our flags).</summary>
        private static bool Measure(out float radius, out bool moonTexOurs, out Vector4 moonIn, out Vector4 planetIn)
        {
            radius = float.NaN;
            moonTexOurs = false;
            moonIn = planetIn = Vector4.zero;
            uSkyManager sky = uSkyManager.main;
            Material m = sky != null ? sky.SkyboxMaterial : null;
            if (m == null) return false;
            radius = m.GetFloat(ShaderPropertyID._PlanetRadius);
            planetIn = m.GetVector(ShaderPropertyID._PlanetInnerCorona);
            // the moon from the sky material that samples it: Subnautica's skybox has no _MoonSampler (run 20261024-003024:
 // "Material doesn't have a texture property '_MoonSampler'" every 2 s from this read)
            MoonMaterial = "none";
            foreach (Material mm in SkyMaterials(sky))
            {
                if (mm == null || !mm.HasProperty(ShaderPropertyID._MoonSampler)) continue;
                MoonMaterial = mm.shader != null ? mm.shader.name : mm.name;
                Texture t = mm.GetTexture(ShaderPropertyID._MoonSampler);
                moonTexOurs = t != null && _blank != null && ReferenceEquals(t, _blank);
                if (mm.HasProperty(ShaderPropertyID._MoonInnerCorona)) moonIn = mm.GetVector(ShaderPropertyID._MoonInnerCorona);
                break;
            }
            return true;
        }

        /// <summary>The shader of the sky material that samples the moon ("none": no material draws uSky's moon texture).</summary>
        public static string MoonMaterial = "none";

        private static void MoonRow()
        {
            float radius;
            bool ours;
            Vector4 mi, pi;
            bool have = Measure(out radius, out ours, out mi, out pi);
            bool planetHidden = have && radius <= 0.01f && pi.x == 0f && pi.y == 0f && pi.z == 0f;
            bool moonHidden = have && ours && mi.x == 0f && mi.y == 0f && mi.z == 0f;
            MoonsHidden = planetHidden && moonHidden;
            uSkyManager sky = uSkyManager.main;
            JsonOut j = S5Rows.Begin("s15.moons");
            Vector3 ma = MoonAxis();
            j.Arr("moonAxis").Num(null, (double)ma.x).Num(null, (double)ma.y).Num(null, (double)ma.z).EndArr();
            j.Bool("measured", have).Bool("hidden", MoonsHidden).Bool("planetHidden", planetHidden).Bool("moonHidden", moonHidden)
             .Num("planetRadius", (double)radius).Bool("moonTexOurs", ours).Str("moonMaterial", MoonMaterial).Num("moonCoronaRgb", (double)(mi.x + mi.y + mi.z)).Num("planetCoronaRgb", (double)(pi.x + pi.y + pi.z))
             .Num("planetZenith", sky != null ? (double)sky.planetZenith : double.NaN).Num("discs", (long)Discs.Count).Num("shown", (long)Shown)
             .Bool("mutant", DevMutants.IsOn(S15SnMutants.MoonOn));
            S5Rows.End(j);
        }

        private static void SkyRow(Camera cam, ulong tick, double d, Vector3 sun)
        {
            Rows++;
            Transform ct = cam.transform;
            Vector3 cp = ct.position, right = ct.right, up = ct.up, fwd = ct.forward;
            JsonOut j = S5Rows.Begin("s15.sky");
            j.Num("tick", tick).Num("frameTick", _frame.Tick).Num("listSeq", (long)_frame.ListSeq).Num("camY", (double)cp.y).Num("far", (double)cam.farClipPlane)
             .Num("discD", d).Bool("moonsHidden", MoonsHidden).Bool("mutant", DevMutants.IsOn(S15SnMutants.SizeDiameter));
            j.Arr("sun").Num(null, (double)sun.x).Num(null, (double)sun.y).Num(null, (double)sun.z).EndArr();
            j.Arr("bodies");
            foreach (Disc disc in Discs)
            {
                if (disc.Go == null) continue;
                j.Obj(null).Str("name", disc.Name).Num("idx", (long)Discs.IndexOf(disc)).Bool("shown", disc.Go.activeSelf);
                if (disc.Go.activeSelf)
                {
                    // measured from the transforms Unity renders: the camera's axes and the disc's position and scale
                    Vector3 to = disc.T.position - cp;
                    float dist = to.magnitude;
                    Vector3 n = to / dist;
                    j.Arr("cam").Num(null, (double)Vector3.Dot(n, right)).Num(null, (double)Vector3.Dot(n, up)).Num(null, (double)Vector3.Dot(n, fwd)).EndArr();
                    j.Num("angDeg", Math.Atan(disc.T.localScale.x / dist) * 180.0 / Math.PI);
                }
                j.EndObj();
            }
            j.EndArr();
            S5Rows.End(j);
        }

        /// <summary>The axis of uSky's moon light (its forward, SN world): the launcher aims the night captures along both signs.</summary>
        public static Vector3 MoonAxis()
        {
            uSkyManager sky = uSkyManager.main;
            if (sky == null) return Vector3.zero;
            if (sky.MoonLight != null) return sky.MoonLight.transform.forward;
            // no MoonLight in Subnautica's scene (run 20261024-003024: the axis read (0, 0, 0)): uSky then uses its fixed
 // world-to-local rotation; its forward in the world, both signs are captured
            Quaternion q = new Quaternion(-0.9238795f, 8.817204E-08f, 8.817204E-08f, 0.3826835f);
            return Quaternion.Inverse(q) * Vector3.forward;
        }

        /// <summary>The shown discs: name, SN direction, angular radius in degrees (the launcher aims the sky captures).</summary>
        public static List<object> ShownBodies()
        {
            var l = new List<object>();
            foreach (Disc d in Discs)
                if (d.Go != null && d.Go.activeSelf)
                    l.Add(new Dictionary<string, object> { { "name", d.Name }, { "dir", new List<object> { d.Dir.X, d.Dir.Y, d.Dir.Z } }, { "angDeg", d.AngRadius * 180.0 / Math.PI } });
            return l;
        }

        public static string Status()
        {
            return "sky " + (On ? "on" : "off") + ": " + Discs.Count + " disc(s), " + Shown + " shown, list " + (_listSeqHave == uint.MaxValue ? "none" : _listSeqHave.ToString(CultureInfo.InvariantCulture)) +
                   ", frame tick " + _frame.Tick + ", moons hidden " + MoonsHidden + ", rows " + Rows + ", stale " + StaleFrames;
        }
    }
}
