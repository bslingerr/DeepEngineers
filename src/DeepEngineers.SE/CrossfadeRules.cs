using System;
using System.Globalization;

namespace DeepEngineers.SE
{
    /// <summary>
 /// CONTRACT §5.4 "S7t crossfade"): the
 /// crossfade between SE's picture and Subnautica's at the Approach band under the Site picture policy, the pure part
 /// (no game type, no clock: every input is a value; SE.Tests S7tCompositeTests). Design F (ARCHITECTURE §5.1): SE's
 /// window is DWM-composed over Subnautica's with per-pixel premultiplied alpha (VERIFIED code and synthetic T-D0; the
 /// N0 probe HONOURED), so the blend is SE's own scene, still drawn, with its backbuffer
 /// scaled to <c>alpha·colour, alpha</c> by one full-screen pass before the HUD sprites; the scene skip engages when the
 /// ramp reaches Subnautica and disengages when the ramp back starts.
 /// <list type="bullet">
 /// <item>Progress <c>p</c> ∈ [0, 1]: 0 = SE's picture, 1 = Subnautica's; it moves linearly toward its target at
 /// 1 / <c>ms</c> per millisecond of the render thread's clock (<see cref="Step"/>), so a reversed fade turns back from
 /// where it is.</item>
 /// <item>SE's backbuffer alpha = 1 − <see cref="Ease"/>(p) (smoothstep: no step at either end).</item>
 /// <item>The colour factor that makes the stored value premultiplied (<see cref="ColourFactor"/>): the backbuffer is
 /// <c>R8G8B8A8_UNorm_SRgb</c> (ARCHITECTURE §5.1, VERIFIED <c>MyBackbuffer</c> creates its RTV without a description), so
 /// the blend runs on linear values and is re-encoded: scaling the linear colour by alpha^2.2 scales the stored
 /// (encoded) value by alpha, which is what DWM's premultiplied composition expects.</item>
 /// <item>The update-thread decision (<see cref="Decide"/>): under Site the target is Subnautica while the picture latch is
 /// on and the leave dwell (the altitude exit held 1 s, F15) has not started; the fade back starts at the leave dwell,
 /// so it ends before the latch drops in Space; a latch that drops without the dwell (site, peer or re-streaming loss,
 /// zone) snaps to SE's picture at once (S2's rule: never a stale Subnautica).</item>
 /// </list>
 /// </summary>
    internal static class CrossfadeRules
    {
        public const double DefaultMs = 700.0;
        public const double MinMs = 50.0, MaxMs = 5000.0;
        public const string EnvVar = "DEEPENGINEERS_FADE_MS";
        /// <summary>The sRGB transfer approximated as a power (the blend scales linear values; the encoded value must scale by alpha).</summary>
        public const double SrgbGamma = 2.2;

        /// <summary>
 /// "700" (ms, 50…5000), "off" / "0" (crossfade off: the S7a cut and flash), blank = <see cref="DefaultMs"/>; false
 /// (and the default) for anything else.
 /// </summary>
        public static bool TryParseMs(string text, out double ms)
        {
            ms = DefaultMs;
            if (text == null) return true;
            string t = text.Trim().ToLowerInvariant();
            if (t.Length == 0) return true;
            if (t == "off" || t == "false" || t == "no") { ms = 0.0; return true; }
            if (t.EndsWith("ms", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 2).Trim();
            double v;
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) || double.IsNaN(v) || double.IsInfinity(v)) return false;
            if (v == 0.0) { ms = 0.0; return true; }
            if (v < MinMs || v > MaxMs) return false;
            ms = v;
            return true;
        }

        /// <summary>The ease curve on the progress: smoothstep 3t² − 2t³ (clamped), symmetric, flat at both ends.</summary>
        public static double Ease(double t)
        {
            if (!(t > 0.0)) return 0.0;
            if (t >= 1.0) return 1.0;
            return t * t * (3.0 - 2.0 * t);
        }

