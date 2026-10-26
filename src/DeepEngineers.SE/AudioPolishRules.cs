using System;
using System.Globalization;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The game-independent half of S7's audio polish on the SE side (ARCHITECTURE §5.3 "Audio polish (S7)" A1–A6; CONTRACT
 /// §3.2 and §5.4 "S7 conventions" <c>format audio.muffle</c>, <c>audio.bus</c>), compiled into
 /// tests/DeepEngineers.SE.Tests. The items, exactly as the contract lists them (nothing new):
 /// <list type="bullet">

 /// choice among SE's four helmet/cockpit effects, <c>de.s7audio {effect}</c>) while the plugin's underwater SE mix is
 /// in effect (C5 <c>seMixActive</c>) and the camera is not in an airtight room, only for sounds SE marks
 /// <c>ModifiableByHelmetFilters</c>; HUD and music are 2D and never pass through the 3D emitter;</item>
 /// <item>A2 the bus above water: unchanged (Subnautica muted above water under <c>Site</c>; event sounds only on
 /// <c>Underwater</c> rows: the Subnautica rule, <c>AudioPolishRules</c> there);</item>
 /// <item>A3 the dry room under water: no muffle (<see cref="ShouldMuffle"/>'s room term), Subnautica's interior
 /// ambience through <c>IsInsideWalkable</c>, recorded by S7.N2/N11;</item>
 /// <item>A4 crossfade reversal: settled as built (SE restarts a full <c>crossfadeMs</c> ramp from the current level,
 /// Subnautica moves its bus at the constant rate 1/<c>crossfadeMs</c>): no code, no change;</item>
 /// <item>A5 biome and depth ambience: unchanged (the ghost at the camera feeds <c>FMODGameParams</c>), recorded by
 /// S7.N11 on the Subnautica side;</item>
 /// <item>A6 event sounds: Subnautica's own FMOD events, counted by the Subnautica audio rows.</item>
 /// </list>
 /// </summary>
    internal static class AudioPolishRules
    {
        /// <summary>SE's own effect for an unhelmeted head without oxygen (<c>MyEntity3DSoundEmitter.m_effectNoHelmetNoOxygen</c>).</summary>
        public const string DefaultEffect = "LowPassNoHelmetNoOxy";

        /// s7audio {effect}</c>): SE's four, VERIFIED.</summary>
        public static readonly string[] Effects = { "LowPassNoHelmetNoOxy", "LowPassHelmet", "LowPassCockpit", "LowPassCockpitNoOxy" };

        /// <summary>Rows <c>s7.audio</c>: every change, and once per second while the underwater mix is active.</summary>
        public const ulong RowPeriodMs = 1000;

        /// <summary>A1: the muffle decision for one <c>SelectEffect</c> call.</summary>
 /// <param name="enabled">the polish is on (<c>de.s7audio {effect: off}</c> turns it off)</param>
 /// <param name="mixActive">C5 <c>seMixActive</c> of this update (the underwater SE mix is in effect)</param>
 /// <param name="cameraInRoom">C1 flags bit 3: the camera is in an airtight room (A3: dry air, no muffle)</param>
 /// <param name="modifiable">the emitter's <c>MySoundData.ModifiableByHelmetFilters</c> (true when SE has no data yet, as SE's own check)</param>
 /// <param name="mutantOff">S7.M19: the postfix leaves SE's answer</param>
        public static bool ShouldMuffle(bool enabled, bool mixActive, bool cameraInRoom, bool modifiable, bool mutantOff)
        {
            return enabled && !mutantOff && mixActive && !cameraInRoom && modifiable;
        }

        /// <summary>
 /// A1 with SE's sound mode: the postfix never forces an effect unless the world plays
 /// realistic sound (<c>MySession.Static.Settings.RealisticSound</c>) and <c>MyFakes.ENABLE_NEW_SOUNDS</c> is set. In
 /// an Arcade world SE's own <c>SelectEffect</c> answers <c>NullOrEmpty</c> and installs no effect delegate
 /// so a forced answer applies no filter and only makes
 /// <c>Update</c> restart every looping modifiable sound (<c>:376-387</c>). No safe Arcade route exists through the
 /// public API (the effect must replace the emitter's private voice), so an Arcade world is left unfiltered:
 /// Subnautica's mix carries the dive.
 /// </summary>
        public static bool ShouldMuffle(bool enabled, bool mixActive, bool cameraInRoom, bool modifiable, bool mutantOff, bool realisticSound)
        {
            return realisticSound && ShouldMuffle(enabled, mixActive, cameraInRoom, modifiable, mutantOff);
        }

        /// <summary>The once-per-world line of an Arcade world.</summary>
        public const string ArcadeLine = "DE.Audio muffle: Arcade sound mode, SE's own effect path is off — Subnautica's mix carries the dive";

        /// <summary>
 /// <c>de.s7audio {effect}</c>: one of <see cref="Effects"/> (case-insensitive; the canonical spelling returned), or
 /// <c>off</c> (polish disabled: <paramref name="effect"/> null). False for anything else.
 /// </summary>
        public static bool TryParseEffect(string text, out string effect, out bool off)
        {
            effect = null;
            off = false;
            string t = (text ?? "").Trim();
            if (t.Length == 0) return false;
            if (string.Equals(t, "off", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "none", StringComparison.OrdinalIgnoreCase))
            {
                off = true;
                return true;
            }
            if (string.Equals(t, "default", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "on", StringComparison.OrdinalIgnoreCase))
            {
                effect = DefaultEffect;
                return true;
            }
            foreach (string e in Effects)
                if (string.Equals(e, t, StringComparison.OrdinalIgnoreCase)) { effect = e; return true; }
            return false;
        }

        /// <summary>The state an <c>s7.audio</c> row reports; a change of it writes a row at once.</summary>
        public static int StateKey(bool enabled, bool mixActive, bool cameraInRoom, bool mutantOff, string effect)
        {
            int k = (enabled ? 1 : 0) | (mixActive ? 2 : 0) | (cameraInRoom ? 4 : 0) | (mutantOff ? 8 : 0);
            return k ^ ((effect ?? "").GetHashCode() << 4);
        }

        /// <summary>As <see cref="StateKey(bool, bool, bool, bool, string)"/> with the sound mode.</summary>
        public static int StateKey(bool enabled, bool mixActive, bool cameraInRoom, bool mutantOff, string effect, bool realisticSound)
        {
            return StateKey(enabled, mixActive, cameraInRoom, mutantOff, effect) ^ (realisticSound ? 0 : 0x40000000);
        }

        /// <summary>Row cadence: a state change, else once per <see cref="RowPeriodMs"/> while the mix is active.</summary>
        public static bool RowDue(bool changed, bool mixActive, ulong nowMs, ulong lastRowMs)
        {
            if (changed) return true;
            return mixActive && nowMs >= lastRowMs + RowPeriodMs;
        }

        /// <summary>The <c>DE.S7 audio</c> status line.</summary>
        public static string Line(bool applied, bool enabled, string effect, bool mixActive, bool cameraInRoom, long calls, long muffled, string missing)
        {
            return "DE.S7 audio muffle " + (applied ? "patched" : "NOT patched" + (string.IsNullOrEmpty(missing) ? "" : " (" + missing + ")")) +
                   " effect=" + (enabled ? effect ?? DefaultEffect : "off") + " seMixActive=" + (mixActive ? "1" : "0") +
                   " inRoom=" + (cameraInRoom ? "1" : "0") + " selectEffect=" + calls.ToString(CultureInfo.InvariantCulture) +
                   " muffled=" + muffled.ToString(CultureInfo.InvariantCulture) + (S7Mutants.M19 ? " MUTANT S7.M19 (postfix off)" : "");
        }
    }
}
