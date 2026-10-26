using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Sandbox.Graphics;
using VRage.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>

 /// what hides the remaining gap of a fast dive). A full-screen GUI sprite drawn by SE — the GUI pass is never skipped
 /// by design F, so the fade shows over both pictures — for <see cref="DurationMs"/> after the view machine's picture
 /// edge (<c>ViewOutputs.PictureEdge</c>, S7a): under the <c>Site</c> policy the swap at the Approach band (a sky tint),
 /// under <c>Waterline</c> the Underwater entry and exit, easing out from
 /// <see cref="PeakAlpha"/> to nothing. Subnautica's own surface crossing effects fire by themselves as the ghost camera
 /// crosses its surface. Every fade writes one <c>s7a.flash</c> row {tick, kind band|waterline, toSn, durationMs}
 /// (S7a.N2 checks one within a tick of every edge). Seeded failure S7a.M3 skips the fade.
 /// Drawn from the session component's <c>Draw</c> with <c>Sandbox.Graphics.MyGuiManager.DrawSpriteBatch(string,
 /// Vector2, Vector2, Color, MyGuiDrawAlignEnum, bool, bool, string, float, float, bool)</c>
 /// A missing member disables only the flash (logged once). <c>DEEPENGINEERS_FLASH_MS</c> overrides the duration
 /// (0 = off).
 /// S7t T1: under Site the band swap is the crossfade's (<see cref="CrossfadeRules"/>), so the band tint is
 /// off while the crossfade covers the swap (<see cref="S7tSettings.CrossfadeCovers"/>: Site, the crossfade on, design F or
 /// the A′ ramp); <c>de.s7t flash on</c> (env <c>DEEPENGINEERS_S7T_FLASH=on</c>) draws it over the crossfade too. The
 /// waterline tints (the Waterline policy) are unchanged.
 /// </summary>
    internal static class CrossingFlash
    {
        public const double DefaultDurationMs = 260.0;
        public const float PeakAlpha = 0.85f;
        /// <summary>S7t T1: band edges the crossfade covered (no tint, no s7a.flash row; the s7t.fade rows carry the swap).</summary>
        public static long SkippedByCrossfade;
        private static readonly Color EnterTint = new Color(170, 215, 230);   // water-lit white going under
        private static readonly Color ExitTint = new Color(235, 245, 250);    // bright spray coming out
        private static readonly Color BandInTint = new Color(205, 226, 244);  // S7a: SE's sky into Subnautica's (pale sky blue)
        private static readonly Color BandOutTint = new Color(226, 234, 246); // S7a: Subnautica's sky back to SE's (high haze)
        private const string BlankTexture = "Textures\\GUI\\Blank.dds";

        public static double DurationMs = ReadDuration();
        public static long Triggers, Frames, Skipped;
        public static string DisabledWhy;

        private static long _startTicks;
        private static bool _active;
        private static Color _tint;

        private static double ReadDuration()
        {
            try
            {
                string s = Environment.GetEnvironmentVariable("DEEPENGINEERS_FLASH_MS");
                double d;
                if (!string.IsNullOrEmpty(s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 0 && d <= 5000) return d;
            }
            catch (Exception) { }
            return DefaultDurationMs;
        }

        /// <summary>
 /// A picture edge: <paramref name="toSubnautica"/> = SE → Subnautica; <paramref name="band"/> = the swap at the
 /// Approach band (Site policy), else at the waterline (Waterline policy). Starts the fade and writes the row.
 /// </summary>
        public static void Trigger(bool toSubnautica, bool band, string why)
        {
            if (S7aPolicyMutants.NoFade)
            {
                Skipped++;
                DeLog.Info("DE.Flash skipped (MUTANT S7a.M3) " + (band ? "band" : "waterline") + (toSubnautica ? " to Subnautica" : " to SE") + " (" + why + ")");
                return;
            }
            if (band && S7tSettings.CrossfadeCovers && !S7tSettings.FlashWithFade)
            {
                // S7t T1: the crossfade blends the two pictures; the tint would hide what it shows
                SkippedByCrossfade++;
                DeLog.Info("DE.Flash band " + (toSubnautica ? "in" : "out") + " not drawn: the S7t crossfade covers the swap (" + why + "; de.s7t flash on draws it too)");
                return;
            }
            if (DurationMs <= 0 || DisabledWhy != null) return;
            _startTicks = Stopwatch.GetTimestamp();
            _active = true;
            _tint = band ? (toSubnautica ? BandInTint : BandOutTint) : (toSubnautica ? EnterTint : ExitTint);
            Triggers++;
            DeLog.Info("DE.Flash " + (band ? (toSubnautica ? "band in" : "band out") : (toSubnautica ? "dive" : "surface")) + " #" + Triggers + " " +
                       DurationMs.ToString("0", CultureInfo.InvariantCulture) + " ms (" + why + ")");
            Row(toSubnautica, band);
        }

        private static void Row(bool toSubnautica, bool band)
        {
            try
            {
                ViewStateDriver d = S2Host.Driver;
                var sb = new StringBuilder(160);
                sb.Append("{\"t\":"); JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                sb.Append(",\"ev\":\"s7a.flash\",\"tick\":").Append((d != null ? d.Tick : 0UL).ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"kind\":\"").Append(band ? "band" : "waterline").Append('"');
                sb.Append(",\"toSn\":").Append(toSubnautica ? "true" : "false");
                sb.Append(",\"durationMs\":").Append(DurationMs.ToString("0.#", CultureInfo.InvariantCulture));
                sb.Append(",\"n\":").Append(Triggers.ToString(CultureInfo.InvariantCulture)).Append('}');
                Jsonl.WriteLineBuffered(sb.ToString());
            }
            catch (Exception) { }
        }

        /// <summary>Per frame from the session component's Draw: draws the fade while it lasts.</summary>
        public static void Draw()
        {
            if (!_active || DisabledWhy != null) return;
            double t = (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency / DurationMs;
            if (t >= 1.0) { _active = false; return; }
            float k = (float)((1.0 - t) * (1.0 - t));   // ease-out
            var c = new Color(_tint.R, _tint.G, _tint.B, (byte)Math.Round(PeakAlpha * k * 255.0));
            try
            {
                MyGuiManager.DrawSpriteBatch(BlankTexture, Vector2.Zero, Vector2.One, c, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP, true);
                Frames++;
            }
            catch (Exception e)
            {
                _active = false;
                DisabledWhy = e.GetType().Name + ": " + e.Message;
                DeLog.Info("DE.Flash disabled: " + DisabledWhy);
            }
        }

        public static string Status()
        {
            return "flash " + (DisabledWhy != null ? "disabled (" + DisabledWhy + ")" : DurationMs <= 0 ? "off" : DurationMs.ToString("0", CultureInfo.InvariantCulture) + " ms") +
                   " triggers=" + Triggers + " frames=" + Frames + (Skipped > 0 ? " skippedByS7aM3=" + Skipped : "") +
                   (SkippedByCrossfade > 0 ? " coveredByS7tCrossfade=" + SkippedByCrossfade : "") + (S7tSettings.FlashWithFade ? " (S7t: drawn over the crossfade)" : "");
        }
    }
}
