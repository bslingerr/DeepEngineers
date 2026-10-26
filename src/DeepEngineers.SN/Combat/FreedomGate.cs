using System;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The Freedom gate (CONTRACT §5.4 "C7 for S6"; S6.N2): <c>GameModeUtils.GetGameMode(out GameModeOption mode, out
 /// GameModeOption cheats)</c> gives mode == Freedom (NoSurvival, 2) and <c>GameModeUtils.IsInvisible</c> (the
 /// effective NoAggression bit) is false. The decision table is
 /// <see cref="HostSaveRules.CheckLoaded"/> (S1, tested) through <see cref="FreedomRules"/>. Logged
 /// <c>DE.S6 freedom mode=Freedom invisible=false PASS</c> (else REFUSED); evaluated every second by the S6 host.
 /// </summary>
    internal static class FreedomGate
    {
        public static bool Evaluate(out string line, out int mode, out int cheats, out bool invisible)
        {
            mode = 0; cheats = 0; invisible = false;
            try
            {
                GameModeOption m, c;
                GameModeUtils.GetGameMode(out m, out c);
                mode = (int)m;
                cheats = (int)c;
                invisible = GameModeUtils.IsInvisible();
            }
            catch (Exception e)
            {
                line = "DE.S6 freedom REFUSED (GameModeUtils unreadable: " + e.GetType().Name + ": " + e.Message + ")";
                return false;
            }
            return FreedomRules.Evaluate(mode, cheats, invisible, out line);
        }
    }
}
