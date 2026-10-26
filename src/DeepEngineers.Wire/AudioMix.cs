namespace DeepEngineers.Wire
{
    /// <summary>
 /// The audio mix of the view state machine (ARCHITECTURE §5.3, ROADMAP S2 state table, CONTRACT §1.1): targets per
 /// state and the 300 ms linear crossfade on the unscaled clock. Pure helpers; nothing here touches an engine.
 /// <list type="bullet">
 /// <item>Underwater: Subnautica 1.0; SE game 0.7 × config; SE music 0; Water Mod underwater sound 0 (emitters
 /// scaled, settings untouched); SE HUD = config (not a factor).</item>
 /// <item>Every other state: Subnautica 0 (muted); SE at its config volumes; Water Mod untouched (factor 1).</item>
 /// <item>SE writes volumes only while focused, unpaused, with no GUI screen other than gameplay open and not loading
 /// (<see cref="WriteGate"/>); SE's own <c>EnableMuteWhenNotInFocus</c> handles Alt+Tab.</item>
 /// <item>Subnautica's bus target is 0 while SE is not foreground (both silent while away).</item>
 /// </list>
 /// </summary>
    public static class AudioMix
    {
        /// <summary>C5 crossfadeMs (CONTRACT §5.4: 300).</summary>
        public const float CrossfadeMs = 300f;
        public const long CrossfadeUs = 300000;

        public const double SnBusUnderwater = 1.0;
        public const double SeGameUnderwater = 0.7;
        public const double SeMusicUnderwater = 0.0;
        public const double WaterModUnderwater = 0.0;

        /// <summary>
 /// Crossfade level at <paramref name="nowUs"/> of a linear ramp that started at <paramref name="startUs"/> from
 /// <paramref name="from"/> towards 1 (<paramref name="toUnderwater"/>) or 0, over <see cref="CrossfadeUs"/>.
 /// </summary>
        public static double Level(double from, bool toUnderwater, long startUs, long nowUs)
        {
            double target = toUnderwater ? 1.0 : 0.0;
            long dt = nowUs - startUs;
            if (dt <= 0) return from;
            if (dt >= CrossfadeUs) return target;
            return from + (target - from) * ((double)dt / CrossfadeUs);
        }

        /// <summary>The value between the above-water value (level 0) and the underwater value (level 1).</summary>
        public static double Lerp(double above, double underwater, double level)
        {
            return above + (underwater - above) * level;
        }

        /// <summary>
 /// SE volumes may be written: focused (or SE's <c>EnableMuteWhenNotInFocus</c> off), unpaused, no GUI screen
 /// other than gameplay, not loading.
 /// </summary>
        public static bool WriteGate(bool foreground, bool paused, bool guiScreenOpen, bool loading)
        {
            return foreground && !paused && !guiScreenOpen && !loading;
        }
    }

    /// <summary>
 /// A linear ramp on an unscaled microsecond clock, for an engine that fades to a target itself (Subnautica's bus to
 /// C5 <c>snBusTarget</c> over C5 <c>crossfadeMs</c>). Retargeting starts from the current value, so ramps in both
 /// directions overlap and never jump. A plain struct: no allocation.
 /// </summary>
    public struct LinearRamp
    {
        public double From;
        public double To;
        public long StartUs;
        public long DurationUs;

        /// <summary>Current value.</summary>
        public double Value(long nowUs)
        {
            if (DurationUs <= 0) return To;
            long dt = nowUs - StartUs;
            if (dt <= 0) return From;
            if (dt >= DurationUs) return To;
            return From + (To - From) * ((double)dt / DurationUs);
        }

        /// <summary>Starts a ramp to <paramref name="target"/> from the current value (no-op if already heading there).</summary>
        public void Retarget(double target, long nowUs, long durationUs)
        {
            if (target == To && durationUs == DurationUs) return;
            From = Value(nowUs);
            To = target;
            StartUs = nowUs;
            DurationUs = durationUs;
        }

        /// <summary>Jumps to <paramref name="value"/> at once.</summary>
        public void Set(double value, long nowUs)
        {
            From = value;
            To = value;
            StartUs = nowUs;
            DurationUs = 0;
        }
    }
}
