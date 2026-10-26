using System;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S15 "Gameplay and immersion polish" (ROADMAP §14 "### S15" and its addenda; docs/S15-BRIEF.md; CONTRACT §5.4 "S15
 // conventions"). Layout 1.0 and every earlier pin unchanged: the new values live in unpinned space of layout 1.0.

 // the Seamoth's welcome (a small grid), engine powering up / shutting down on the grid's power state; SE decides them
 // with the S15 gate over S16's voice rule and counts them in the C5 SeState pad at 0xAC-0xCB (S16 holds 0x80-0xA9);
 // Subnautica plays the Cyclops's and the Seamoth's own lines (one per increment, S16SeqReader) through S16's voice mix.
 // - Subnautica's audio across the surface (its options VCAs: the music and the voice stay live above water) and its
 // story off (story goals skipped, the Aurora's countdown and explosion held off).
 // - C, SE's planets in Subnautica's sky: a slow body list and a per-tick sky frame in C6's region space past the 64-byte
 // SeTime record (+0x100 the frame, +0x200 the list; each with its own seqlock word), so Subnautica computes every
 // body's direction and size itself and draws one disc per body after hiding its own two sky bodies.
 // - B, one planet from orbit to the seabed: the colour targets (Subnautica's measured palette, Wire S7tBandTargets) and
 // the world mod's own water material, pinned here; the analyzer measures the pictures against them.
 // The canonical text below is pinned (FNV-1a 64); the Python twin is tools/s15/s15_ref.py (same text, same hash, the
 // gate, the sky math and both codecs; --selftest, --vectors into tests/s15/vectors/).
 // =================================================================================================================

    public static class S15Conventions
    {
        public const string Header = "ext s15 voice 5";
        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/> as pinned in CONTRACT §5.4 "S15 conventions".</summary>
        public const ulong PinnedHash = 0xd5d5c7fdeeece0ecUL;
        public const int CanonicalLines = 126, CanonicalBytes = 16636;

        /// <summary>
 /// The voice events (format voice.gate
 /// the ids are the C5 counters' order.
 /// </summary>
        public static class Event
        {
            public const int Welcome = 1, EnginePowerUp = 2, EnginePowerDown = 3, SmallWelcome = 4;
            /// <summary>The C5 counters (all four are SE's and travel in C5).</summary>
            public const int C5Count = 4, Count = 4;
        }

        /// <summary>One row of the voice table: the event, its gate, the line Subnautica plays.</summary>
        public sealed class VoiceRow
        {
            public readonly int Id;
            public readonly string Name;
            public readonly int RearmMs, CooldownMs;
            /// </summary>
            public readonly string Member, Source;
            /// <summary>The prefab the line comes from: "cyclops" (LightmappedPrefabs scene prefab) or "seamoth" (TechType.Seamoth).</summary>
            public readonly string Prefab;
            public readonly string Owner;
            /// <summary>The event's condition in words (formats voice.welcome, voice.power).</summary>
            public readonly string When;

            public VoiceRow(int id, string name, int rearmMs, int cooldownMs, string owner, string member, string source, string prefab, string when)
            {
                Id = id; Name = name; RearmMs = rearmMs; CooldownMs = cooldownMs; Owner = owner; Member = member; Source = source; Prefab = prefab; When = when;
            }
        }

        /// <summary>
 /// Index = id - 1. No
 /// once-per-session rule; each line once per transition with a short cooldown. The FMOD paths are prefab data
 /// Subnautica logs each resolved path once.
 /// </summary>
        public static readonly VoiceRow[] Voices =
        {
            new VoiceRow(Event.Welcome, "Welcome", 5000, 120000, "SubRoot", "welcomeNotification", "SubRoot.cs:64", "cyclops", "boarding a large grid or a station"),
            new VoiceRow(Event.EnginePowerUp, "EnginePowerUp", 2000, 10000, "SubRoot", "enginePowerUpNotification", "SubRoot.cs:98", "cyclops", "the aboard grid's power from none to some"),
            new VoiceRow(Event.EnginePowerDown, "EnginePowerDown", 2000, 10000, "SubRoot", "enginePowerDownNotification", "SubRoot.cs:100", "cyclops", "the aboard grid's power switch off"),
            new VoiceRow(Event.SmallWelcome, "SmallWelcome", 5000, 120000, "Vehicle", "welcomeNotification", "Vehicle.cs:105", "seamoth", "boarding a small grid"),
        };

        public static VoiceRow VoiceOf(int id) { return id >= 1 && id <= Voices.Length ? Voices[id - 1] : null; }

        public static class Voice
        {
            /// <summary>The aboard condition must hold this long before it counts (walking through a room, a seat by mistake).</summary>
            public const int AboardMs = 1000;
            /// <summary>SE samples the conditions every this many ticks.</summary>
            public const int ScanTicks = 10;
            /// <summary>Subnautica's audio is live (the S2 rule) when C5 snBusTarget is at least this (per mille).</summary>
            public const int SnBusLivePermille = 500;
            /// <summary>One line at a time on Subnautica: a line waits this long after the last S15 or S16 voice (= S16's DuckMs).</summary>
            public const int QueueGapMs = S16Conventions.Voice.DuckMs;
            public const int QueueMax = 2;
        }

        /// <summary>The C5 extension (SeState pad bytes 0xAC-0xCB, after S16's 0x80-0xA9); version 2 since the four-line set.</summary>
        public static class C5
        {
            public const int Offset = 0xAC, Size = 0x20, Version = 2;
        }

        public static class C5Flags
        {
            /// <summary>Aboard (seat, sealed room or station), Seated, Room, Powered (the aboard grid's electricity not NoPower), Large (a large grid or a station), Station.</summary>
            public const int Aboard = 1, Seated = 2, Room = 4, Powered = 8, Large = 16, Station = 32;
        }

        /// <summary>
 /// The events block in C6's region space (format c6.s15events), its own seqlock word at offset 0; version 2 carries the
 /// sonar only (the picks' voice events are gone): 0x04 u8 version, 0x05 u8 flags (<see cref="EventFlags"/>), 0x06 u16
 /// count 0, 0x08 u32 worldEpoch, 0x0C u32 sonarSeq; 0x10-0x3F zero.
 /// </summary>
        public static class Events
        {
            public const int Offset = 0x700, Size = 0x40, Version = 2;
        }

        public static class EventFlags
        {
            public const int SonarOn = 1;
        }

        /// <summary>The kept non-voice picks' numbers (formats night.lights, lens.splash, sonar.ping, seabase.room, fish.scare).</summary>
        public static class Picks
        {
            /// <summary>The block lights at night: this / 1000 of their day intensity (DayNightCycle.GetLightScalar 0).</summary>
            public const int NightLightPermille = 400;
            /// <summary>The lens splash after the camera was under the sea at least this long.</summary>
            public const int SplashMinUnderMs = 1000;
            /// <summary>The sonar's period.</summary>
            public const int SonarPeriodMs = 5000;
            /// <summary>The seabase light: intensity (per mille) and range (decimetres); colour <see cref="SeabaseLight"/>.</summary>
            public const int SeabaseLightPermille = 600, SeabaseLightRangeDm = 80;
            /// <summary>The fish scare: a proxy section faster than this (dm/s) scares Scareable fish within FishScareRangeM by FishScarePermille / 1000.</summary>
            public const int FishScareSpeedDms = 40, FishScareRangeM = 15, FishScarePermille = 1000, FishScanMs = 1000;
            /// <summary>A section faster than this (dm/s) jumped (a teleport, a re-pose: run 20261024-041144 logged 3088 m/s): no scare.</summary>
            public const int FishJumpSpeedDms = 1000;
            /// <summary>The block whose CustomName holds this (any case) is the sonar switch.</summary>
            public const string SonarTag = "sonar";
            public static readonly BandRgb SeabaseLight = new BandRgb(255, 240, 220);
        }

        /// <summary>
 /// Subnautica's audio groups across the surface (format audio.groups
 /// music, when going above the water"): the game's own VCAs, the effects and ambience
 /// following the S2 rule as before, the music and the voice live on both sides, at these levels above water.
 /// </summary>
        public static class Mix
        {
            /// <summary>The music and the voice groups above water (per mille of the player's own option volume); tunable live with de.s15 music / voiceAbove.</summary>
            public const int MusicAbovePermille = 600, VoiceAbovePermille = 600;
            /// <summary>The music and voice groups' crossfade at the switch (the effects keep C5's crossfadeMs, 300).</summary>
            public const int CrossfadeMs = 2000;
            /// <summary>The s15.mix rows: every this while a group fades, else at each change and every 5 s.</summary>
            public const int RowMs = 250;
            /// <summary>The VCAs (Subnautica's options sliders): ambient/effects, music, voice.</summary>
            public const string SfxVca = "634d9305-7695-40df-999a-cc1d049e1ebe", MusicVca = "0d0dd1e2-9d46-4a5b-aae8-ae412d827831", VoiceVca = "3c4e7775-5fbe-4b00-83e0-35bc9add106a";
            public const string VcaSource = "SoundSystem.cs:57-59";
        }

        /// <summary>
 /// Subnautica's story off (format story.off
 /// Subnautica's story is not played): every story goal
 /// skipped, and the Aurora's countdown and explosion
 /// held off by moving its warning and countdown times this far (s of DayNightCycle.timePassed).
 /// </summary>
        public static class Story
        {
            public const int AuroraNeverS = 1000000000;
        }

        /// <summary>The C6 region space past SeTime: the sky frame (+0x100) and the body list (+0x200), each with its own seqlock.</summary>
        public static class Sky
        {
            public const int FrameOffset = 0x100, FrameSize = 0x60;
            public const int ListOffset = 0x200, ListHeader = 0x20, RowSize = 0x40, MaxBodies = 16, NameMax = 28;
            public const int Version = 1;
            /// <summary>SE rewrites the list this often even when it did not change (a new section, a restarted reader).</summary>
            public const int ListRefreshTicks = 300;
            /// <summary>The disc's distance as a fraction of the camera's far plane (per mille).</summary>
            public const int DiscDistancePermille = 900;
            public const int DiscSegments = 48, DiscRings = 6;
            /// <summary>The dark side's floor and the limb blend start (fraction of the disc radius), per mille.</summary>
            public const int AmbientPermille = 60, LimbPermille = 850;
            /// <summary>The disc's opacity by day (the sky's light over it) and at night, per mille.</summary>
            public const int DayAlphaPermille = 850, NightAlphaPermille = 1000;
            public const int RenderQueue = 2600;
            /// <summary>Both sides log the sky rows on SE ticks that are multiples of this.</summary>
            public const int RowTicks = 60;
        }

        public static class SkyFlags
        {
            public const int Valid = 1;
        }

        public static class BodyFlags
        {
            public const int Bound = 1, Atmosphere = 2;
        }

        /// <summary>The oracle tolerances (ROADMAP S15 addendum 2: angle at most 1 degree, size within 10 %).</summary>
        public static class Oracle
        {
            public const int AngleCentiDeg = 100, SizePermille = 100;
            /// <summary>A sky row counts when the SE camera turned less than this since the previous tick.</summary>
            public const int StillCentiDeg = 5;
        }

        /// <summary>Subnautica's two sky bodies off (format moons.hide).</summary>
        public static class Moons
        {
            /// <summary>uSkyManager.planetRadius while hidden, in micrometres (0.001 m: never hit by the sky shader's ray).</summary>
            public const int PlanetRadiusMicro = 1000;
            public const int TextureSize = 4;
        }

        /// <summary>The colour tolerances of the B rows (T5's, compare.py bands: per channel and Rec. 709 luma on sRGB bytes).</summary>
        public static class Colour
        {
            public const int TolChannel = 24, TolLuma = 16;
            /// <summary>The disc's centre patch and limb ring, per mille of its radius.</summary>
            public const int CentrePermille = 400, LimbInnerPermille = 850, LimbOuterPermille = 970;
            /// <summary>The band captures: SE above the band enter altitude, Subnautica below it, metres.</summary>
            public const int BandOffsetM = 50;
            /// <summary>
 /// The from-the-air rows (format air.colour): the face is the frame's central circle of this radius (per mille of the
 /// frame height); a pixel is sea when blue or green leads red by SeaLeadMin (SE's opaque sea is teal: run 20261024-003024), cloud when its luma is at least
 /// CloudLumaMin, seabed otherwise
 /// 85 % seabed with Transparent on).
 /// </summary>
            public const int FacePermille = 300, SeaLeadMin = 25, CloudLumaMin = 200, SeabedMaxPermille = 100;
            /// <summary>
 /// The sea's hue (formats air.colour, orbit.colour): each of r, g, b / (r + g + b) within this per mille of the row's
 /// (Subnautica's near water is 27 off the ending's, the Water Mod's JWater2 seen from 40 km 64).
 /// </summary>
            public const int SeaChromaPermille = 30;
        }

        /// <summary>The from-the-air captures (format air.</summary>
        public static readonly int[] AirKm = { 30, 5 };

        /// <summary>

 /// tools/s15/measure_ending.py, palette only (the frames stay outside the repository): the sea (navy to teal), the clouds
 /// (warm white cumulus) and the share of the face they cover, the limb (the face's haze-softened edge), the atmosphere's
 /// rim (warm orange-pink toward the sun, pale blue elsewhere). A low sun: the orbit and air rows take the sea's hue and a
 /// brightness from the ending's to Subnautica's near water's (format ending.palette).
 /// </summary>
        public static class Ending
        {
            public static readonly BandRgb Sea = new BandRgb(34, 59, 75), Cloud = new BandRgb(120, 114, 110), Limb = new BandRgb(66, 69, 77),
                                           RimWarm = new BandRgb(164, 135, 121), RimCool = new BandRgb(82, 85, 94);
            public const int CoverPermille = 127;
        }

        /// <summary>One flat palette of a sky body (sRGB; never SE's textures).</summary>
        public sealed class PaletteRow
        {
            public readonly int Id;
            public readonly string Prefix;
            public readonly BandRgb Rgb, Limb;
            public PaletteRow(int id, string prefix, byte r, byte g, byte b, byte lr, byte lg, byte lb)
            {
                Id = id; Prefix = prefix; Rgb = new BandRgb(r, g, b); Limb = new BandRgb(lr, lg, lb);
            }
        }

        /// <summary>
 /// The palette by the planet generator's subtype prefix (case-insensitive, first match; 0 = any other). DE_4546B is
 /// Subnautica's measured sea and horizon (S7tBandTargets near water and horizon haze); the others are flat colours
 /// chosen for SE's stock planets' look (our numbers, no texture).
 /// </summary>
        public static readonly PaletteRow[] Palettes =
        {
            new PaletteRow(0, "*", 128, 128, 128, 170, 180, 200),
            new PaletteRow(1, "DE_4546B", 65, 97, 122, 197, 241, 255),
            new PaletteRow(2, "EarthLike", 70, 110, 160, 140, 190, 255),
            new PaletteRow(3, "Moon", 150, 150, 150, 150, 150, 150),
            new PaletteRow(4, "Mars", 170, 100, 70, 210, 160, 130),
            new PaletteRow(5, "Europa", 200, 215, 230, 220, 235, 255),
            new PaletteRow(6, "Alien", 110, 130, 90, 170, 220, 160),
            new PaletteRow(7, "Titan", 200, 170, 120, 230, 200, 150),
            new PaletteRow(8, "Triton", 180, 200, 215, 220, 230, 240),
            new PaletteRow(9, "Pertam", 190, 140, 90, 230, 190, 140),
        };

        /// <summary>The from-orbit targets (ROADMAP S15 addendum 1: 600, 200, 100 km): the ending's sea and limb (<see cref="Ending"/>).</summary>
        public sealed class OrbitRow
        {
            public readonly int Km;
            public readonly BandRgb Centre, Limb;
            public OrbitRow(int km, BandRgb centre, BandRgb limb) { Km = km; Centre = centre; Limb = limb; }
        }

        public static readonly OrbitRow[] Orbit =
        {
            new OrbitRow(600, Ending.Sea, Ending.Limb),
            new OrbitRow(200, Ending.Sea, Ending.Limb),
            new OrbitRow(100, Ending.Sea, Ending.Limb),
        };

        /// <summary>
 /// The far sea (format water.far): the Water Mod has one surface material per planet and no distance term
 /// so the SE plugin swaps the planet's texture to this one
 /// when its camera rises above SwapUpM over the sea and back below SwapDownM (WaterLook: the Water Mod's settings and a
 /// LOD rebuild).
 /// noon 30 km gain of run 20261024-054404. SeaMaterial: a copy of the Water Mod's own "Water" MaterialConfig with
 /// Reflectivity 0.5, the planet's default since run 20261024-063156 (the near water at the switch: dMax 34 to 15, dL 13 to
 /// 3); PlainMaterial: the Water Mod's own, selectable for a session (de.water material=Water) and the band's information pair.
 /// </summary>
        public static class WaterFar
        {
            public const string Material = "DE_4546B_WaterFar";
            public const string Texture = "Textures\\DE_4546B_WaterFar.dds";
            public const int MeanR = 62, MeanG = 96, MeanB = 122;
            public const int SwapUpM = 20000, SwapDownM = 15000;
            public const string SeaMaterial = "DE_4546B_Sea";
            public const string SeaReflectivity = "0.5";
            public const string PlainMaterial = "Water";
        }

        /// <summary>The world mod's water material (format water.material; tools/s15/gen_water_texture.py).</summary>
        public static class Water
        {
            public const string Material = "DE_4546B_Water";
            public const string Texture = "Textures\\DE_4546B_Water.dds";
            public const int Size = 256;
            /// <summary>The near texture's mean sRGB: Subnautica's near water at the switch through the noon gain of run 20261024-054404.</summary>
            public const int MeanR = 110, MeanG = 156, MeanB = 184;
            /// <summary>
 /// The Water Mod's PlanetConfig.DistantSoftnessOffset for DE_4546B (its default 0.003): the soft-particle fade of its
 /// surface is min((altitude / 2000)^2, Radius x this) x 5 m, 900 m from
 /// 27 km up with the default, so the seabed under the camera showed through from 30 km (run 20261024-003024); with
 /// this it stays the mod's own up to 4.9 km and 30 m above (the 5 km look, opaque). Read at every world load.
 /// </summary>
            public const string DistantSoftnessOffset = "0.0001";
            /// <summary>
 /// The Water Mod's PlanetConfig.SpecularIntensity for DE_4546B (its default 1; the plugin's look defaults had 0.8). The mod
 /// adds pow(dot(n, h), Specularity) x this to a surface quad only where sunDot &gt; 0
 /// its half vector h built from the camera's planet-local position minus world quad corners, so h is a
 /// fixed direction set by the planet's world position, not a glint, and the term switches on at the terminator: a step
 /// along the quad edges (the look sheet 20261024-124136, 5 and 30 km: 44 and 32 luma; Math.Pow of a negative dot with
 /// the even exponent 4 is positive). Off for the planet; the terminator is then the mod's continuous
 /// max(sunDot, AmbientColorIntensity). Read at every world load.
 /// </summary>
            public const string SpecularIntensity = "0";
            /// </summary>
            public const string Previous = "JWater2";
            /// <summary>
 /// much better"): off, so the sea is
 /// opaque from the air
 /// </summary>
            public const bool Transparent = false;
        }

        public static readonly string[] FormatLines =
        {
            "c6.s15events C6 region +0x700, its own seqlock, written with every C6 while S15 is active: version 2, flags (SonarOn: the sonar switch on aboard), count 0, worldEpoch, the sonar ping counter (read the c5.s15 way); the rest zero",
            "aboard the aboard grid is the grid of the cockpit the local character sits in (MyCharacter.UsingEntity as MyCockpit), else the grid whose airtight room (the S7 air rule) holds the character's head, else (for voice.power only) a station grid (IsStatic) whose world box holds the head: a base or a house; it counts after AboardMs; a door, a ladder or an open deck of a ship never counts",
            "voice.welcome Welcome while the aboard grid is a large grid or a station (MyCubeGrid.GridSizeEnum Large or IsStatic) and the player is in its seat or a sealed room of it; SmallWelcome the same for a small dynamic grid (the Seamoth's own boarding line, Vehicle.welcomeNotification of the Seamoth prefab, else its Vehicle.enterSound); the rising edge is the boarding itself, so a dive never greets",
            "voice.power EnginePowerUp while the aboard grid is powered: its resource distributor's electricity state (MyResourceDistributorComponent.ResourceStateByType without a grid: the grid's whole network, read without a recompute) is not NoPower, so the edge is the grid's power going from none to some (a reactor placed or fuelled with uranium, the power switch on, any source); EnginePowerDown while the grid's power switch is off (MyCubeGrid.IsPowerSwitchOn false: SE's Y key, MyShipController.SwitchReactorsLocal) after it went off from a powered grid; never thrust or thruster strength",
            "voice.gate per (event, grid): a rising edge of the event's condition after it was off RearmMs is an event; S16VoiceRule (one per event, CooldownMs) decides; an event met while SE's audio is live (C5 snBusTarget < SnBusLivePermille / 1000) is skipped: not sent, no cooldown started, never held or replayed (owner 2026-10-24: no line on a dive); a grid that becomes aboard takes its power state and its switch as the baseline (boarding a powered grid is no EnginePowerUp, a switched-off one no EnginePowerDown); no once-per-session rule",
            "c5.s15 SeState 0xac-0xcb, version 2 (0 = absent): flags (Aboard, Seated, Room, Powered, Large, Station), worldEpoch, the counters of events 1-4 (u32, never reset in an SE session), the aboard grid; a reader takes its first value as the baseline and plays one line per increment seen in the same worldEpoch (S16SeqReader)",
            "voice.mix Subnautica plays each line (SubRoot.<member>.sound of the Cyclops prefab, Vehicle.welcomeNotification.sound of the Seamoth prefab) as an FMOD instance at the camera at S16VoiceMix.VoiceVolume, never started while its C5 snBusTarget says SE's audio is live; one voice at a time both ways on one shared clock: an S15 line waits QueueGapMs after the last S15 or S16 voice, S16's hull voice waits QueueGapMs after an S15 line, S16's creaks and groans duck under both; at most QueueMax lines waiting (a new line beyond that is dropped, a welcome replaces the newest other line)",
            "voice.end a line started under water plays to its end above water: the voice group stays live across the surface (audio.groups); each played line is followed to its end (its playback state, timeline position and length) with the voice group's lowest level and the master mute during it",
            "audio.groups Subnautica's mix in groups (its options VCAs by GUID, SoundSystem.cs:57-59; the player's own slider value kept as the factor): the effects and ambience group follows the S2 rule (C5 snBusTarget over crossfadeMs: 0 above water), the music and voice groups go to Mix.MusicAbovePermille and Mix.VoiceAbovePermille / 1000 above water and back to 1 under it over Mix.CrossfadeMs; bus:/ stays at 1 and unmuted above water (still muted with no live SE, SE not in front, quitting); without the VCAs the S2 rule's old bus:/ fade and mute",
            "story.off Subnautica's story goals are skipped (StoryGoal.Execute, StoryGoal.cs:31: no PDA log line, no radio message, no databank entry, no goal completion); every PDA voice request (SoundQueue.Play) is logged; the system lines DeepEngineers keeps (the Cyclops and Seamoth set, the sonar, Subnautica's own oxygen alert) are not story goals",
            "aurora.off the Aurora's countdown and explosion never start (CrashedShipExploder: its warning and countdown times moved to Story.AuroraNeverS before each Update, so AuroraWarnings and the countdown sound, explosion, camera shake and radius damage never trigger); a save whose Aurora already exploded keeps its model and its Update is skipped (no periodic camera shake or rumble)",
            "night.lights the mirrored block lights (S7's kinds BlockPoint and BlockSpot) at NightLightPermille / 1000 + (1 - NightLightPermille / 1000) x DayNightCycle.GetLightScalar() of their intensity; the character's lamps untouched",
            "lens.splash Subnautica's LensWater.CreateSplash on the main camera when the camera rises through the sea surface after SplashMinUnderMs under it",
            "sonar.ping a functional block of the aboard grid whose CustomName holds SonarTag (any case) and is on, while seated, pings every SonarPeriodMs: SE counts it in the C6 events block; Subnautica plays the Cyclops's sonar sound (CyclopsSonarButton.soundFX's asset) and SNCameraRoot.SonarPing (the SonarScreenFX pulse), only with its audio live",
            "seabase.room the camera in an airtight room under the sea (S7's inside flag): the FMODGameParams loops of state OnlyInBase play (their state set to OnlyInside, which S7's IsInsideWalkable answers) and one point light of SeabaseLight colour, SeabaseLightPermille / 1000 intensity and SeabaseLightRangeDm / 10 m range follows the camera; both restored when it leaves",
            "fish.scare a ship's proxy section (S6) faster than FishScareSpeedDms / 10 m/s (and not over FishJumpSpeedDms / 10: a jump) scares the small fish (a Scareable with a FleeWhenScared) within FishScareRangeM of it: Creature.Scared + FishScarePermille / 1000, CreatureFear.SetScarePosition at the section, the flee action started; checked every FishScanMs",
            "air.colour SE's picture from a spectator over the site looking down at each air row's altitude (de.s15 orbit, SE's own screenshot): the face (the frame's central FacePermille circle) has at most SeabedMaxPermille seabed pixels (neither sea: the larger of green and blue ahead of red by SeaLeadMin, nor cloud: luma CloudLumaMin) and, for a row at or above WaterFar.SwapUpM (the far sea), its sea pixels' mean has the row's hue (each of r, g, b / (r + g + b) within SeaChromaPermille / 1000) and a luma from the row's less TolLuma to Subnautica's near water's plus TolLuma (the ending's low sun to the noon over the site); below it (the near sea, tuned for the switch) the hue is information",
            "water.far the far sea: above WaterFar.SwapUpM over the sea the SE plugin sets the planet's texture to WaterFar.Material and back to Water.Material below WaterFar.SwapDownM (once per crossing, a row each), never under S15.M4 or with the water switch off; the near texture is tuned for the switch (Subnautica's near water), the far one for the ending's hue",
            "band.colour the four bands of compare.py bands (sky_top, sky_horizon, far_water, near_water) of SE's picture at band enter + BandOffsetM against Subnautica's at band enter - BandOffsetM, same heading: sky_top, sky_horizon and near_water each within TolChannel per channel and TolLuma, far_water by band.far; the pair with the Water Mod's own material (WaterFar.PlainMaterial) is information",
            "band.far SE's far_water within TolChannel per channel and TolLuma of the haze line: the segment from Subnautica's far_water to its sky_horizon in the same pair (its nearest point in RGB; the luma against that point's). SE's atmosphere carries the far sea to its horizon colour within a few km (run 20261024-063156, luma: (far - near) / (horizon - near) 0.98, Subnautica's water fog 0.15) and no Water Mod value moves it (its billboards take Reflectivity x max(fresnel, 0.1), the light, the specular and the texture, no fog above water; Reflectivity 0.925 to 0.5 moved the far luma by -4, the near by +15), so any point of that line is either game's far sea; a far sea off it (teal, dark, land) fails",
            "c6.s15frame C6 region +0x100, its own seqlock, written with every C6: tick, listSeq, camRel = P - C (SE world, f64), the rows of mapdir at P for the current chart (SN = M x SE, f32); a reader takes the seq it finds first as a baseline (fresh only after a change, 3 s) and the frame's worldEpoch must be C5's",
            "c6.s15list C6 region +0x200, its own seqlock: count <= MaxBodies, nearest the bound planet first: centre - C (SE world, f64), AverageRadius, AtmosphereRadius, palette, flags, ASCII name <= NameMax; written when the list changes (listSeq + 1) and every ListRefreshTicks",
            "moons.hide Subnautica's two sky bodies off: uSkyManager.planetRadius PlanetRadiusMicro with the planet held opposite the sun (planetOrbitSpeed 0, planetZenith from the sun: no eclipse), the planet's and the moon's coronas rgb 0 (alpha kept), MoonTexture transparent; the constant properties sent again to the skybox, skymap, fog and water materials; everything restored when the sky is off; judged on night captures of Subnautica's picture along both signs of MoonLight's axis (the centre no brighter than the sky around it by TolLuma)",
            "orbit.colour SE's picture from a spectator over the site looking at the planet centre at each orbit row's altitude: the disc's inner CentrePermille radius (centre) judged as air.colour's face against the row's centre; information: its LimbInnerPermille-LimbOuterPermille ring against the row's limb, the rim beyond the disc against the ending's warm and cool rim, the centre's cloud share against the ending's cover (SE draws the Water Mod's surface after its atmosphere and cloud layers, MyTransparentRendering.cs:193-213, so neither haze nor clouds can cover the sea)",
            "ending.palette Subnautica's ending from orbit (the owner's frames, tools/s15/measure_ending.py, palette only): the sea, the clouds and their cover of the face, the limb, the rim warm and cool; the orbit and air rows' targets, the water texture's hue and the planet definition's atmosphere and clouds",
            "sky.dir each body's direction in the camera frame (right, up, forward): SE from its camera matrix and the body's centre, Subnautica from Unity's camera transform and the disc's position; the angle between them <= AngleCentiDeg / 100 degrees on the same SE tick while the SE camera is still",
            "sky.disc a camera-facing disc per body except the bound planet at DiscDistancePermille of the camera's far plane, radius D tan(alpha); vertex colours the palette lit by uSkyManager.SunDir as a sphere (floor AmbientPermille), the limb blended to the palette's limb colour past LimbPermille of the radius for a body with an atmosphere; Sprites/Default at RenderQueue, no depth write; shown while the camera is above the sea",
            "sky.size each body's angular radius asin(AverageRadius / distance): Subnautica's disc (atan(radius / distance) of its transform) within SizePermille / 1000 of SE's (the transport of the list and the frame); measured in pixels on a capture of each picture aimed at the body (atan(r px / focal px) from the vertical field of view) as information",
            "water.material DE_4546B's sea uses the world mod's TransparentMaterial Material (Texture, generated, its mean sRGB pinned: the ending's sea hue at the near water's brightness through the band pair's gains) with the Water Mod's Transparent off, its MaterialId WaterFar.SeaMaterial (its own Water material with Reflectivity WaterFar.SeaReflectivity: the near water at the switch, run 20261024-063156), its DistantSoftnessOffset for the planet (the surface's soft fade capped at 30 m above 4.9 km) and its SpecularIntensity Water.SpecularIntensity (the mod adds a specular term to a quad only where its sunDot > 0, with a half vector mixing planet-local and world space: a step along the quad edges at the sea's terminator, the seam of the look sheet 20261024-124136); Data/WaterConfig.xml sets the five for new worlds (the softness and the specular at every load; the plugin's look defaults keep the specular at Water.SpecularIntensity), the SE plugin through de.water texture, transparent and material once per world load for an existing world (when MyTransparentMaterials.ContainsMaterial), every 10 s until the three read back, never again after that (the owner's /wtransparent and de.water material=WaterFar.PlainMaterial hold for the session); judged on captures (air.colour, orbit.colour), the read-back is information",
        };

        private static string _text;
        private static ulong _hash;

        private static void C(StringBuilder sb, string group, string name, int v)
        {
            sb.Append("const ").Append(group).Append(' ').Append(name).Append(' ').Append(v.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        private static string Rgb(BandRgb c)
        {
            return c.R.ToString(CultureInfo.InvariantCulture) + "," + c.G.ToString(CultureInfo.InvariantCulture) + "," + c.B.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
 /// The canonical text (CONTRACT §5.4 "S15 conventions"; ASCII, '\n' after every line, the last included): the header,
 /// the voice rows and the const groups, the palette, orbit, ending and air rows, the mix's VCAs, the sonar, seabase and
 /// water rows, the format lines sorted ordinally.
 /// </summary>
        public static string CanonicalText()
        {
            if (_text != null) return _text;
            var sb = new StringBuilder(8192);
            sb.Append(Header).Append('\n');
            foreach (VoiceRow v in Voices)
                sb.Append("voice ").Append(v.Id.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(v.Name)
                  .Append(" key grid rearmMs ").Append(v.RearmMs.ToString(CultureInfo.InvariantCulture))
                  .Append(" cooldownMs ").Append(v.CooldownMs.ToString(CultureInfo.InvariantCulture))
                  .Append(" member ").Append(v.Owner).Append('.').Append(v.Member).Append(' ').Append(v.Source)
                  .Append(" prefab ").Append(v.Prefab).Append(" when ").Append(v.When).Append('\n');
            C(sb, "Voice", "AboardMs", Voice.AboardMs);
            C(sb, "Voice", "ScanTicks", Voice.ScanTicks);
            C(sb, "Voice", "SnBusLivePermille", Voice.SnBusLivePermille);
            C(sb, "Voice", "QueueGapMs", Voice.QueueGapMs);
            C(sb, "Voice", "QueueMax", Voice.QueueMax);
            C(sb, "Voice", "VolumePermille", S16Conventions.Voice.VolumePermille);
            C(sb, "C5", "Offset", C5.Offset);
            C(sb, "C5", "Size", C5.Size);
            C(sb, "C5", "Version", C5.Version);
            C(sb, "C5Flags", "Aboard", C5Flags.Aboard);
            C(sb, "C5Flags", "Seated", C5Flags.Seated);
            C(sb, "C5Flags", "Room", C5Flags.Room);
            C(sb, "C5Flags", "Powered", C5Flags.Powered);
            C(sb, "C5Flags", "Large", C5Flags.Large);
            C(sb, "C5Flags", "Station", C5Flags.Station);
            C(sb, "Events", "Offset", Events.Offset);
            C(sb, "Events", "Size", Events.Size);
            C(sb, "Events", "Version", Events.Version);
            C(sb, "EventFlags", "SonarOn", EventFlags.SonarOn);
            C(sb, "Picks", "NightLightPermille", Picks.NightLightPermille);
            C(sb, "Picks", "SplashMinUnderMs", Picks.SplashMinUnderMs);
            C(sb, "Picks", "SonarPeriodMs", Picks.SonarPeriodMs);
            C(sb, "Picks", "SeabaseLightPermille", Picks.SeabaseLightPermille);
            C(sb, "Picks", "SeabaseLightRangeDm", Picks.SeabaseLightRangeDm);
            C(sb, "Picks", "FishScareSpeedDms", Picks.FishScareSpeedDms);
            C(sb, "Picks", "FishScareRangeM", Picks.FishScareRangeM);
            C(sb, "Picks", "FishScarePermille", Picks.FishScarePermille);
            C(sb, "Picks", "FishScanMs", Picks.FishScanMs);
            C(sb, "Picks", "FishJumpSpeedDms", Picks.FishJumpSpeedDms);
            C(sb, "Mix", "MusicAbovePermille", Mix.MusicAbovePermille);
            C(sb, "Mix", "VoiceAbovePermille", Mix.VoiceAbovePermille);
            C(sb, "Mix", "CrossfadeMs", Mix.CrossfadeMs);
            C(sb, "Mix", "RowMs", Mix.RowMs);
            C(sb, "Story", "AuroraNeverS", Story.AuroraNeverS);
            C(sb, "Sky", "FrameOffset", Sky.FrameOffset);
            C(sb, "Sky", "FrameSize", Sky.FrameSize);
            C(sb, "Sky", "ListOffset", Sky.ListOffset);
            C(sb, "Sky", "ListHeader", Sky.ListHeader);
            C(sb, "Sky", "RowSize", Sky.RowSize);
            C(sb, "Sky", "MaxBodies", Sky.MaxBodies);
            C(sb, "Sky", "NameMax", Sky.NameMax);
            C(sb, "Sky", "Version", Sky.Version);
            C(sb, "Sky", "ListRefreshTicks", Sky.ListRefreshTicks);
            C(sb, "Sky", "DiscDistancePermille", Sky.DiscDistancePermille);
            C(sb, "Sky", "DiscSegments", Sky.DiscSegments);
            C(sb, "Sky", "DiscRings", Sky.DiscRings);
            C(sb, "Sky", "AmbientPermille", Sky.AmbientPermille);
            C(sb, "Sky", "LimbPermille", Sky.LimbPermille);
            C(sb, "Sky", "DayAlphaPermille", Sky.DayAlphaPermille);
            C(sb, "Sky", "NightAlphaPermille", Sky.NightAlphaPermille);
            C(sb, "Sky", "RenderQueue", Sky.RenderQueue);
            C(sb, "Sky", "RowTicks", Sky.RowTicks);
            C(sb, "SkyFlags", "Valid", SkyFlags.Valid);
            C(sb, "BodyFlags", "Bound", BodyFlags.Bound);
            C(sb, "BodyFlags", "Atmosphere", BodyFlags.Atmosphere);
            C(sb, "Oracle", "AngleCentiDeg", Oracle.AngleCentiDeg);
            C(sb, "Oracle", "SizePermille", Oracle.SizePermille);
            C(sb, "Oracle", "StillCentiDeg", Oracle.StillCentiDeg);
            C(sb, "Moons", "PlanetRadiusMicro", Moons.PlanetRadiusMicro);
            C(sb, "Moons", "TextureSize", Moons.TextureSize);
            C(sb, "Colour", "TolChannel", Colour.TolChannel);
            C(sb, "Colour", "TolLuma", Colour.TolLuma);
            C(sb, "Colour", "CentrePermille", Colour.CentrePermille);
            C(sb, "Colour", "LimbInnerPermille", Colour.LimbInnerPermille);
            C(sb, "Colour", "LimbOuterPermille", Colour.LimbOuterPermille);
            C(sb, "Colour", "BandOffsetM", Colour.BandOffsetM);
            C(sb, "Colour", "FacePermille", Colour.FacePermille);
            C(sb, "Colour", "SeaLeadMin", Colour.SeaLeadMin);
            C(sb, "Colour", "CloudLumaMin", Colour.CloudLumaMin);
            C(sb, "Colour", "SeabedMaxPermille", Colour.SeabedMaxPermille);
            C(sb, "Colour", "SeaChromaPermille", Colour.SeaChromaPermille);
            foreach (PaletteRow p in Palettes)
                sb.Append("palette ").Append(p.Id.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(p.Prefix)
                  .Append(" rgb ").Append(Rgb(p.Rgb)).Append(" limb ").Append(Rgb(p.Limb)).Append('\n');
            foreach (OrbitRow o in Orbit)
                sb.Append("orbit ").Append(o.Km.ToString(CultureInfo.InvariantCulture)).Append(" km centre ").Append(Rgb(o.Centre))
                  .Append(" limb ").Append(Rgb(o.Limb)).Append('\n');
            sb.Append("ending sea ").Append(Rgb(Ending.Sea)).Append(" cloud ").Append(Rgb(Ending.Cloud)).Append(" cover ").Append(Ending.CoverPermille.ToString(CultureInfo.InvariantCulture))
              .Append(" limb ").Append(Rgb(Ending.Limb)).Append(" rim warm ").Append(Rgb(Ending.RimWarm)).Append(" cool ").Append(Rgb(Ending.RimCool)).Append('\n');
            foreach (int km in AirKm) sb.Append("air ").Append(km.ToString(CultureInfo.InvariantCulture)).Append(" km face ").Append(Rgb(Ending.Sea)).Append('\n');
            sb.Append("mix vca sfx ").Append(Mix.SfxVca).Append(" music ").Append(Mix.MusicVca).Append(" voice ").Append(Mix.VoiceVca)
              .Append(' ').Append(Mix.VcaSource).Append('\n');
            sb.Append("sonar tag ").Append(Picks.SonarTag).Append('\n');
            sb.Append("seabase light ").Append(Rgb(Picks.SeabaseLight)).Append('\n');
            sb.Append("water far ").Append(WaterFar.Material).Append(" texture ").Append(WaterFar.Texture).Append(" mean ").Append(WaterFar.MeanR.ToString(CultureInfo.InvariantCulture))
              .Append(',').Append(WaterFar.MeanG.ToString(CultureInfo.InvariantCulture)).Append(',').Append(WaterFar.MeanB.ToString(CultureInfo.InvariantCulture))
              .Append(" up ").Append(WaterFar.SwapUpM.ToString(CultureInfo.InvariantCulture)).Append(" down ").Append(WaterFar.SwapDownM.ToString(CultureInfo.InvariantCulture))
              .Append(" seaMaterial ").Append(WaterFar.SeaMaterial).Append(" reflectivity ").Append(WaterFar.SeaReflectivity).Append(" plain ").Append(WaterFar.PlainMaterial).Append('\n');
            sb.Append("water ").Append(Water.Material).Append(" texture ").Append(Water.Texture).Append(" size ").Append(Water.Size.ToString(CultureInfo.InvariantCulture))
              .Append(" mean ").Append(Water.MeanR.ToString(CultureInfo.InvariantCulture)).Append(',').Append(Water.MeanG.ToString(CultureInfo.InvariantCulture))
              .Append(',').Append(Water.MeanB.ToString(CultureInfo.InvariantCulture)).Append(" previous ").Append(Water.Previous)
              .Append(" transparent ").Append(Water.Transparent ? "on" : "off").Append(" softnessOffset ").Append(Water.DistantSoftnessOffset).Append(" specularIntensity ").Append(Water.SpecularIntensity).Append('\n');
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
