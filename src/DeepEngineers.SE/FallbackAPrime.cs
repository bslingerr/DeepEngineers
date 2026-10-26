namespace DeepEngineers.SE
{
    /// <summary>
 /// Fallback A′ (ARCHITECTURE §5.1 "Fallback A′"; ROADMAP S2 "Fallback A′ behind a config switch"), game-independent
 /// part. Underwater SE lowers itself below Subnautica (<c>SetWindowPos(seHwnd, snHwnd, …, NOACTIVATE)</c> on the render
 /// thread) and stops drawing (the <c>MyGameWindow.DrawEnabled</c> getter patched to false, A′ only); surfacing raises it
 /// (<c>HWND_TOP</c>; SE is the foreground process, so this is legal, VERIFIED synthetic T-D0). Menu rule: while the
 /// cursor is visible, the chat entry is open, a gameplay screen is active or SE is paused, SE raises itself and
 /// draws (its own Water Mod view behind the menu). Selected by <c>config.json</c> <c>"display": "aprime"</c>, the
 /// environment variable <c>DEEPENGINEERS_DISPLAY=aprime</c>, <c>de.display mode=aprime</c>, or a start-up probe that
 /// finds DWM alpha not honoured.
 /// </summary>
    internal static class FallbackAPrime
    {
        /// <summary>The <c>DrawEnabled</c> getter returns false while this is set (render thread: set after lowering,
 /// cleared before raising so the frame shown when SE comes back up is a fresh one).</summary>
        public static volatile bool DrawDisabled;

        /// <summary>The A′ menu rule: a menu, the chat entry, a gameplay screen or the pause keeps SE raised and drawing.</summary>
        public static bool MenuOpen(bool cursorVisible, bool chatEntryVisible, bool gameplayScreenActive, bool paused)
        {
            return cursorVisible || chatEntryVisible || gameplayScreenActive || paused;
        }

        /// <summary>SE should be below Subnautica now: Underwater (or forced) and no menu.</summary>
        public static bool WantLowered(S2State state, bool forcedSeeThrough, bool menuOpen)
        {
            return (state == S2State.Underwater || forcedSeeThrough) && !menuOpen;
        }
    }
}
