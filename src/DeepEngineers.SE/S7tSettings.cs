using System;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S7t se-composite: the live settings of the crossfade and the impostors, each read once from its environment variable at
 /// start (same default as the command) and switched by <c>de.s7t</c>. Game-independent (static values only).
 /// </summary>
    internal static class S7tSettings
    {
        public const string FlashEnvVar = "DEEPENGINEERS_S7T_FLASH";
        public const string APrimeEnvVar = "DEEPENGINEERS_FADE_APRIME";
        public const string StraightEnvVar = "DEEPENGINEERS_FADE_STRAIGHT";

        /// <summary>The crossfade's duration (ms; 0 = off, S7a's cut and band tint). Env <c>DEEPENGINEERS_FADE_MS</c>, default 700.</summary>
        public static double FadeMs;
        public static string FadeSource;
        /// <summary>The S7a band tint drawn over the crossfade too (default off). Env <c>DEEPENGINEERS_S7T_FLASH=on</c>.</summary>
        public static volatile bool FlashWithFade;
        /// <summary>The A′ ramp through SE's window alpha (default off: HYPOTHESIS, A′ is the flip-model fallback). Env <c>DEEPENGINEERS_FADE_APRIME=on</c>.</summary>
        public static volatile bool APrimeRamp;
        /// <summary>Dev comparison: the faded colour not premultiplied (default off). Env <c>DEEPENGINEERS_FADE_STRAIGHT=on</c>.</summary>
        public static volatile bool Straight;
        /// <summary>The impostors (T3; default on). Env <c>DEEPENGINEERS_IMPOSTORS=off</c>.</summary>
        public static volatile bool Impostors;
        /// <summary>Set by the display controller each tick: the crossfade covers the band swap now (the flash stays off).</summary>
        public static volatile bool CrossfadeCovers;
        /// <summary>Set by <c>RenderHooks.PatchS7t</c>: the <c>ConsumeMainSprites</c> prefix is installed (the crossfade pass can run).</summary>
        public static volatile bool SpritesHookOk;

        static S7tSettings()
        {
            FadeMs = CrossfadeRules.DefaultMs;
            FadeSource = "default";
            try
            {
                string v = Environment.GetEnvironmentVariable(CrossfadeRules.EnvVar);
                double ms;
                if (!string.IsNullOrEmpty(v) && CrossfadeRules.TryParseMs(v, out ms)) { FadeMs = ms; FadeSource = "env"; }
                FlashWithFade = OnOff(Environment.GetEnvironmentVariable(FlashEnvVar), false);
                APrimeRamp = OnOff(Environment.GetEnvironmentVariable(APrimeEnvVar), false);
                Straight = OnOff(Environment.GetEnvironmentVariable(StraightEnvVar), false);
                Impostors = OnOff(Environment.GetEnvironmentVariable(S7tImpostorRules.EnvVar), true);
            }
            catch (Exception) { Impostors = true; }
        }

        /// <summary>"on|true|1|yes" / "off|false|0|no" (blanks ignored); anything else = <paramref name="dflt"/>.</summary>
        public static bool OnOff(object v, bool dflt)
        {
            if (v is bool) return (bool)v;
            if (v is double) return (double)v != 0.0;
            string s = v as string;
            if (s == null) return dflt;
            switch (s.Trim().ToLowerInvariant())
            {
                case "on": case "true": case "1": case "yes": return true;
                case "off": case "false": case "0": case "no": return false;
                default: return dflt;
            }
        }

        public static string Describe()
        {
            return "fade=" + (FadeMs > 0 ? FadeMs.ToString("0", CultureInfo.InvariantCulture) + " ms" : "off") + " (" + FadeSource + ")" +
                   " flash=" + (FlashWithFade ? "on" : "off") + " fadeAprime=" + (APrimeRamp ? "on" : "off") +
                   (Straight ? " straight" : "") + " impostors=" + (Impostors ? "on" : "off");
        }
    }
}
