using System;

namespace DeepEngineers.SE
{
    /// <summary>The SE volumes the <c>UpdateSound</c> postfix writes this update (or not).</summary>
    internal struct AudioDecision
    {
        /// <summary>Write <see cref="Game"/>, <see cref="Music"/> and <see cref="Hud"/> into <c>MyAudio.Static</c>.</summary>
        public bool Write;
        public float Game, Music, Hud;
        /// <summary>Underwater mix 0 (above water) … 1 (underwater), crossfaded (the machine's <c>MixLevel</c>).</summary>
        public float Mix;
        /// <summary>The machine's audio gate (focused or SE audible unfocused, unpaused, no GUI screen, not loading).</summary>
        public bool GateOpen;
        /// <summary>C5 <c>seMixActive</c>.</summary>
        public bool MixActive;
        /// <summary>Multiplier for the Water Mod's underwater emitters (1 above water, 0 underwater).</summary>
        public float WaterScale;
    }

    /// <summary>
 /// SE-side audio arithmetic (ARCHITECTURE §5.3, CONTRACT §1.1), game-independent. The mix factors, the 300 ms
 /// crossfade and the write gate come from Wire's view state machine (<c>ViewOutputs.SeGameFactor</c>,
 /// <c>SeMusicFactor</c>, <c>WaterModFactor</c>, <c>SeAudioWrite</c>, <c>MixLevel</c>); this class turns them into SE
 /// volumes (game = config × factor, music = config × factor, HUD = config game volume, what SE itself restores after
 /// Alt+Tab) and adds the SE-side rule that, without the display bridge, the plugin writes only what is needed to
 /// return SE to its config values after a dive.
 /// </summary>
    internal static class AudioMath
    {
        public static void Volumes(float configGame, float configMusic, float gameFactor, float musicFactor, out float game, out float music, out float hud)
        {
            game = configGame * Clamp01(gameFactor);
            music = configMusic * Clamp01(musicFactor);
            hud = configGame;
        }

        /// <summary>
 /// The write decision: the machine's gate, and, while the bridge is not up, only until the config values have
 /// been written back after a dive (<paramref name="restorePending"/>): SE is left alone without Subnautica.
 /// </summary>
        public static bool ShouldWrite(bool machineGate, bool bridgeUp, bool mixActive, ref bool restorePending)
        {
            if (!machineGate) return false;
            if (!bridgeUp && !mixActive && !restorePending) return false;
            restorePending = mixActive;
            return true;
        }

        /// <summary>
 /// Review fix (finding 10): out of the world the machine's gate is closed, so a pending restore (the
 /// last write carried an active mix) is written once with the config values when SE may sound
 /// (<paramref name="allowed"/>: focused or audible unfocused, not loading or saving).
 /// </summary>
        public static bool ShouldRestoreOutsideWorld(bool allowed, ref bool restorePending)
        {
            if (!restorePending || !allowed) return false;
            restorePending = false;
            return true;
        }

        public static float Clamp01(float v)
        {
            if (float.IsNaN(v)) return 1f;
            return v < 0f ? 0f : v > 1f ? 1f : v;
        }
    }
}