        /// <summary>
 /// One step of the progress toward its target: <paramref name="toSn"/> = 1 (Subnautica), else 0; linear at 1 /
 /// <paramref name="ms"/> per millisecond; <paramref name="ms"/> ≤ 0 jumps; a negative or non-finite step is 0.
 /// </summary>
        public static double Step(double p, bool toSn, double dtMs, double ms)
        {
            double target = toSn ? 1.0 : 0.0;
            if (double.IsNaN(p) || double.IsInfinity(p)) p = target;
            if (p < 0.0) p = 0.0; else if (p > 1.0) p = 1.0;
            if (!(ms > 0.0) || double.IsInfinity(ms)) return target;
            if (!(dtMs > 0.0) || double.IsInfinity(dtMs)) return p;
            double d = dtMs / ms;
            return toSn ? Math.Min(1.0, p + d) : Math.Max(0.0, p - d);
        }

        /// <summary>SE's backbuffer alpha at progress <paramref name="p"/>: 1 − Ease(p).</summary>
        public static double Alpha(double p)
        {
            return 1.0 - Ease(p);
        }

        /// <summary>
 /// The blend factor on the colour channels so the stored colour becomes alpha × the stored colour (premultiplied for
 /// DWM): on an sRGB target the blend runs on linear values, hence alpha^2.2; on a linear target alpha;
 /// <paramref name="premultiply"/> false (the dev comparison <c>de.s7t fade straight</c>) leaves the colour alone (1).
 /// </summary>
        public static float ColourFactor(double alpha, bool srgbTarget, bool premultiply)
        {
            if (!premultiply) return 1f;
            double a = alpha < 0.0 ? 0.0 : (alpha > 1.0 ? 1.0 : alpha);
            if (double.IsNaN(a)) a = 1.0;
            return (float)(srgbTarget ? Math.Pow(a, SrgbGamma) : a);
        }

        /// <summary>Milliseconds left of a ramp at <paramref name="p"/> toward its target.</summary>
        public static double RemainingMs(double p, bool toSn, double ms)
        {
            if (!(ms > 0.0)) return 0.0;
            double left = toSn ? 1.0 - p : p;
            return Math.Max(0.0, left) * ms;
        }

        /// <summary>F: the scene skip may engage only once the ramp reached Subnautica (always without a ramp).</summary>
        public static bool SkipAllowed(bool fadeOn, double p)
        {
            return !fadeOn || p >= 1.0;
        }

        /// <summary>F: the alpha pass runs on a frame with SE's scene drawn, the DWM margins presented and part of Subnautica's picture due.</summary>
        public static bool PassWanted(bool fadeOn, double p, bool sceneDrawn, bool marginsReady)
        {
            return fadeOn && p > 0.0 && sceneDrawn && marginsReady;
        }

        /// <summary>F: the margins stay on while any of Subnautica's picture is still in the ramp (a fade back that outlasts the dwell).</summary>
        public static bool HoldMargins(bool fadeOn, double p)
        {
            return fadeOn && p > 0.0;
        }

        // ================================================================================================ the decision

 /// <summary>What the update thread knows of one tick.</summary>
        internal struct Inputs
        {
            /// <summary>The crossfade switch (<c>de.s7t fade</c>, env): a duration &gt; 0.</summary>
            public bool Enabled;
            public double Ms;
            /// <summary>The machine's effective picture policy is Site (the crossfade belongs to the band swap; Waterline keeps S2's cut and flash).</summary>
            public bool PictureSite;
            public bool InWorld;
            public bool DesignF, DesignAPrime;
            /// <summary>A′ ramp switch (<c>de.s7t fade aprime on</c>): SE's window alpha (WS_EX_LAYERED), off by default.</summary>
            public bool APrimeRamp;
            /// <summary>The dev see-through toggle or a forced request: instant (a look-at-the-other-picture aid).</summary>
            public bool Forced;
            /// <summary>A′ menu rule (SE raised at once while a menu is open).</summary>
            public bool MenuOpen;
            /// <summary>The machine's picture: F its scene skip (the latch), A′ its Lowered (latch ∧ ¬menu).</summary>
            public bool Latch;
            /// <summary>The machine's F15 leave dwell: the altitude exit condition holds (Approach → Space after 1 s).</summary>
            public bool LeaveArmed;
        }

