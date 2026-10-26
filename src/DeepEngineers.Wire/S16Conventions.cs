using System;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S16 "Depth progression" (ROADMAP §14 "### S16"; docs/S16-BRIEF.md): depth as the progression axis, Subnautica's
 // way. The hull ladder, the pressure hull upgrade blocks of the world mod
 // the block damage below the limit
 // the creak band
 // from 50 m above the limit, the "Hull integrity critical" voice rule, the HUD fields and the C5 extension that carries
 // them to Subnautica. Layout 1.0 is unchanged (hash 0x4f3a35f6fc1eaf31): the extension lives in the C5 SeState pad
 // (0x80-0xA9 of pad_80[0x80], free before S16), version byte 0 = absent. Pinned on its own by the canonical text below
 // (a changed number changes the hash and turns S16ConventionsTests red). Python twin: tools/s16/s16_ref.py (text, hash,
 // rules, vectors in tests/s16/vectors/).
 // =================================================================================================================

 /// <summary>One rung of the hull ladder: the crush depth of a block class on a grid size.</summary>
    public struct S16HullRow
    {
        public int Size, Class, CrushM;
        public S16HullRow(int size, int cls, int crushM) { Size = size; Class = cls; CrushM = crushM; }
    }

    /// <summary>One pressure hull upgrade tier (a world-mod block per grid size: <c>SubtypeBase + "Large" | "Small"</c>).</summary>
    public struct S16TierRow
    {
        public int Tier;
        public string SubtypeBase;
        public int CrushM;
        /// <summary>The planet's ores whose ingots the tier's alloy component is made of (comma-separated, SE ingot subtypes).</summary>
        public string Ingots;
        public S16TierRow(int tier, string subtypeBase, int crushM, string ingots) { Tier = tier; SubtypeBase = subtypeBase; CrushM = crushM; Ingots = ingots; }
    }

    public static class S16Conventions
    {
        public const string Header = "ext s16 depth 11";
        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/> as pinned in CONTRACT §5.4 "S16 conventions".</summary>
        public const ulong PinnedHash = 0xbad78a2237d74b6dUL;
        public const int CanonicalLines = 118, CanonicalBytes = 8516;   // depth 11

 /// <summary>Grid sizes (MyCubeSize: Large = 0, Small = 1 in SE; ours are 1-based and named).</summary>
        public static class Size
        {
            public const int Small = 1, Large = 2;
            public static string Name(int s) { return s == Small ? "Small" : s == Large ? "Large" : "size" + s.ToString(CultureInfo.InvariantCulture); }
        }

        /// <summary>Pressure classes of a block (<c>format block.class</c>).</summary>
        public static class BlockClass
        {
            public const int LightArmor = 1, HeavyArmor = 2, Fitting = 3;
            public static readonly string[] Names = { "", "LightArmor", "HeavyArmor", "Fitting" };
            public static string Name(int c) { return c >= 1 && c <= 3 ? Names[c] : "class" + c.ToString(CultureInfo.InvariantCulture); }
            public static bool IsArmor(int c) { return c == LightArmor || c == HeavyArmor; }
        }

        /// <summary>
 /// The hull ladder (ROADMAP §14 S16 point 1). A Fitting on the pressure boundary takes the class of the armour it
 /// touches, so it has no rung of its own.
 /// </summary>
        public static readonly S16HullRow[] HullTable =
        {

            // grids without upgrades"): 180/300/600/1,000 → 350/500/800/1,200
 // grids must stay at least 250-300 m") and re-rates the tiers above them to the planet: 1,400 / 2,000
            new S16HullRow(Size.Small, BlockClass.LightArmor, 350),
            new S16HullRow(Size.Small, BlockClass.HeavyArmor, 500),
            new S16HullRow(Size.Large, BlockClass.LightArmor, 800),
            new S16HullRow(Size.Large, BlockClass.HeavyArmor, 1200),
        };

        /// <summary>
 /// The upgrade tiers (ROADMAP §14 S16 point 2): the grid's crush depth is raised to CrushM (the best functional one counts).

 /// with the slope ores a 500 m hull collects; Mk2 2,000 m is the planet's floor cap (the sculpt allows no seabed deeper),
 /// paid with the rare ores of 300-1,500 m. The void under the Crater Edge (3,000 m) stays scenery: nothing lives there.
 /// </summary>
        public static readonly S16TierRow[] TierTable =
        {
            new S16TierRow(1, "DE_PressureHullMk1", 1400, "Cobalt,Magnesium,Silver"),
            new S16TierRow(2, "DE_PressureHullMk2", 2000, "Gold,Platinum,Uranium"),
        };

        /// <summary>The alloy component each tier block is paid with (the world mod's Component, made in the assembler from <see cref="S16TierRow.Ingots"/>).</summary>
        public const string AlloyPrefix = "DE_HullAlloyMk";

        public static class Damage
        {
            /// <summary>One evaluation per grid every 60 SE ticks (1 s of game time).</summary>
            public const int PeriodTicks = 60;
            /// <summary>Hull points per second at the limit (milli): 10 hp/s.</summary>
            public const int BaseMilliHpS = 10000;
            /// <summary>Added per metre past the limit (milli): +1 hp/s per m.</summary>
            public const int PerMetreMilliHpS = 1000;
            /// <summary>The cap (milli): 100 hp/s, reached 90 m past the limit.</summary>
            public const int CapMilliHpS = 100000;
            /// <summary>At most this many blocks (the deepest pressure-relevant ones past the limit) per grid per period.</summary>
            public const int MaxBlocksPerPeriod = 8;
            /// <summary>SE integrity per hull point: S6's k_small = 1 and k_large = 25 (CONTRACT §4.1: a light armour cube of the grid size = 100 hp).</summary>
            public const int KSmallPermille = 1000, KLargePermille = 25000;
            /// <summary>The SE damage type (MyStringHash.GetOrCompute; CONTRACT §4.1 names it for the abyss rule).</summary>
            public const string SeType = "DeepPressure";
            /// <summary>No crush damage for the first 60 s after S16 becomes active in a world.</summary>
            public const int GraceTicks = 3600;
        }

        public static class Creak
        {
            /// <summary>Creaks start this far above the limit (ROADMAP §14 S16 point 3).</summary>
            public const int BandM = 50;
            /// <summary>Level 2 from this far above the limit.</summary>
            public const int Level2M = 25;
            /// <summary>Cadence per level in SE ticks: 9 s, 6 s, and past the limit Subnautica's crushPeriod 3 s.</summary>
            public const int PeriodTicks1 = 540, PeriodTicks2 = 360, PeriodTicks3 = 180;
        }

        public static class Voice
        {
            /// <summary>A grid's voice plays at most once per 20 s (the S15 voice rule: once per event, cooldown).</summary>
            public const int CooldownMs = 20000;
            /// <summary>The event is armed again once the grid is this far above its limit.</summary>
            public const int RearmM = 10;
            /// </summary>
            public const int RepeatMs = 20000;
            /// <summary>Subnautica plays the voice at 1.6.</summary>
            public const int VolumePermille = 1600;
            /// <summary>S16's own creaks and damage groans within DuckMs after a voice play at DuckPermille of their volume (the voice was masked).</summary>
            public const int DuckMs = 3500, DuckPermille = 250;
        }

        public static class HudState
        {
            public const int Off = 0, Safe = 1, Creak = 2, Limit = 3, Damage = 4;
            public static readonly string[] Names = { "off", "safe", "creak", "limit", "damage" };
            public static string Name(int s) { return s >= 0 && s <= 4 ? Names[s] : "state" + s.ToString(CultureInfo.InvariantCulture); }
        }

        public static class Hud
        {
            /// <summary>The SE HUD line is refreshed every 30 ticks (2 Hz) and lives 1.5 s.</summary>
            public const int RefreshTicks = 30;
            public const int AliveMs = 1500;
            /// <summary>Below 0.5 m (decimetres) the HUD shows depth; above, it is off.</summary>
            public const int MinDepthDm = 5;
            /// <summary>
 /// put it above the HUD
 /// bar"): the readout's visible content top-centre TopPermille of the height below the top edge (depth 6: the
 /// Seamoth panel is gone); the canvas scale is Subnautica's own
 /// </summary>
            public const int TopPermille = 60, RefWidth = 1920, RefHeight = 1080;
            /// <summary>The readout's caution colour (creaking), 0xRRGGBB; normal and danger are the compass's own textColorNormal/Danger.</summary>
            public const int CautionRgb = 0xFFC040;
            /// <summary>
 /// The seated readout's fit: the text is sized to FitPermille of the compass width, its font
 /// never below MinFontPermille of its own, and the short form "1603 / 1700 m" above SeatedLongDigits digits.
 /// </summary>
            public const int FitPermille = 900, MinFontPermille = 250, SeatedLongDigits = 6;
            /// <summary>The seated readout's centre within this share of the compass width of the half-moon's centre.</summary>
            public const int CentreTolPermille = 50;
            /// <summary>
 /// A readout part wider than W × MaxPartPermille / 1000 or taller than H × MaxPartPermille / 1000 is a backdrop and
 /// the measured bounds were
 /// 5,034 × 2,779 px on a 2,560 × 1,440 screen).
 /// </summary>
            public const int MaxPartPermille = 500;
        }

        /// <summary>
 /// <c>format damage.
 /// Creative world with destructible blocks): the gate is the world's DestructibleBlocks, whatever the game mode.
 /// </summary>
        public static class DamageGate
        {
            public const int None = 0, Destructible = 1, Indestructible = 2, Off = 3;
            public static readonly string[] Names = { "none", "destructible", "indestructible", "off" };
            public static string Name(int g) { return g >= 0 && g <= 3 ? Names[g] : "gate" + g.ToString(CultureInfo.InvariantCulture); }
        }

        /// <summary>
 /// <c>format hud.
 /// the grid line only as the fallback when seated and Subnautica does not show its readouts.
 /// </summary>
 /// <summary><c>format hud.</summary>
        public static class Tone
        {
            public const int Normal = 0, Caution = 1, Danger = 2;
            public static readonly string[] Names = { "normal", "caution", "danger" };
            public static string Name(int t) { return t >= 0 && t <= 2 ? Names[t] : "tone" + t.ToString(CultureInfo.InvariantCulture); }
        }

        public static class SeLine
        {
            public const int None = 0, Grid = 1, Suppressed = 2;
            public static readonly string[] Names = { "none", "grid", "suppressed" };
            public static string Name(int l) { return l >= 0 && l <= 2 ? Names[l] : "line" + l.ToString(CultureInfo.InvariantCulture); }
        }

        /// <summary>The diver keeps Subnautica's rule (S2b, SwimThresholds): no crush depth, breathing ×1.5 below 100 m, ×2 below 200 m.</summary>
        public static class Diver
        {
            public const int UnsafeDepthM = 100, CrushDepthM = 200;
            public const int BreathX10Safe = 10, BreathX10Unsafe = 15, BreathX10Crush = 20;
        }

        /// <summary>The C5 extension (bytes 0x80-0xA9 of SeState, inside layout 1.0's pad_80).</summary>
        public static class C5
        {
            public const int Offset = 0x80, Size = 0x2A, Version = 2;
        }

        public static class C5Flags
        {
            /// <summary>The values describe the local player's grid (seated, or standing inside it).</summary>
            public const int Grid = 1;
            /// <summary>The values describe the diver (no grid; depth = the eye).</summary>
            public const int Diver = 2;
            /// <summary>Crush damage applies in this world (destructible blocks on, whatever the game mode; de.s16 damage not off): <c>format damage.gate</c>.</summary>
            public const int DamageOn = 4;
            /// <summary>The grid is docked (connector or landing gear) to a larger carrier: its crush depth is at least the carrier's.</summary>
            public const int Docked = 8;
            /// <summary>Docked with every block inside the carrier's airtight room: no crush damage.</summary>
            public const int Exempt = 16;
            /// <summary>The 60 s grace after the world load: no crush damage yet.</summary>
            public const int Grace = 32;
        }

        /// <summary>The C7 S16 block (SnStatus 0xBC-0xBF, inside pad_8c after the S8a block 0x8C-0xBB): Subnautica's HUD state.</summary>
        public static class C7
        {
            public const int Offset = 0xBC, Size = 4, Version = 1;
        }

        public static class C7Flags
        {
            /// <summary>Subnautica's uGUI_DepthCompass is found and under S16's control.</summary>
            public const int CompassOk = 1;
            /// <summary>Subnautica's uGUI_SeamothHUD was under S16's control;</summary>
            public const int SeamothOk = 2;
            /// <summary>This frame Subnautica shows the diver's depth meter (a report: SE has no diver line, <c>format hud.se</c>).</summary>
            public const int DiverShown = 4;
            /// <summary>This frame Subnautica shows the vehicle readouts for the seated grid (SE hides its grid line).</summary>
            public const int GridShown = 8;
        }

        /// <summary>The C5 field table (name, offset, size, type), in offset order.</summary>
        public static readonly string[][] C5Fields =
        {
            new[] { "version", "0x80", "1", "u8" },
            new[] { "flags", "0x81", "1", "u8" },
            new[] { "creakLevel", "0x82", "1", "u8" },
            new[] { "hullCode", "0x83", "1", "u8" },
            new[] { "tier", "0x84", "1", "u8" },
            new[] { "breathX10", "0x85", "1", "u8" },
            new[] { "hudState", "0x86", "2", "u16" },
            new[] { "depthM", "0x88", "4", "f32" },
            new[] { "crushM", "0x8c", "4", "f32" },
            new[] { "creakSeq", "0x90", "4", "u32" },
            new[] { "voiceSeq", "0x94", "4", "u32" },
            new[] { "damageSeq", "0x98", "4", "u32" },
            new[] { "worldEpoch", "0x9c", "4", "u32" },
            new[] { "gridId", "0xa0", "8", "i64" },
            new[] { "hullPct", "0xa8", "1", "u8" },
            new[] { "powerPct", "0xa9", "1", "u8" },
        };

        /// <summary>The C7 field table (SnStatus offsets).</summary>
        public static readonly string[][] C7Fields =
        {
            new[] { "c7version", "0xbc", "1", "u8" },
            new[] { "c7flags", "0xbd", "1", "u8" },
            new[] { "c7reserved", "0xbe", "2", "u16" },
        };

        public static bool TryTierRow(int tier, out S16TierRow row)
        {
            foreach (S16TierRow r in TierTable) if (r.Tier == tier) { row = r; return true; }
            row = default(S16TierRow);
            return false;
        }

        // ---------------------------------------------------------------------------------------------- canonical text

        public static readonly string[] FormatLines =
        {
            "block.class HeavyArmor: definition EdgeType Heavy; LightArmor: TypeId CubeBlock and SubtypeId contains Armor (ordinal, ignoring case); every other block (windows, doors, cockpits, vents, connectors) a Fitting; the tier blocks are Fittings that set the tier while ComponentStack.IsFunctional",
            "c5.s16 SeState 0x80-0xa9; version 0 = absent; creakSeq, voiceSeq, damageSeq count events (u32, never reset in an SE session); a reader takes its first value as the baseline and plays one sound per increment seen in the same worldEpoch",
            "c7.s16 SnStatus 0xbc-0xbf written by Subnautica every frame; version 0 = absent; SE hides its grid line while GridShown is set in a fresh record (DiverShown only reports Subnautica's diver depth meter)",
            "crush.depth max(min over counted blocks of hull(size, class), crushM of the best functional tier block or 0); no counted block: hull(size, LightArmor); a static grid (a station) is never tracked nor rated: no crush depth, no damage, seated in its cockpit the diver readout",
            "creak.cadence one creak when the level becomes >= 1 or rises, then one every PeriodTicks(level of the last creak) while the level stays >= 1",
            "creak.level d = the HUD grid's lowest point depth; 0 if d < L - BandM, 1 if d < L - Level2M, 2 if d <= L, 3 if d > L",
            "damage.block every PeriodTicks per grid (first period PeriodTicks + id mod PeriodTicks after it is tracked), none during GraceTicks after S16 becomes active: the MaxBlocksPerPeriod deepest damage-set blocks whose centre depth d > L each take MySlimBlock.DoDamage(min(Cap, Base + PerMetre * (d - L)) * PeriodTicks / 60 * k(size), DeepPressure, sync, no hit info, attacker 0); the damage set is the counted blocks, or every block with a wet face when none counts",
            "exposure a face-neighbour cell outside the block is wet when empty and not in an airtight room (MyGridGasSystem.GetOxygenRoomForCubeGridPosition, IsAirtight), dry when empty and in one; armour counts when a neighbour is wet, a Fitting when one is wet and one dry, as the majority class of the armour cells it touches (HeavyArmor only with more heavy than light cells, else LightArmor)",
            "group.crush L = max(crush.depth, crushM of the best functional tier block of the mechanical group); docked (a Physical or NoContactDamage link to a larger grid outside the mechanical group): L = max(L, the carrier's L), no damage while every block centre lies in an airtight room of the carrier",
            "hud.diver no grid: depth = the eye; breathX10 = BreathX10Crush if d > CrushDepthM, BreathX10Unsafe if d > UnsafeDepthM, else BreathX10Safe; no crush depth for the diver",
            "hud.hull hullPct = ceil(100 * sum(Integrity - AccumulatedDamage) / the largest sum(MaxIntegrity) seen) over the damage set; powerPct = ceil(100 * stored / capacity) over the grid's batteries, 255 without one",
            "hud.se SE's HUD text line: None for the diver (under water Subnautica's compass shows the depth, above water nothing); for a seated grid Grid, Suppressed while Subnautica shows its readouts (GridShown in a fresh C7 record)",
            "hud.sn Subnautica shows its own HUD while the picture is its own (C5 viewState Underwater or Surfacing) and hudState is not Off: uGUI_DepthCompass's player readout (shadow, halfMoon, depthText, suffixText); the diver: depthText = depth, suffixText = MeterSuffix; seated, the Cyclops helm's form depth / crush (CyclopsHelmHUDManager.cs:106-119 '{depth}m / {crush}m') in one field: depthText = depth + ' ' + MeterSuffix + ' / ' + crushM + ' ' + MeterSuffix, or depth + ' / ' + crushM + ' ' + MeterSuffix when the two numbers have more than SeatedLongDigits digits together, crushM <= 0 (no crush depth): depth + ' ' + MeterSuffix only; suffixText off, no word wrapping, overflow, out of any layout group, centred on the compass (|text centre - half-moon centre| <= CentreTolPermille / 1000 of the compass width), its font size measured: min(its own, its own x target / the preferred width at its own), at least MinFontPermille / 1000 of its own, target = the compass width x FitPermille / 1000; it fits when TMP renders one line no wider than the target (live textInfo.lineCount, textBounds after ForceMeshUpdate); its tone Normal for Safe, Caution (CautionRgb) for Creak, Danger (textColorDanger, halfMoonDanger) for Limit and Damage; no Seamoth panel",
            "hud.state Off if d < MinDepthDm / 10; else Damage if d > L and damage applies, Limit if d > L, Creak if d >= L - BandM, Safe",
            "damage.gate crush damage applies when the world's DestructibleBlocks is on, whatever the game mode (Creative included), unless de.s16 damage is off (the dev override): Destructible, Indestructible, Off, None without a session; SE's DoDamage also honours the grid's own destructible flag (MyCubeGrid.BlocksDestructionEnabled)",
            "hud.place the canvas scale is min(W / RefWidth, H / RefHeight) x uiScale; an element is moved so that the bounds of its readout (the shown mode's own fields and the graphics on their parents up to the element; a part wider than W x MaxPartPermille / 1000 or taller than H x MaxPartPermille / 1000 is a backdrop and left out; canvas units, origin the canvas centre) have its top-centre at (0, H/2 - H x TopPermille / 1000), then shifted back inside the canvas; on screen (pixels, top-left origin) x = (u + W/2) x scale, y = (H/2 - v) x scale",
            "voice.mix Subnautica plays the voice at VolumePermille / 1000 and every S16 creak and damage groan that starts within DuckMs after a voice at DuckPermille / 1000 of its own volume",
            "voice.rule one hullCritical event when d > L while armed (disarmed by it, armed again when d < L - RearmM) and one more every RepeatMs while d stays > L; it plays when the same grid played none within CooldownMs",
        };

        private static string _text;
        private static ulong _hash;

        private static void C(StringBuilder sb, string group, string name, long v)
        {
            sb.Append("const ").Append(group).Append(' ').Append(name).Append(' ').Append(v.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        /// <summary>
 /// The canonical text (CONTRACT §5.4 "S16 conventions"; ASCII, '\n' after every line, the last included): the header,
 /// the const groups, the hull and tier rows, the C5 field lines, the format lines sorted by key. The Python twin
 /// writes the same bytes from its own copy of the tables.
 /// </summary>
        public static string CanonicalText()
        {
            if (_text != null) return _text;
            var sb = new StringBuilder(4096);
            sb.Append(Header).Append('\n');
            C(sb, "S16Size", "Small", Size.Small);
            C(sb, "S16Size", "Large", Size.Large);
            for (int c = 1; c <= 3; c++) C(sb, "S16Class", BlockClass.Names[c], c);
            foreach (S16HullRow r in HullTable)
                sb.Append("hull ").Append(Size.Name(r.Size)).Append(' ').Append(BlockClass.Name(r.Class))
                  .Append(" crushM ").Append(r.CrushM.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (S16TierRow t in TierTable)
                sb.Append("tier ").Append(t.Tier.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(t.SubtypeBase)
                  .Append(" crushM ").Append(t.CrushM.ToString(CultureInfo.InvariantCulture)).Append(" ingots ").Append(t.Ingots).Append('\n');
            sb.Append("const S16Alloy ").Append(AlloyPrefix).Append(' ').Append(TierTable.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
            C(sb, "Damage", "PeriodTicks", Damage.PeriodTicks);
            C(sb, "Damage", "BaseMilliHpS", Damage.BaseMilliHpS);
            C(sb, "Damage", "PerMetreMilliHpS", Damage.PerMetreMilliHpS);
            C(sb, "Damage", "CapMilliHpS", Damage.CapMilliHpS);
            C(sb, "Damage", "MaxBlocksPerPeriod", Damage.MaxBlocksPerPeriod);
            C(sb, "Damage", "KSmallPermille", Damage.KSmallPermille);
            C(sb, "Damage", "KLargePermille", Damage.KLargePermille);
            C(sb, "Damage", "GraceTicks", Damage.GraceTicks);
            sb.Append("const Damage SeType ").Append(Damage.SeType).Append('\n');
            C(sb, "Creak", "BandM", Creak.BandM);
            C(sb, "Creak", "Level2M", Creak.Level2M);
            C(sb, "Creak", "PeriodTicks1", Creak.PeriodTicks1);
            C(sb, "Creak", "PeriodTicks2", Creak.PeriodTicks2);
            C(sb, "Creak", "PeriodTicks3", Creak.PeriodTicks3);
            C(sb, "Voice", "CooldownMs", Voice.CooldownMs);
            C(sb, "Voice", "RearmM", Voice.RearmM);
            C(sb, "Voice", "RepeatMs", Voice.RepeatMs);
            C(sb, "Voice", "VolumePermille", Voice.VolumePermille);
            C(sb, "Voice", "DuckMs", Voice.DuckMs);
            C(sb, "Voice", "DuckPermille", Voice.DuckPermille);
            for (int s = 0; s <= 4; s++) C(sb, "HudState", HudState.Names[s], s);
            C(sb, "Hud", "RefreshTicks", Hud.RefreshTicks);
            C(sb, "Hud", "AliveMs", Hud.AliveMs);
            C(sb, "Hud", "MinDepthDm", Hud.MinDepthDm);
            C(sb, "Hud", "TopPermille", Hud.TopPermille);
            C(sb, "Hud", "CautionRgb", Hud.CautionRgb);
            C(sb, "Hud", "FitPermille", Hud.FitPermille);
            C(sb, "Hud", "MinFontPermille", Hud.MinFontPermille);
            C(sb, "Hud", "SeatedLongDigits", Hud.SeatedLongDigits);
            C(sb, "Hud", "CentreTolPermille", Hud.CentreTolPermille);
            C(sb, "Hud", "RefWidth", Hud.RefWidth);
            C(sb, "Hud", "RefHeight", Hud.RefHeight);
            C(sb, "Hud", "MaxPartPermille", Hud.MaxPartPermille);
            for (int g = 0; g <= 3; g++) C(sb, "DamageGate", DamageGate.Names[g], g);
            for (int l = 0; l <= 2; l++) C(sb, "SeLine", SeLine.Names[l], l);
            for (int t = 0; t <= 2; t++) C(sb, "Tone", Tone.Names[t], t);
            C(sb, "Diver", "UnsafeDepthM", Diver.UnsafeDepthM);
            C(sb, "Diver", "CrushDepthM", Diver.CrushDepthM);
            C(sb, "Diver", "BreathX10Safe", Diver.BreathX10Safe);
            C(sb, "Diver", "BreathX10Unsafe", Diver.BreathX10Unsafe);
            C(sb, "Diver", "BreathX10Crush", Diver.BreathX10Crush);
            C(sb, "C5", "Offset", C5.Offset);
            C(sb, "C5", "Size", C5.Size);
            C(sb, "C5", "Version", C5.Version);
            C(sb, "C5Flags", "Grid", C5Flags.Grid);
            C(sb, "C5Flags", "Diver", C5Flags.Diver);
            C(sb, "C5Flags", "DamageOn", C5Flags.DamageOn);
            C(sb, "C5Flags", "Docked", C5Flags.Docked);
            C(sb, "C5Flags", "Exempt", C5Flags.Exempt);
            C(sb, "C5Flags", "Grace", C5Flags.Grace);
            foreach (string[] f in C5Fields)
                sb.Append("field s16 ").Append(f[0]).Append(' ').Append(f[1]).Append(' ').Append(f[2]).Append(' ').Append(f[3]).Append('\n');
            C(sb, "C7", "Offset", C7.Offset);
            C(sb, "C7", "Size", C7.Size);
            C(sb, "C7", "Version", C7.Version);
            C(sb, "C7Flags", "CompassOk", C7Flags.CompassOk);
            C(sb, "C7Flags", "SeamothOk", C7Flags.SeamothOk);
            C(sb, "C7Flags", "DiverShown", C7Flags.DiverShown);
            C(sb, "C7Flags", "GridShown", C7Flags.GridShown);
            foreach (string[] f in C7Fields)
                sb.Append("field s16 ").Append(f[0]).Append(' ').Append(f[1]).Append(' ').Append(f[2]).Append(' ').Append(f[3]).Append('\n');
            var formats = (string[])FormatLines.Clone();
            Array.Sort(formats, string.CompareOrdinal);
            foreach (string f in formats) sb.Append("format ").Append(f).Append('\n');
            _text = sb.ToString();
            return _text;
        }

        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/>.</summary>
        public static ulong Hash()
        {
            if (_hash == 0) _hash = LayoutHash.Fnv1a64Ascii(CanonicalText());
            return _hash;
        }
    }
}
