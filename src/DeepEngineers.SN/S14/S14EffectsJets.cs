using System;
using System.Globalization;
using DeepEngineers.Wire;
using UnityEngine;
using EventType = DeepEngineers.Wire.S7Conventions.EventType;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S14 point 5 and 4b inside the S7 effects player (a part of <see cref="S7Effects"/>; ROADMAP §14 "### S14" point 5;
 /// CONTRACT §5.4 "S14 conventions", formats jets.above, jets.length, jets.old, jets.visible).
 /// hydrogen thrusters look impressive under water but sad above";: "when do the thrusters come in?"): below the
 /// band (1 km) Subnautica's picture shows SE's ships above the water too, and their thruster flames are only what this
 /// plugin draws.

 /// on each above-water flame, and round 4 turned
 /// scene-above's yellow puffs off for it. That jet is a water effect: its two renderers draw with UWE/Particles/UBER
 /// underwater materials (<c>waterBlob_01_sheet_premul_underwaterClipCloseLit</c>, <c>bubble_01_sheet_premul_underwaterCameraVertexLit</c>,
 /// named by the plugin's own 14:50 log line), so above the sea nothing showed (his 16:49 session: 338 s14.jets rows, a jet on
 /// every above-water flame, up to 850 live particles, nothing seen; its template's own 21 m length also kept the length
 /// factor at its floor 1). Now each above-water flame holds a flame cone of this plugin (<see cref="S14FlameTemplate"/>:
 /// a local-space cone, S7p's flame rules for its length, speed, size and rate at the jet's strength, see
 /// <see cref="S14Rules.JetLengthM"/>), drawn with S7p's above-water additive material (<c>S7pFxKit.SyntheticMaterial</c>:
 /// Unity's Legacy Shaders/Particles/Additive on a soft dot, the one scene-above's puffs proved above the water) in SE's
 /// flame colour. Without that material the jets are off and the kit's puffs stay (never nothing). <c>de.s14 jets off</c> or
 /// Rows <c>s14.jets</c> (1 Hz while an above-water thruster burns): the jets shown
 /// samples (intensity, radius, the cone's length, the flame's length scale), the live particles, the jets a camera renders.
 /// </summary>
    internal sealed partial class S7Effects
    {
        public const int MaxS14Jets = 64;
        private S7FxPool _s14Jets;
        private long _s14JetsShown, _s14JetsNone, _s14JetsTaken;
        private float _s14NextJetRowS, _s14LastEngineS = -1f;
        private static string _s14JetStatus = "not built";
        // jets.old
        private static float _s14ReadyUntil = -1f;
        private static long _s14KitSkipped, _s14OldFlames, _s14RowKitSkipped, _s14RowOldFlames;
        // round 5: the flame's last written values (rows), the material and the Prawn's own looks (information)
        private float _s14LastLen, _s14LastSpeed, _s14LastDia, _s14LastRate;
        private string _s14JetShader = "none", _s14PrawnLooks = "not read";

        /// <summary>
 /// jets.old (S7pScene hook, scene-above's per-frame thruster sustain): true when the jets draw the above-water thrusters
 /// (on, their material found, S14.M6 off, the effects player ticking), so the kit skips its puffs; counts kitSkipped / oldFlames.
 /// </summary>
        internal static bool S14KitThrusterOff()
        {
            bool off = S14Rules.KitThrusterOff(S14Sn.Enabled, S14Sn.JetsOn, DevMutants.IsOn(S14SnMutants.NoJets), Time.unscaledTime <= _s14ReadyUntil);
            if (off) _s14KitSkipped++;
            else _s14OldFlames++;
            return off;
        }

        internal static string S14JetStatus() { return _s14JetStatus; }

        /// <summary>BuildPools (S14 hook): the above-water flame pool (round 5: our own cone; the Prawn's jet is read for the log only).</summary>
        private void BuildS14Pools(Transform home)
        {
            GameObject prawn = _lib.Template(FxSource.ExosuitJets);
            _s14PrawnLooks = prawn != null ? S14Renderers(prawn) : "no Prawn template (" + _lib.State(FxSource.ExosuitJets) + ")";
            GameObject template = S14FlameTemplate(home);
            _s14Jets = Pool("s14.jets", template, home, MaxS14Jets);
            _s14JetStatus = _s14Jets.Valid ? "flame cone (" + _s14JetShader + ")" : "no above-water material (" + _s14JetShader + "): scene-above's puffs stay";
            DeLog.Info("DE.S14 jets above water: " + _s14JetStatus + "; cone " + S14Conventions.Jets.ConeAngleDeg + " deg, S7p flame length x" +
                       (S14Conventions.Jets.S7pFlameLengthPermille / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " x radius, lifetime " +
                       S14Conventions.Jets.S7pLifetimeMs + " ms, idle floor " + S14Conventions.Jets.IdleFloorPermille + " permille; the Prawn's jet not used above water (its renderers: " +
                       _s14PrawnLooks + ")");
        }

        /// <summary>
 /// jets.above: the inactive template of the held flame cone (Local space, cone, size shrinking to the tip, a fade-in and
 /// fade-out over its life, Billboard with S7p's above-water additive material); null without that material.
 /// </summary>
        private GameObject S14FlameTemplate(Transform home)
        {
            try
            {
                string shader;
                Material m = S7pFxKit.SyntheticMaterial(true, out shader);
                _s14JetShader = shader ?? "none";
                if (m == null) return null;
                var go = new GameObject("DE.S14.JetFlame");
                go.transform.SetParent(home, false);
                go.SetActive(false);
                var ps = go.AddComponent<ParticleSystem>();
                ParticleSystem.MainModule main = ps.main;
                main.playOnAwake = false;
                main.loop = true;
                main.duration = 1f;
                main.simulationSpace = ParticleSystemSimulationSpace.Local;
                main.scalingMode = ParticleSystemScalingMode.Shape;
                main.startLifetime = S14Conventions.Jets.S7pLifetimeMs / 1000f;
                main.startSpeed = 10f;
                main.startSize = 1f;
                main.startColor = Color.white;
                main.maxParticles = S14Conventions.Jets.MaxParticles;
                main.gravityModifier = 0f;
                ParticleSystem.EmissionModule em = ps.emission;
                em.enabled = true;
                em.rateOverTime = 0f;
                ParticleSystem.ShapeModule sh = ps.shape;
                sh.enabled = true;
                sh.shapeType = ParticleSystemShapeType.Cone;
                sh.angle = S14Conventions.Jets.ConeAngleDeg;
                sh.radius = 0.5f;
                sh.radiusThickness = 1f;
                sh.arc = 360f;
                ParticleSystem.SizeOverLifetimeModule sz = ps.sizeOverLifetime;
                sz.enabled = true;
                sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, S14Conventions.Jets.TipSizePermille / 1000f));
                ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
                col.enabled = true;
                var g = new Gradient();
                float fadeIn = S14Conventions.Jets.FadeInPermille / 1000f;
                g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                          new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, fadeIn), new GradientAlphaKey(0.6f, 0.5f), new GradientAlphaKey(0f, 1f) });
                col.color = new ParticleSystem.MinMaxGradient(g);
                var r = go.GetComponent<ParticleSystemRenderer>() ?? go.AddComponent<ParticleSystemRenderer>();
                r.renderMode = ParticleSystemRenderMode.Billboard;
                r.sharedMaterial = m;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                return go;
            }
            catch (Exception e)
            {
                _s14JetShader = "error " + e.GetType().Name;
                DeLog.Warn("DE.S14 jets: the flame template failed (" + e.GetType().Name + ": " + e.Message + "): scene-above's puffs stay");
                return null;
            }
        }

        /// <summary>Each renderer of a template (name, mode, material, shader) for the log.</summary>
        private static string S14Renderers(GameObject template)
        {
            var sb = new System.Text.StringBuilder(256);
            try
            {
                foreach (ParticleSystemRenderer r in template.GetComponentsInChildren<ParticleSystemRenderer>(true))
                {
                    if (sb.Length > 0) sb.Append(" | ");
                    Material m = r.sharedMaterial;
                    sb.Append(r.name).Append(' ').Append(r.renderMode).Append(' ').Append(m != null ? m.name : "no material").Append(" / ")
                      .Append(m != null && m.shader != null ? m.shader.name : "no shader").Append(r.enabled ? "" : " (disabled)");
                }
            }
            catch (Exception) { sb.Append("unreadable"); }
            return sb.Length > 0 ? sb.ToString() : "none";
        }

        private static bool S14JetsWanted()
        {
            return S14Sn.Enabled && S14Sn.JetsOn && !DevMutants.IsOn(S14SnMutants.NoJets);
        }

        /// <summary>Visualize (S14 hook), a thruster sustain above the water: the held flame cone along the flame, SE's length and colour.</summary>
        private void S14Jet(Sustain s, float now, bool row)
        {
            if (!S14JetsWanted() || _s14Jets == null || !_s14Jets.Valid || !(s.Intensity > 0f))
            {
                S14JetOff(s, now);
                return;
            }
            bool fresh = false;
            if (s.Jet == null || s.Jet.Go == null)
            {
                s.Jet = _s14Jets.Take(now, 0f, ref _budget);
                if (s.Jet == null) { _s14JetsNone++; return; }
                s.Jet.Owner = s;
                fresh = true;
                _s14JetsTaken++;
            }
            if (!fresh && !row) return;
            Place(s.Jet, s.Attached ? s.Anchor : null, s.Pos, s.Dir, WorldOf(s), 1f);
            float knob = S14Sn.JetKnob;
            float len = (float)S14Rules.JetLengthM(s.Intensity, s.Size, S7pFxCodec.LengthScaleOf(s.Extra), knob);
            float speed = (float)S14Rules.JetSpeed(len);
            float dia = (float)S14Rules.JetDiameterM(s.Size, knob);
            float rate = (float)S14Rules.JetRate(s.Intensity, s.Size) * knob;   //: the knob also feeds the particle rate (additive overlap = brightness)
            Color c = ThrusterColour(s);
            // 15, scales SE's colour
 // to 15 % on an additive material: invisible by day); the cone is the flame itself, SE's colour at full strength
            if (s.Jet.Root != null)
            {
                ParticleSystem.MainModule main = s.Jet.Root.main;
                main.startSpeed = speed;
                main.startSize = dia;
                main.startColor = new Color(c.r, c.g, c.b, 1f);
                ParticleSystem.ShapeModule shape = s.Jet.Root.shape;
                shape.radius = (float)S14Rules.JetShapeRadiusM(s.Size);
            }
            SetRate(s.Jet, rate);
            _s14LastLen = len;
            _s14LastSpeed = speed;
            _s14LastDia = dia;
            _s14LastRate = rate;
            if (fresh) ActivatePlay(s.Jet);
        }

        /// <summary>The jet goes (under water, the thrust ended, the sustain ended).</summary>
        private static void S14JetOff(Sustain s, float now)
        {
            if (s.Jet == null) return;
            ReleaseHeld(s.Jet, now, 0.5f);
            s.Jet = null;
        }

        /// <summary>Tick (S14 hook, after the engines): the listener row, the s14.jets row.</summary>
        private void S14Frame(float now)
        {
            S14Sn.Frame(Time.unscaledTime);
            if (_s14Jets != null && _s14Jets.Valid) _s14ReadyUntil = Time.unscaledTime + 0.5f; // jets.old: the kit may leave thrusters to the jets
            if (now < _s14NextJetRowS) return;
            _s14NextJetRowS = now + S14Conventions.Rows.PeriodMs / 1000f;
            int above = 0, shown = 0, n = 0, visible = 0;
            long particles = 0;
            var samples = new System.Text.StringBuilder(160);
            foreach (Sustain s in _sustains.Values)
            {
                if (s.Type != EventType.Thruster || (s.Flags & S7Conventions.EventFlags.Underwater) != 0 || !(s.Intensity > 0f)) continue;
                above++;
                if (s.Jet == null || s.Jet.Go == null || !s.Jet.Go.activeSelf) continue;
                shown++;
                bool seen = false;
                if (s.Jet.All != null)
                    foreach (ParticleSystem p in s.Jet.All)
                    {
                        if (p == null) continue;
                        particles += p.particleCount;
                        Renderer rr = p.GetComponent<Renderer>();
                        if (rr != null && rr.enabled && rr.isVisible) seen = true;
                    }
                if (seen) visible++;
                if (n < 4)
                {
                    // round 5: intensity, SE's flame radius, the cone's written length, the flame's length scale (the twin's inputs)
                    float written = s.Jet.Root != null ? s.Jet.Root.main.startSpeedMultiplier * S14Conventions.Jets.S7pLifetimeMs / 1000f : 0f;
                    if (n > 0) samples.Append(';');
                    samples.Append(s.Intensity.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                           .Append(s.Size.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                           .Append(written.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                           .Append(S7pFxCodec.LengthScaleOf(s.Extra).ToString("0.###", CultureInfo.InvariantCulture));
                    n++;
                }
            }
            long old = _s14OldFlames - _s14RowOldFlames, skipped = _s14KitSkipped - _s14RowKitSkipped;
            _s14RowOldFlames = _s14OldFlames;
            _s14RowKitSkipped = _s14KitSkipped;
            if (above == 0 && old == 0) return;
            _s14JetsShown += shown;
            JsonOut j = S5Rows.Begin("s14.jets");
            j.Num("aboveThrusters", (long)above).Num("jets", (long)shown).Str("source", _s14JetStatus).Str("samples", samples.ToString())
             .Str("material", _s14JetShader).Num("knob", (double)S14Sn.JetKnob).Num("noClone", _s14JetsNone).Num("taken", _s14JetsTaken).Bool("on", S14Sn.JetsOn)
             .Num("oldFlames", old).Num("kitSkipped", skipped).Num("particles", particles).Num("visible", (long)visible)
             .Num("flameLenM", (double)_s14LastLen).Num("speed", (double)_s14LastSpeed).Num("diameterM", (double)_s14LastDia).Num("rate", (double)_s14LastRate)
             .Bool("mutantS14M6", DevMutants.IsOn(S14SnMutants.NoJets));
            S5Rows.End(j);
        }

        /// <summary>UpdateEngines (S14 hook): the frame's step in ms on the unscaled clock (bounded).</summary>
        private double S14EngineDtMs(float now)
        {
            double dt = _s14LastEngineS < 0f || now < _s14LastEngineS ? 16.7 : Math.Min(250.0, (now - _s14LastEngineS) * 1000.0);
            _s14LastEngineS = now;
            return dt;
        }
    }
}