        internal enum Reason : byte { None = 0, Latch = 1, LeaveDwell = 2, Back = 3, Loss = 4, Off = 5 }

        /// <summary>The fade state the update thread keeps between ticks.</summary>
        internal struct Memory
        {
            public bool Known;
            public bool On;
            public bool ToSn;
            public bool LatchPrev;
            public int SnapSeq;
        }

        /// <summary>The request of one tick.</summary>
        internal struct Decision
        {
            /// <summary>The ramp runs (else the request is S7a's: instant).</summary>
            public bool On;
            /// <summary>The target: Subnautica's picture.</summary>
            public bool ToSn;
            public double Ms;
            /// <summary>F: the scene skip the request carries (the render thread engages it at p = 1).</summary>
            public bool Skip;
            /// <summary>A′: lowered (the render thread lowers at p = 1).</summary>
            public bool Lowered;
            /// <summary>The target changed this tick (a fade or a snap starts).</summary>
            public bool Started;
            /// <summary>Jump to the target at once (render thread: on a new <see cref="Memory.SnapSeq"/>).</summary>
            public bool Snap;
            public int SnapSeq;
            public Reason Why;
        }

        /// <summary>
 /// The update thread's decision of one tick: whether the ramp runs, its target, the skip / lowered the request
 /// carries, and a snap. <paramref name="machineSkip"/> / <paramref name="machineLowered"/> are what the request would
 /// carry without the crossfade (S7a), returned unchanged while the ramp is off.
 /// </summary>
        public static Decision Decide(ref Inputs i, ref Memory m, bool machineSkip, bool machineLowered)
        {
            var d = new Decision { Ms = i.Ms, Skip = machineSkip, Lowered = machineLowered };
            bool designOk = i.DesignF || (i.DesignAPrime && i.APrimeRamp && !i.MenuOpen);
            d.On = i.Enabled && i.Ms > 0.0 && i.PictureSite && i.InWorld && !i.Forced && designOk;
            if (!d.On)
            {
                // S7a behaviour: the picture follows the latch at once (the render thread jumps while the ramp is off)
                d.ToSn = i.Latch;
                d.Started = m.Known && m.ToSn != d.ToSn;
                d.Why = d.Started ? Reason.Off : Reason.None;
            }
            else
            {
                bool wantSn = i.Latch && !i.LeaveArmed;
                d.ToSn = wantSn;
                bool was = m.Known && m.ToSn;
                if (was && !wantSn)
                {
                    d.Started = true;
                    if (i.Latch) d.Why = Reason.LeaveDwell;            // the fade back over the leave dwell, the latch still on
                    else { d.Why = Reason.Loss; d.Snap = true; }       // the latch dropped without the dwell: SE's picture at once
                }
                else if (!was && wantSn)
                {
                    d.Started = true;
                    d.Why = m.Known && m.On && m.LatchPrev ? Reason.Back : Reason.Latch;   // Back: the dwell cancelled (descending again)
                }
                d.Skip = machineSkip && wantSn;
                d.Lowered = machineLowered && wantSn;
            }
            if (d.Snap) m.SnapSeq++;
            d.SnapSeq = m.SnapSeq;
            m.Known = true;
            m.On = d.On;
            m.ToSn = d.ToSn;
            m.LatchPrev = i.Latch;
            return d;
        }

        public static string Name(Reason r)
        {
            switch (r)
            {
                case Reason.Latch: return "latch";
                case Reason.LeaveDwell: return "leaveDwell";
                case Reason.Back: return "back";
                case Reason.Loss: return "loss";
                case Reason.Off: return "off";
                default: return "none";
            }
        }
    }
}
