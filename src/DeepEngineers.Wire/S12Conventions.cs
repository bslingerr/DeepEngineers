using System;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S12 "Interiors v2" (ROADMAP §14 "### S12"; docs/S12-BRIEF.md): (1) the air rule near a house: the camera has air only
 // inside a room's oxygen volume (the airtight room's cells at the camera's cell, with a hysteresis margin, the last
 // completed room cells while SE recomputes, never a held per-grid answer), (2) the interior look under water (a pinned
 // preset: caustics and sun shafts off, the interior sky's diffuse and specular scaled and tinted cool, a floor bounce
 // light), (3) flooding and venting (a water plane rising after a breach at a Torricelli rate, then following SE's own
 // oxygen level when the room is airtight again; bubbles at an Air Vent that SE vents through). SE's own Air Vent
 // carries the vent
 // effect: no world-mod block (format vent.block). Layout 1.0 is unchanged (hash 0x4f3a35f6fc1eaf31): the flood and vent
 // rows travel in C6's region space at +0x800, past S15's blocks (+0x100, +0x200..+0x61F, +0x700..+0x73F), with their own
 // seqlock word (the S15/S8a precedent). Pinned on its own by the canonical text below; Python twin tools/s12/s12_ref.py.
 // =================================================================================================================

    public static class S12Conventions
    {
        public const string Header = "ext s12 interiors 4";
        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/> as pinned in CONTRACT §5.4 "S12 conventions".</summary>
        public const ulong PinnedHash = 0x9aa269decc02e106UL;
        public const int CanonicalLines = 109, CanonicalBytes = 10496;

        /// <summary>The air rule (formats air.volume, air.hold, air.water).</summary>
        public static class Air
        {
            /// <summary>The hysteresis margin as a fraction of the grid size (permille): 0.10 m on a large grid, 0.02 m on a small one.</summary>
            public const int HysteresisPermille = 40;
            /// <summary>A grid's room snapshot keeps at most this many cells (beyond it: no snapshot, the processing answer is "no room").</summary>
            public const int SnapshotMaxCells = 262144;
            /// <summary>format air.water: a dry probe point becomes wet this far below a flooding room's plane (mm; S2b's SwimEnterEyeY).</summary>
            public const int WaterEnterMm = 100;
            /// <summary>format air.water: a wet probe point is dry again this far above the plane (mm; S2b's SwimExitGroundedEyeY).</summary>
            public const int WaterExitMm = 100;
        }

        /// <summary>The interior look preset (format look.preset), applied while the camera is in an airtight room under water.</summary>
        public static class Look
        {
            /// <summary>The interior sky's diffuse (ambient) intensity against Subnautica's BaseInterior sky, permille.</summary>
            public const int AmbientPermille = 450;
            /// <summary>The interior sky's specular intensity, permille.</summary>
            public const int SpecPermille = 600;
            /// <summary>The cool tint multiplied into the interior sky's spherical harmonics (0xRRGGBB).</summary>
            public const int TintRgb = 0xB8C8DE;
            /// <summary>Subnautica's caustics amount indoors (WaterscapeVolume.causticsAmount), permille of its own value.</summary>
            public const int CausticsPermille = 0;
            /// <summary>Sun shafts (WaterSunShaftsOnCamera) indoors: 0 off.</summary>
            public const int SunShafts = 0;
            /// <summary>The floor bounce light's intensity against the brightest mirrored lamp within BounceRangeCells, permille.</summary>
            public const int BouncePermille = 150;
            /// <summary>The bounce light's colour (0xRRGGBB, warm white: lamp light off a grey floor).</summary>
            public const int BounceRgb = 0xFFE6CC;
            public const int BounceRangeCells = 4;
            public const int BounceMaxIntensityPermille = 800;
            /// <summary>The bounce light stands this far above the room floor under the camera (mm).</summary>
            public const int BounceHeightMm = 300;
            /// <summary>Row tolerance: the measured interior mean colour against the reference, mean absolute RGB (0-1), permille.</summary>
            public const int RefTolPermille = 120;
            /// <summary>Row tolerance: the exterior with the preset on against off, mean absolute RGB, permille.</summary>
            public const int ExteriorTolPermille = 20;
            /// <summary>Row: the preset makes the interior at least this much darker than the preset off (luma, permille).</summary>
            public const int DarkerPermille = 40;
            /// <summary>Row: the water seen through the window from inside, the preset on against off, mean absolute RGB, permille.</summary>
            public const int WindowTolPermille = 40;
            /// <summary>Row: the waterline measured in a capture within this fraction of the frame height of SE's level (permille).</summary>
            public const int LineTolPermille = 60;
            /// <summary>Row: a waterline is a colour step of at least this mean absolute RGB down the far wall's column (permille).</summary>
            public const int LineMinStepPermille = 40;
        }

        /// <summary>Flooding (formats flood.breach, flood.fill, flood.drain, flood.plane).</summary>
        public static class Flood
        {
            /// <summary>The breach's discharge coefficient (Torricelli flow through the hole), permille.</summary>
            public const int DischargePermille = 600;
            public const int GravityMmS2 = 9810;
            public const int MinFillMs = 2000;
            public const int MaxFillMs = 60000;
            /// <summary>The breach depth used when the room centre is shallower (mm).</summary>
            public const int MinDepthMm = 500;
            /// <summary>Holes counted per breach (cells of the removed blocks), at most.</summary>
            public const int HoleCellsMax = 4;
            /// <summary>At or above this level a rising room is full (permille).</summary>
            public const int FullPermille = 990;
            /// <summary>At or below this level a draining room is dry and its row ends (permille).</summary>
            public const int DryPermille = 10;
            /// <summary>Rooms and vents farther than this from the camera are not tracked (m).</summary>
            public const int RangeM = 60;
            /// <summary>An oxygen-driven flood starts when the room's level drops this far below full while a vent depressurizes it (permille).</summary>
            public const int OxygenStartPermille = 20;
            /// <summary>A row is rewritten at least this often while its level changes (SE ticks).</summary>
            public const int RowTicks = 6;
            /// <summary>A full room drains no faster than this after the repair (ms).</summary>
            public const int DrainMs = 20000;
            /// <summary>A room's environment oxygen from this level up is its dry level (format flood.drain), permille.</summary>
            public const int DryEnvMinPermille = 50;
            /// <summary>format flood.breach: a breached room drains only after staying airtight this long (ms; SE's door cycles).</summary>
            public const int RepairDebounceMs = 2000;
            /// <summary>format flood.breach: a breach this soon after a drain started goes on as the same flood (ms; no new start).</summary>
            public const int ReBreachQuietMs = 5000;
        }

        /// <summary>Venting (format vent.event).</summary>
        public static class Vent
        {
            /// <summary>Bubbles per second at a venting Air Vent (through S7p's per-frame bubble budget).</summary>
            public const int BubblesPerS = 40;
            /// <summary>The burst at a venting start (one per event).</summary>
            public const int BurstBubbles = 60;
            /// <summary>The splash burst of a flood start (one per event).</summary>
            public const int SplashBubbles = 80;
            /// <summary>Row: the capture around the venting vent's mouth differs from the idle one by at least this (mean absolute RGB, permille).</summary>
            public const int CaptureMinPermille = 30;
        }

        /// <summary>The C6 region-space block (format c6.s12).</summary>
        public static class C6
        {
            public const int Offset = 0x800, HeaderSize = 0x20, RowSize = 0x80, MaxRows = 8, MaxBoxes = 6;
            public const int Size = HeaderSize + MaxRows * RowSize;
            public const int Version = 1;
        }

        public static class C6Flags
        {
            public const int Active = 1, AirRule = 2, Look = 4;
        }

        public static class RowKind
        {
            public const int None = 0, Flood = 1, Vent = 2;
            public static readonly string[] Names = { "none", "flood", "vent" };
            public static string Name(int k) { return k >= 0 && k < Names.Length ? Names[k] : "kind" + k.ToString(CultureInfo.InvariantCulture); }
        }

        public static class FloodState
        {
            public const int None = 0, Rising = 1, Full = 2, Draining = 3;
            public static readonly string[] Names = { "none", "rising", "full", "draining" };
            public static string Name(int s) { return s >= 0 && s < Names.Length ? Names[s] : "state" + s.ToString(CultureInfo.InvariantCulture); }
        }

        public static class FloodSource
        {
            public const int None = 0, Breach = 1, Oxygen = 2;
            public static readonly string[] Names = { "none", "breach", "oxygen" };
            public static string Name(int s) { return s >= 0 && s < Names.Length ? Names[s] : "source" + s.ToString(CultureInfo.InvariantCulture); }
        }

        public static class VentState
        {
            public const int None = 0, Pressurizing = 1, Depressurizing = 2;
            public static readonly string[] Names = { "none", "pressurizing", "depressurizing" };
            public static string Name(int s) { return s >= 0 && s < Names.Length ? Names[s] : "state" + s.ToString(CultureInfo.InvariantCulture); }
        }

        /// <summary>The grid-local axis that points up (against gravity): 1 +X, 2 -X, 3 +Y, 4 -Y, 5 +Z, 6 -Z (SE-local).</summary>
        public static class UpAxis
        {
            public const int PosX = 1, NegX = 2, PosY = 3, NegY = 4, PosZ = 5, NegZ = 6;
        }

        /// <summary>The header fields (C6 region offsets relative to +0x800).</summary>
        public static readonly string[][] HeaderFields =
        {
            new[] { "seq", "0x00", "4", "u32" },
            new[] { "version", "0x04", "1", "u8" },
            new[] { "flags", "0x05", "1", "u8" },
            new[] { "count", "0x06", "1", "u8" },
            new[] { "reserved", "0x07", "1", "u8" },
            new[] { "worldEpoch", "0x08", "4", "u32" },
            new[] { "floodSeq", "0x0c", "4", "u32" },
            new[] { "ventSeq", "0x10", "4", "u32" },
            new[] { "tick", "0x14", "8", "u64" },
            new[] { "reserved2", "0x1c", "4", "u32" },
        };

        /// <summary>The row fields (offsets inside a 128-byte row; row i at 0x20 + i x 0x80).</summary>
        public static readonly string[][] RowFields =
        {
            new[] { "gridId", "0x00", "8", "i64" },
            new[] { "trackId", "0x08", "4", "u32" },
            new[] { "kind", "0x0c", "1", "u8" },
            new[] { "state", "0x0d", "1", "u8" },
            new[] { "source", "0x0e", "1", "u8" },
            new[] { "boxCount", "0x0f", "1", "u8" },
            new[] { "level", "0x10", "4", "f32" },
            new[] { "ratePerS", "0x14", "4", "f32" },
            new[] { "startSeq", "0x18", "4", "u32" },
            new[] { "gridSize", "0x1c", "1", "u8" },
            new[] { "upAxis", "0x1d", "1", "u8" },
            new[] { "flags", "0x1e", "2", "u16" },
            new[] { "point", "0x20", "12", "f32x3" },
            new[] { "dir", "0x2c", "12", "f32x3" },
            new[] { "boxes", "0x38", "72", "i16x6x6" },
        };

        // ---------------------------------------------------------------------------------------------- canonical text

        public static readonly string[] FormatLines =
        {
            "air.volume the probe point p (grid-local, metres), its cell c = round(p / g) and f = p / g - c; the distance to the face of c in direction s e_a is g (1/2 - s f_a); when c is a cell of an airtight room the depth d = min over the faces whose neighbour cell is not of the same room of that distance, g / 2 with no such face (the air reaches the room cells' faces, where SE's airtight faces are: MyGridGasSystem.IsAirtightBlock; a window's, a door's or a slope's cell that SE counts as room is air up to its sealed face); else d = -(min over the faces whose neighbour is a cell of an airtight room of that distance), -g / 2 with none; the room cells are MyGridGasSystem.GetOxygenRoomForCubeGridPosition (IsAirtight) when the gas system is not processing, else format air.hold's",
            "air.hold per probe point and grid: in stays true while d > -h and becomes true when d > +h, h = g x HysteresisPermille / 1000; one flip per crossing of a room's boundary; while the gas system processes, d comes from the grid's room snapshot (InteriorRooms: GetRooms, each airtight MyOxygenRoom.Blocks) taken after the gas system's last OnProcessingDataComplete, else (no snapshot, or an older one) the probe point keeps its previous answer for that grid",
            "air.camera C1 InRoom (Subnautica's inside-walkable picture, SE's audio muffle) is the camera's answer in first person and for the spectator; in third person (the camera controller is the local character or its seat, not in first person) it also needs the local character's eye in a room (its swim-gate answer of this tick or the last two): a character outside the hull never gets the dry picture",
            "air.flood a room that is not airtight is water: while a breach flood rises or is full (format flood.breach) the room gives no air to the camera or to any character, whatever its drawn plane shows (SE's own environment oxygen there is the water's); only an airtight room with a flood row has air, above its plane (format air.water)",
            "air.water a probe point in an airtight room with a flood row (format flood.plane) has air only above the plane, with a latch per probe point and grid: water once its height above the room's water floor along upAxis < level x the water height - WaterEnterMm / 1000, air again once > level x the water height + WaterExitMm / 1000 (S2b's swim entry and grounded exit: SwimThresholds.SwimEnterEyeY, SwimExitGroundedEyeY)",
            "c6.s12 C6 region +0x800, own seqlock, written with every C6 while S12 is active: header (version, flags Active|AirRule|Look, count <= MaxRows, worldEpoch, floodSeq and ventSeq summed over the rows' starts, tick), then count rows of RowSize: one per flooding room (kind flood) and one per venting Air Vent (kind vent) within RangeM of the camera, nearest first; version 0 = absent",
            "flood.breach a room tracked while airtight whose watched cell (its snapshot's first cell) lies in a room that exists and is not airtight on a completed gas system (SE sets IsAirtight false and OxygenAmount 0 at once and merges the cells: MyGridGasSystem.RemoveBlock; a door that opens: IsAirtightBlock's PressurizedClosed) starts a breach flood (startSeq + 1) when its box centre is under the sea; no room at the watched cell (a block placed there: MyGridGasSystem.AddBlock nulls it) decides nothing, and the watched cell is picked again from the next snapshot's room over the track's cells; a repaired room drains once it stayed airtight RepairDebounceMs, a breach within ReBreachQuietMs of the drain's start goes on as the same flood (no new start), a later one rises again (startSeq + 1); a draining room's boxes follow its airtight room, and of two active tracks over the same cell only the one with the newer geometry draws it (one plane per cell)",
            "flood.drain the water an airtight room's oxygen gives is w = clamp((D - OxygenLevel) / D, 0, 1), D = MyOxygenRoom.EnvironmentOxygen when it is >= DryEnvMinPermille / 1000 (a room SE rebuilds on a breathable planet starts at it: MyGridGasSystem.CreateAirtightRoom), else 1; after the repair the level moves toward w no faster than a full room in DrainMs and never above the level at the repair; an airtight room under water whose vent depressurizes it floods with level = w once w >= OxygenStartPermille / 1000 (source oxygen); a row ends when level <= DryPermille / 1000 while draining",
            "flood.fill a breach rises at level(t) = min(1, t / T), T = clamp(V / Q, MinFillMs, MaxFillMs), V = the room's cells x g^3, Q = DischargePermille / 1000 x A x sqrt(2 x GravityMmS2 / 1000 x max(depth, MinDepthMm / 1000)), A = min(holes, HoleCellsMax) x g^2 (holes >= 1), depth = the room box centre's depth below the sea (m); rising becomes full at FullPermille / 1000",
            "flood.plane a flood row's boxes are the room's water cells (its cells less the cells whose block has a face on the room's boundary: windows, doors, slopes; MyCubeGrid.GetCubeBlock) decomposed into boxes (the lowest remaining cell in z, y, x order: its run along x, grown along y, then z), the MaxBoxes largest by cells, never their bounding box; Subnautica draws one water plane per box (the box's cross-section normal to upAxis at the box floor + level x the height of the boxes' union, under the mirrored grid's Entity; Unity-local = SE-local with z negated), with BaseWaterPlane's own material (its private waterRender's material, read from a loaded BaseWaterPlane) or a transparent fallback; the level eased toward the row's (ratePerS between records); one splash per startSeq increment (Subnautica's Vehicle.splashSound and SplashBubbles on the room floor under the row's point, through S7's effect player and its caps)",
            "flood.surface inside an airtight room with a flood row the room's plane is the character's water surface: S2b's swim model takes the eye's height above the plane (the plane's SN y at the eye) instead of above the sea (its entry below -0.1 m, exit above +0.8 m or +0.1 m grounded) and SE walks above it; when the row ends the room is dry and the swim gate's airtight room releases the swim latch at once (SwimExit.Room)",
            "look.preset caustics: WaterscapeVolume.causticsAmount x CausticsPermille / 1000; sun shafts: WaterSunShaftsOnCamera.enabled = SunShafts != 0; the interior chunks' sky: a copy of MarmoSkies' BaseInterior sky with DiffIntensity x AmbientPermille / 1000, SpecIntensity x SpecPermille / 1000 and its SH coefficients x TintRgb / 255 per channel; a floor bounce point light (no shadows) BounceHeightMm above the room floor under the camera, colour BounceRgb, intensity min(BounceMaxIntensityPermille / 1000, BouncePermille / 1000 x the brightest mirrored lamp within BounceRangeCells cells), range BounceRangeCells x g; each value restored when the look ends",
            "look.when the preset applies while de.s12 look is on, the camera is in an airtight room (C1 InRoom, the S12 air rule) and the camera is under the sea (SN y < 0); the exterior is untouched",
            "vent.block SE's own Air Vent carries the effect (MyAirVent is the only IMyGasBlock with CanPressurizeRoom: MyAirVent.cs:257, MyGasTank.cs:203 false; SE builds no airtight room without one: MyGridGasSystem.ShouldPressurize); no world-mod block",
            "vent.event an Air Vent within RangeM of the camera vents while SE plays its own vent effect (MyAirVent.m_isPlayingVentEffect, set by CheckForVentEffect from VentToRoom/DrainFromRoom) or while de.s12 vent drives it (dev): one row (kind vent, state Pressurizing or Depressurizing from IsDepressurizing, point = the vent's centre, dir = its forward; its room is the room at its vent dummy: MyAirVent.GetOxygenBlock), startSeq + 1 at each start; Subnautica bubbles BurstBubbles at a start and BubblesPerS while it vents (S7p's per-frame bubble budget), no sound of its own (SE's PressurizeSound/DepressurizeSound play on SE's side)",
        };

        private static string _text;
        private static ulong _hash;

        private static void C(StringBuilder sb, string group, string name, long v)
        {
            sb.Append("const ").Append(group).Append(' ').Append(name).Append(' ').Append(v.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        /// <summary>
 /// The canonical text (CONTRACT §5.4 "S12 conventions"; ASCII, '\n' after every line, the last included): the header,
 /// the const groups, the header and row field lines, the format lines sorted by key. The Python twin writes the same bytes.
 /// </summary>
        public static string CanonicalText()
        {
            if (_text != null) return _text;
            var sb = new StringBuilder(8192);
            sb.Append(Header).Append('\n');
            C(sb, "Air", "HysteresisPermille", Air.HysteresisPermille);
            C(sb, "Air", "SnapshotMaxCells", Air.SnapshotMaxCells);
            C(sb, "Air", "WaterEnterMm", Air.WaterEnterMm);
            C(sb, "Air", "WaterExitMm", Air.WaterExitMm);
            C(sb, "Look", "AmbientPermille", Look.AmbientPermille);
            C(sb, "Look", "SpecPermille", Look.SpecPermille);
            C(sb, "Look", "TintRgb", Look.TintRgb);
            C(sb, "Look", "CausticsPermille", Look.CausticsPermille);
            C(sb, "Look", "SunShafts", Look.SunShafts);
            C(sb, "Look", "BouncePermille", Look.BouncePermille);
            C(sb, "Look", "BounceRgb", Look.BounceRgb);
            C(sb, "Look", "BounceRangeCells", Look.BounceRangeCells);
            C(sb, "Look", "BounceMaxIntensityPermille", Look.BounceMaxIntensityPermille);
            C(sb, "Look", "BounceHeightMm", Look.BounceHeightMm);
            C(sb, "Look", "RefTolPermille", Look.RefTolPermille);
            C(sb, "Look", "ExteriorTolPermille", Look.ExteriorTolPermille);
            C(sb, "Look", "DarkerPermille", Look.DarkerPermille);
            C(sb, "Look", "WindowTolPermille", Look.WindowTolPermille);
            C(sb, "Look", "LineTolPermille", Look.LineTolPermille);
            C(sb, "Look", "LineMinStepPermille", Look.LineMinStepPermille);
            C(sb, "Flood", "DischargePermille", Flood.DischargePermille);
            C(sb, "Flood", "GravityMmS2", Flood.GravityMmS2);
            C(sb, "Flood", "MinFillMs", Flood.MinFillMs);
            C(sb, "Flood", "MaxFillMs", Flood.MaxFillMs);
            C(sb, "Flood", "MinDepthMm", Flood.MinDepthMm);
            C(sb, "Flood", "HoleCellsMax", Flood.HoleCellsMax);
            C(sb, "Flood", "FullPermille", Flood.FullPermille);
            C(sb, "Flood", "DryPermille", Flood.DryPermille);
            C(sb, "Flood", "RangeM", Flood.RangeM);
            C(sb, "Flood", "OxygenStartPermille", Flood.OxygenStartPermille);
            C(sb, "Flood", "RowTicks", Flood.RowTicks);
            C(sb, "Flood", "DrainMs", Flood.DrainMs);
            C(sb, "Flood", "DryEnvMinPermille", Flood.DryEnvMinPermille);
            C(sb, "Flood", "RepairDebounceMs", Flood.RepairDebounceMs);
            C(sb, "Flood", "ReBreachQuietMs", Flood.ReBreachQuietMs);
            C(sb, "Vent", "BubblesPerS", Vent.BubblesPerS);
            C(sb, "Vent", "BurstBubbles", Vent.BurstBubbles);
            C(sb, "Vent", "SplashBubbles", Vent.SplashBubbles);
            C(sb, "Vent", "CaptureMinPermille", Vent.CaptureMinPermille);
            C(sb, "C6", "Offset", C6.Offset);
            C(sb, "C6", "HeaderSize", C6.HeaderSize);
            C(sb, "C6", "RowSize", C6.RowSize);
            C(sb, "C6", "MaxRows", C6.MaxRows);
            C(sb, "C6", "MaxBoxes", C6.MaxBoxes);
            C(sb, "C6", "Size", C6.Size);
            C(sb, "C6", "Version", C6.Version);
            C(sb, "C6Flags", "Active", C6Flags.Active);
            C(sb, "C6Flags", "AirRule", C6Flags.AirRule);
            C(sb, "C6Flags", "Look", C6Flags.Look);
            for (int k = 0; k < RowKind.Names.Length; k++) C(sb, "RowKind", RowKind.Names[k], k);
            for (int s = 0; s < FloodState.Names.Length; s++) C(sb, "FloodState", FloodState.Names[s], s);
            for (int s = 0; s < FloodSource.Names.Length; s++) C(sb, "FloodSource", FloodSource.Names[s], s);
            for (int s = 0; s < VentState.Names.Length; s++) C(sb, "VentState", VentState.Names[s], s);
            C(sb, "UpAxis", "PosX", UpAxis.PosX);
            C(sb, "UpAxis", "NegX", UpAxis.NegX);
            C(sb, "UpAxis", "PosY", UpAxis.PosY);
            C(sb, "UpAxis", "NegY", UpAxis.NegY);
            C(sb, "UpAxis", "PosZ", UpAxis.PosZ);
            C(sb, "UpAxis", "NegZ", UpAxis.NegZ);
            foreach (string[] f in HeaderFields)
                sb.Append("field s12 header ").Append(f[0]).Append(' ').Append(f[1]).Append(' ').Append(f[2]).Append(' ').Append(f[3]).Append('\n');
            foreach (string[] f in RowFields)
                sb.Append("field s12 row ").Append(f[0]).Append(' ').Append(f[1]).Append(' ').Append(f[2]).Append(' ').Append(f[3]).Append('\n');
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
