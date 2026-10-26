using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeepEngineers.SN
{
    /// <summary>
 /// Subnautica's legacy save-slot game mode
 /// as written in a slot's `gameinfo.json` and held in
 /// `SaveLoadManager.GameInfo.gameMode`. Plain ints so the decision table stays Unity-free (tests run without the game).
 /// </summary>
    internal static class LegacyGameMode
    {
        public const int Survival = 0, Freedom = 1, Hardcore = 2, Creative = 3, None = 4;

        public static string Name(int mode)
        {
            switch (mode)
            {
                case Survival: return "Survival";
                case Freedom: return "Freedom";
                case Hardcore: return "Hardcore";
                case Creative: return "Creative";
                case None: return "None";
                default: return "Unknown(" + mode.ToString(CultureInfo.InvariantCulture) + ")";
            }
        }
    }

    /// <summary>
 /// `GameModeOption` flag values. `Utils.SetLegacyGameMode`
 /// maps Freedom to NoSurvival (2) and Creative to Cheats | NoSurvival, which contains NoAggression (0x80): with it
 /// `GameModeUtils.IsInvisible` is true and creatures never attack (ARCHITECTURE §5.10).
 /// </summary>
    internal static class GameModeOptionBits
    {
        public const int None = 0, Permadeath = 1, NoSurvival = 2, NoCost = 4, NoBlueprints = 8, NoEnergy = 0x10,
            NoPressure = 0x20, NoOxygen = 0x40, NoAggression = 0x80, NoHints = 0x100, NoRadiation = 0x200, InitialItems = 0x400;
        public const int Cheats = NoCost | NoBlueprints | NoEnergy | NoPressure | NoOxygen | NoAggression | NoRadiation | InitialItems;
        public const int Survival = 0, Hardcore = Permadeath | NoHints, Freedom = NoSurvival, Creative = Cheats | NoSurvival;

        public static string Name(int mode)
        {
            if (mode == Survival) return "Survival";
            if (mode == Freedom) return "Freedom";
            if (mode == Hardcore) return "Hardcore";
            if (mode == Creative) return "Creative";
            return "0x" + mode.ToString("x", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>One save slot as the main menu knows it (`SaveLoadManager.GetActiveSlotNames` + `GetGameInfo`).</summary>
    internal struct HostSlot
    {
        public string Slot;
        public string Session;
        public int GameMode;
        public int ChangeSet;
        public int GameTime;
        public long DateTicks;
        public bool Corrupted;

        public override string ToString()
        {
            return Slot + " session=" + Session + " gameMode=" + LegacyGameMode.Name(GameMode) + " changeSet=" +
                   ChangeSet.ToString(CultureInfo.InvariantCulture) + " gameTime=" + GameTime.ToString(CultureInfo.InvariantCulture) + "s" +
                   (Corrupted ? " corrupted" : "");
        }
    }

    /// <summary>Outcome of <see cref="HostSaveRules.SelectSlot"/>.</summary>
    internal sealed class HostSlotChoice
    {
        public bool Ok;
        public HostSlot Slot;
        /// <summary>Short refusal reason (the word after `DE.HostSave REFUSE`), null when Ok.</summary>
        public string Reason;
        public string Detail;
        /// <summary>Slots whose session id matched.</summary>
        public int Matches;

        public override string ToString()
        {
            return Ok ? "OK " + Slot : "REFUSE " + Reason + (Detail != null ? " (" + Detail + ")" : "");
        }
    }

    /// <summary>Outcome of <see cref="HostSaveRules.CheckLoaded"/>.</summary>
    internal sealed class HostModeCheck
    {
        public bool Ok;
        public string Reason;
        /// <summary>The S1.N5 line: `DE.HostSave mode=&lt;name&gt; invisible=&lt;bool&gt;` (+ REFUSE reason).</summary>
        public string Line;
    }

    /// <summary>
 /// The host-save decision table (ROADMAP S1 SN plugin, CONTRACT §1 "Saves" / "Subnautica session", ARCHITECTURE
 /// §5.10). Unity-free: the plugin feeds it the menu's slot list and, after the load, the effective game mode.
 /// <list type="bullet">
 /// <item>The host save is chosen by its session id (Subnautica names slots `slotNNNN`; the ROADMAP name
 /// "DeepEngineers-Host" is not a slot name).
 /// with <c>DEEPENGINEERS_SN_HOST_SESSION</c>; <c>DEEPENGINEERS_SN_HOST_SLOT</c> names the slot explicitly (the
 /// throwaway copies of S1.M3/M4 share the session id, so they are selected by slot name).</item>
 /// <item>Refused at the menu (never loaded): no matching slot, several slots sharing the session without a slot
 /// override, a named slot of another session, a corrupted slot, a slot whose mode is not Freedom.</item>
 /// <item>Refused after the load: effective mode not Freedom, or `GameModeUtils.IsInvisible` (NoAggression).</item>
 /// </list>
 /// </summary>
    internal static class HostSaveRules
    {
        /// </summary>
        public const string DefaultSessionId = "702d8c12-288a-43c2-9d23-a9efde02f690";

        /// <summary>S9: no slot at all is a usable Freedom save (not corrupted): the plugin may create one.</summary>
        public static bool NoFreedomSlot(IList<HostSlot> slots)
        {
            if (slots == null) return true;
            for (int i = 0; i < slots.Count; i++)
                if (!slots[i].Corrupted && slots[i].GameMode == LegacyGameMode.Freedom) return false;
            return true;
        }

        public static HostSlotChoice SelectSlot(IList<HostSlot> slots, string sessionId, string slotOverride)
        {
            var r = new HostSlotChoice();
            if (string.IsNullOrEmpty(sessionId)) sessionId = DefaultSessionId;
            int count = slots != null ? slots.Count : 0;
            if (count == 0)
            {
                r.Reason = "no-saves";
                r.Detail = "the main menu lists no save slot";
                return r;
            }
            var matching = new List<HostSlot>();
            for (int i = 0; i < count; i++)
                if (string.Equals(slots[i].Session, sessionId, StringComparison.OrdinalIgnoreCase)) matching.Add(slots[i]);
            r.Matches = matching.Count;

            HostSlot chosen;
            if (!string.IsNullOrEmpty(slotOverride))
            {
                int at = -1;
                for (int i = 0; i < count; i++)
                    if (string.Equals(slots[i].Slot, slotOverride, StringComparison.OrdinalIgnoreCase)) { at = i; break; }
                if (at < 0)
                {
                    r.Reason = "slot-not-found";
                    r.Detail = "slot " + slotOverride + " is not among the " + count + " slots of the main menu";
                    return r;
                }
                chosen = slots[at];
                if (!string.Equals(chosen.Session, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    r.Reason = "session-mismatch";
                    r.Detail = "slot " + chosen.Slot + " has session " + chosen.Session + ", expected " + sessionId;
                    return r;
                }
            }
            else
            {
                if (matching.Count == 0)
                {
                    r.Reason = "session-not-found";
                    r.Detail = "no slot has session " + sessionId + " (" + count + " slots)";
                    return r;
                }
                if (matching.Count > 1)
                {
                    var names = new List<string>();
                    foreach (HostSlot s in matching) names.Add(s.Slot);
                    r.Reason = "ambiguous";
                    r.Detail = matching.Count + " slots share session " + sessionId + " (" + string.Join(",", names.ToArray()) +
                               "); name one with " + S1Switches.HostSlotVar;
                    return r;
                }
                chosen = matching[0];
            }

            r.Slot = chosen;
            if (chosen.Corrupted)
            {
                r.Reason = "corrupted";
                r.Detail = "slot " + chosen.Slot + " is marked corrupted";
                return r;
            }
            if (chosen.GameMode != LegacyGameMode.Freedom)
            {
                r.Reason = "mode";
                r.Detail = "slot " + chosen.Slot + " gameMode=" + LegacyGameMode.Name(chosen.GameMode) +
                           " (Freedom required: Creative contains NoAggression, survival modes drain the ghost)";
                return r;
            }
            r.Ok = true;
            return r;
        }

        /// <summary>
 /// After the load: `GameModeUtils.GetGameMode(out mode, out cheats)` and `GameModeUtils.IsInvisible`.
 /// Pass: mode is exactly Freedom (NoSurvival) and invisible is false (and NoAggression in neither word).
 /// </summary>
        public static HostModeCheck CheckLoaded(int mode, int cheats, bool invisible)
        {
            var r = new HostModeCheck();
            bool aggressionOff = invisible || ((mode | cheats) & GameModeOptionBits.NoAggression) != 0;
            string head = "DE.HostSave mode=" + GameModeOptionBits.Name(mode) + " invisible=" + (invisible ? "true" : "false");
            string cheatText = cheats != 0 ? " cheats=0x" + cheats.ToString("x", CultureInfo.InvariantCulture) : "";
            if (mode != GameModeOptionBits.Freedom)
            {
                r.Reason = "mode";
                r.Line = head + cheatText + " REFUSE mode (Freedom required)";
                return r;
            }
            if (aggressionOff)
            {
                r.Reason = "invisible";
                r.Line = head + cheatText + " REFUSE invisible (NoAggression: creatures would never attack)";
                return r;
            }
            r.Ok = true;
            r.Line = head + cheatText;
            return r;
        }
    }

    /// <summary>
 /// Run-time switches of the S1 Subnautica side (environment variables, read once; dev and test use). All have a safe
 /// default: a plain bridged launch auto-loads the host save and changes nothing else.
 /// </summary>
    internal static class S1Switches
    {
        /// <summary>"0" keeps Subnautica at the main menu in bridge mode (S0-style session; launcher `--ready-phase Menu`).</summary>
        public const string AutoLoadVar = "DEEPENGINEERS_SN_AUTOLOAD";
        public const string HostSessionVar = "DEEPENGINEERS_SN_HOST_SESSION";
        public const string HostSlotVar = "DEEPENGINEERS_SN_HOST_SLOT";
        /// <summary>"1": one `DE.CamTrace` line per presented frame (S1.N1/N2 histograms; heavy, tests only).</summary>
        public const string TraceVar = "DEEPENGINEERS_SN_TRACE";
        /// <summary>"1" (dev builds): S1.N11 test hook, "Run in background" set false in memory before the enforcement.</summary>
        public const string TestRunInBackgroundOffVar = "DEEPENGINEERS_SN_TEST_RUNINBACKGROUND_OFF";

        /// <summary>Test seam: the environment reader (tests replace it).</summary>
        public static Func<string, string> Reader = ReadEnvironment;

        private static string ReadEnvironment(string name)
        {
            try { return Environment.GetEnvironmentVariable(name); }
            catch (Exception) { return null; }
        }

        public static bool AutoLoad { get { return !IsOff(Reader(AutoLoadVar)); } }

        public static string HostSession
        {
            get
            {
                string v = Reader(HostSessionVar);
                return string.IsNullOrEmpty(v) ? HostSaveRules.DefaultSessionId : v.Trim();
            }
        }

        public static string HostSlot
        {
            get
            {
                string v = Reader(HostSlotVar);
                return string.IsNullOrEmpty(v) ? null : v.Trim();
            }
        }

        public static bool Trace { get { return IsOn(Reader(TraceVar)); } }

        public static bool TestRunInBackgroundOff { get { return BuildFlavor.Dev && IsOn(Reader(TestRunInBackgroundOffVar)); } }

        public static bool IsOn(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            v = v.Trim();
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                   v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsOff(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            v = v.Trim();
            return v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                   v.Equals("no", StringComparison.OrdinalIgnoreCase);
        }

        public static string Describe()
        {
            return "autoload=" + (AutoLoad ? "on" : "off") + " session=" + HostSession + " slot=" + (HostSlot ?? "(by session)") +
                   " trace=" + (Trace ? "on" : "off") + (TestRunInBackgroundOff ? " TEST-runInBackgroundOff" : "");
        }
    }
}
