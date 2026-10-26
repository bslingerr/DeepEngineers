using System;
using System.Collections.Generic;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    // =================================================================================================================
 // S15 "Gameplay and immersion polish", the game-free part of the SE side (compiled into tests/DeepEngineers.SE.Tests:
 // Link.cs fills the C5 block and calls the C6 writer, BuildFlavor.cs names the mutants). CONTRACT §5.4 "S15 conventions".
 // =================================================================================================================

 /// <summary>The S15 seeded failures of the SE side (dev builds; <c>de.mutant</c>, <c>DEEPENGINEERS_MUTANTS</c>).</summary>
    internal static class S15SeMutants
    {
        /// <summary>S15.M1: every rising edge is sent (no rearm, no cooldown, SE's audio ignored): S15.VOICE red.</summary>
        public static volatile bool NoRules;
        /// <summary>S15.M2: the character inside any grid's box counts as aboard (doors, ladders, decks): S15.ABOARD red.</summary>
        public static volatile bool AnyGrid;
        /// <summary>S15.AIR and S15.ORBIT red.</summary>
        public static volatile bool WaterOff;
        /// <summary>S15.M16: EnginePowerUp follows the thrust again (the removed rule: any thruster at 0.1): S15.POWER red.</summary>
        public static volatile bool ThrustPower;
        /// <summary>S15.WELCOME red.</summary>
        public static volatile bool HeldWelcome;
        /// <summary>S15.M18: the grid's size ignored: a small grid gets the Cyclops welcome, never the Seamoth's: S15.WELCOME red.</summary>
        public static volatile bool SizeIgnored;
        public static readonly string[] All = { "S15.M1", "S15.M2", "S15.M4", "S15.M16", "S15.M17", "S15.M18" };

        public static string Active
        {
            get
            {
                var parts = new List<string>(4);
                if (NoRules) parts.Add("S15.M1");
                if (AnyGrid) parts.Add("S15.M2");
                if (WaterOff) parts.Add("S15.M4");
                if (ThrustPower) parts.Add("S15.M16");
                if (HeldWelcome) parts.Add("S15.M17");
                if (SizeIgnored) parts.Add("S15.M18");
                return string.Join(",", parts.ToArray());
            }
        }

        public static void ClearAll() { NoRules = AnyGrid = WaterOff = ThrustPower = HeldWelcome = SizeIgnored = false; }

        /// <summary>Switches an S15 SE mutant; the log text, or null when the id is not an S15 id this side owns.</summary>
        public static string Set(string id, bool on)
        {
            string state = on ? "ON" : "OFF";
            switch ((id ?? "").Trim().ToUpperInvariant())
            {
                case "S15.M1": NoRules = on; return "S15.M1 " + state + " (every rising edge is sent: no rearm, no cooldown, SE's audio ignored)";
                case "S15.M2": AnyGrid = on; return "S15.M2 " + state + " (any grid box around the head counts as aboard: doors, ladders, decks)";
                case "S15.M4": WaterOff = on; return "S15.M4 " + state + " (the previous water look at bind: JWater2, transparent on)";
                case "S15.M16": ThrustPower = on; return "S15.M16 " + state + " (EnginePowerUp on the thrust again: the removed rule)";
                case "S15.M17": HeldWelcome = on; return "S15.M17 " + state + " (a welcome met with SE's audio live is held and sent under water: the removed rule)";
                case "S15.M18": SizeIgnored = on; return "S15.M18 " + state + " (the grid's size ignored: small grids get the Cyclops welcome)";
                case "S15.M8":
                case "S15.M9":
                case "S15.M10": return "REFUSE " + id.Trim() + ": retired with the rule it seeded (owner 2026-10-24: the breach, the oxygen alerts and the first-encounter lines are gone)";
                case "S15.M3":   //  Subnautica's now (the disc placed unmapped, its visibility on the mapped direction)
                case "S15.M5":
                case "S15.M6":
                case "S15.M7":
                case "S15.M11":
                case "S15.M12":
                case "S15.M13":
                case "S15.M14":
                case "S15.M15":
                case "S15.M19":
                case "S15.M20": return "REFUSE " + id.Trim() + ": a Subnautica-side S15 mutant (send de.mutant to Subnautica: /de sn mutant ...)";
                default: return null;
            }
        }
    }

    /// <summary>
 /// The S15 hooks of <c>Link</c>: the C5 block (format c5.s15) filled into every SeState, and the C6 region-space writer
 /// (formats c6.s15frame, c6.s15list, c6.s15events) called inside the C6 write. Both run on SE's update thread, like the
 /// game-side S15Se that sets their state.
 /// </summary>
    internal static unsafe class S15SeWire
    {
        private static S15WireState _state;
        private static bool _active;
        public static uint WelcomeSeq, PowerUpSeq, PowerDownSeq, SmallWelcomeSeq;
        public static long Writes, C6Writes, C6Errors;

        public static bool IsActive { get { return _active; } }
        public static S15WireState Current { get { return _state; } }

        public static void Set(S15WireState s)
        {
            s.WelcomeSeq = WelcomeSeq;
            s.PowerUpSeq = PowerUpSeq;
            s.PowerDownSeq = PowerDownSeq;
            s.SmallWelcomeSeq = SmallWelcomeSeq;
            _state = s;
            _active = true;
        }

        public static void Off() { _active = false; }

        /// <summary>The sonar's counter (format c6.s15events).</summary>
        public static uint SonarSeq;

        /// <summary>The counter of event <paramref name="ev"/> + 1 (a sent event).</summary>
        public static uint Bump(int ev)
        {
            switch (ev)
            {
                case S15Conventions.Event.Welcome: return ++WelcomeSeq;
                case S15Conventions.Event.EnginePowerUp: return ++PowerUpSeq;
                case S15Conventions.Event.EnginePowerDown: return ++PowerDownSeq;
                case S15Conventions.Event.SmallWelcome: return ++SmallWelcomeSeq;
                default: return 0u;
            }
        }

        /// <summary>The S15 hook of <c>Link.WriteC5</c>: the block of this record (cleared when inactive).</summary>
        public static void Fill(ref SeState st)
        {
            if (!_active) { S15Wire.Clear(ref st); return; }
            S15WireState s = _state;
            S15Wire.Write(ref st, ref s);
            Writes++;
        }

        /// <summary>The writer of the C6 region space (set by S15Se while active; null = nothing written).</summary>
        internal delegate void C6Writer(byte* c6Region, ulong tick);
        internal static C6Writer C6Extra;

        /// <summary>The S15 hook of <c>Link.WriteSeTime</c>, inside its lock: never throws into the C6 writer.</summary>
        public static void WriteC6(byte* c6Region, ulong tick)
        {
            C6Writer w = C6Extra;
            if (w == null) return;
            try { w(c6Region, tick); C6Writes++; }
            catch (Exception) { C6Errors++; }
        }
    }

    /// <summary>The S15 switches read once per session (env) and changed live by de.s15 (dev).</summary>
    internal static class S15Switch
    {
        public const string EnvVar = "DEEPENGINEERS_S15";
        public static volatile bool VoiceOn = true, SkyOn = true, WaterOn = true;

        /// <summary>Off with DEEPENGINEERS_S15=off (or 0, false); "voice", "sky" and "water" in a comma list switch those off only (e.g. "nosky").</summary>
        public static bool EnabledFromEnvironment()
        {
            string v = null;
            try { v = Environment.GetEnvironmentVariable(EnvVar); } catch (Exception) { }
            if (string.IsNullOrEmpty(v)) return true;
            v = v.Trim().ToLowerInvariant();
            if (v == "off" || v == "0" || v == "false") return false;
            foreach (string part in v.Split(','))
            {
                string p = part.Trim();
                if (p == "novoice") VoiceOn = false;
                else if (p == "nosky") SkyOn = false;
                else if (p == "nowater") WaterOn = false;
            }
            return true;
        }
    }
}
