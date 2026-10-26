using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S8a "The terrain converter": the meaning S8a gives to C2 region bytes 0x50000-0x5803F (the S8a header and the
 // terrain notice rows, inside the C2 seqlock) and to C7 SnStatus pad bytes 0x8C-0xBB (the acknowledgement block), plus
 // the converter's, the store's, the window's and the chart's rules (CONTRACT §3.3 and §5.4 "S8a conventions"). Layout
 // 1.0 is unchanged (hash 0x4f3a35f6fc1eaf31; O16 stays open). Pinned on its own by the canonical text below (FNV-1a 64
 // 0xcbf4ca049d06c430, 154 lines, 14,175 bytes; ext s8a terrain 5 since late night (Stage F2): ring.field and the ring's identity in
 // store.key;offsite
 // Step0Batches 5, StepBatches 12 and StepGapMs (the treadmill's hysteresis); terrain 3 (0x4741bf09b81065a2) the visible common ores' types; 2 was
 // 0x7f480768c4cb872f (Stage C: biome map, ore rows, sculpt lines), 1 was 0x3d52f1105b92a172), as S5, S6, S7a and S7 pinned theirs. The text is built from the
 // constants of this file, so a changed constant changes the hash and turns S8aConventionsTests red. Python twin:
 // tools/planet/s8a_ref.py (text, hash, codecs, chart, plan, keys; vectors tests/s8a/vectors/).
 // =================================================================================================================

 /// <summary>The S8a conventions (CONTRACT §5.4 "S8a conventions"): one nested class per <c>const</c> group of the text.</summary>
    public static class S8aConventions
    {
        /// <summary>The first line of the canonical text (<c>ext s8a terrain &lt;n&gt;</c>).</summary>
        public const string Header = "ext s8a terrain 6";
        /// <summary>The <c>&lt;n&gt;</c> of <see cref="Header"/>.</summary>
        public const int ExtensionVersion = 6;
        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/> as pinned in CONTRACT §5.4.</summary>
        public const ulong PinnedHash = 0x3188251ab11e2fecUL;
        /// <summary>Lines and bytes of the canonical text.</summary>
        public const int CanonicalLines = 155, CanonicalBytes = 14696;

        /// <summary>The C2 S8a block: a 64-byte header at C2 region offset 0x50000, then up to 1,024 rows of 32 bytes.</summary>
        public static class C2S8a
        {
            public const int Version = 1;
            public const int RowSize = 32;
            public const int HeaderSize = 64;
            /// <summary>New rows (never written before) per C2 write.</summary>
            public const int MaxPerTick = 64;
            /// <summary>Rows per C2 write in all (the window of unacknowledged notices).</summary>
            public const int MaxRows = 1024;
            /// <summary>C2 region offset of the S8a header (0x50000).</summary>
            public const int Offset = 0x50000;
            /// <summary>C2 region offset of row 0 (0x50040).</summary>
            public const int RowsOffset = 0x50040;
            /// <summary>One past the last byte of the block (0x58040).</summary>
            public const int End = RowsOffset + MaxRows * RowSize;
        }

        /// <summary>The S8a header's <c>flags</c> (byte 0x01).</summary>
        public static class C2S8aFlags
        {
            public const byte DrillOn = 1;
            public const byte WindowOn = 2;
            public const byte TreadmillOn = 4;
            public const byte NearPending = 8;
            public const byte NearEmpty = 16;
            public const byte Resync = 32;
            /// offsite").</summary>
            public const byte OffSite = 64;
        }

        /// <summary>The C7 S8a block in <c>SnStatus.pad_8c</c> (absolute offsets 0x8C-0xBB).</summary>
        public static class C7S8a
        {
            public const int Version = 1;
            public const int Size = 48;
            /// <summary>Absolute C7 offset of the block (0x8C).</summary>
            public const int Offset = 0x8C;
        }

        /// <summary>The C7 S8a block's <c>flags</c> (byte 0x8D).</summary>
        public static class C7S8aFlags
        {
            public const byte StoreOpen = 1;
            public const byte GetPathPatched = 2;
            public const byte LoaderPatched = 4;
            public const byte CenterWidened = 8;
            public const byte Refused = 16;
        }

        /// <summary>The chart (treadmill) rule ("chart.step", "chart.anchor").</summary>
        public static class Chart
        {
            /// <summary>chartSeq of the site chart (site map v1).</summary>
            public const int Site = 0;
            /// <summary>

 /// BoxInnerM, and on chart 1 the inner bound came before ReturnM; now chart 1 starts at 4,000 m, 800 m inside the box, and the
 /// return at site 4,160 = chart 3,360 comes before the inner bound).
 /// </summary>
            public const int Step0Batches = 5;
            /// <summary>T of every later step, in batches (terrain 4: 12, was 24: a 3,840 m step from BoxOuterM landed on BoxInnerM and back,
 /// no hysteresis; 1,920 m lands in the middle of the 3,200..7,040 box, 1,920 m from either bound).</summary>
            public const int StepBatches = 12;
            /// <summary>The camera stays this far out on charts ≥ 1 (≥ 1,152 m from the crater box's edge: its install terrain is never near).</summary>
            public const int BoxInnerM = 3200;
            /// <summary>An automatic step at least this long after the previous one (no flip-flop at a bound; forced steps exempt).</summary>
            public const int StepGapMs = 2000;
            public const int AnchorMaxM = 3840;
            public const int ReturnM = 4160;
            public const int Chart0OutM = 4800;
            public const int BoxOuterM = 7040;
        }

        /// <summary>A notice row's <c>flags</c> (u16 at 0x06).</summary>
        public static class NoticeFlags
        {
            public const ushort AllSolid = 1;
            public const ushort AllEmpty = 2;
            public const ushort Reuse = 4;
            public const ushort Final = 8;
            public const ushort Restore = 16;
        }

        /// <summary>A notice row's <c>kind</c> (byte 0x04).</summary>
        public static class NoticeKind
        {
            public const byte BatchWritten = 1;
            public const byte BatchCleared = 2;
            public const byte ChartStep = 3;
            public const byte StoreReset = 4;
        }

        /// <summary>A notice row's <c>layer</c> (byte 0x05; 0 for ChartStep).</summary>
        public static class NoticeLayer
        {
            public const byte Drill = 1;
            public const byte Window = 2;
        }

        /// <summary>The drill overlay ("overlay.file"): 16³ LOD-0 cells of a tile.</summary>
        public static class Overlay
        {
            public const int Version = 1;
            public const int CellVoxels = 16;
            public const int MaxCells = 65536;
        }

        /// <summary>Subnautica's reload queue and SE's notice queue.</summary>
        public static class Queue
        {
            public const int MaxReloadsInFlight = 8;
            public const int MaxCellReloadsPerNotice = 64;
            public const int MaxPending = 4096;
        }

        /// <summary>Subnautica's voxel world (CONTRACT §3.1 and §3.3 "Batch index math").</summary>
        public static class SnWorld
        {
            public const int OriginY = -3040;
            public const int OriginX = -2048;
            public const int OriginZ = -2048;
            /// <summary>Batch file version written (and the lowest one Subnautica loads).</summary>
            public const int FileVersion = 4;
            public const int OctreesPerBatch = 5;
            public const int BatchesY = 20;
            public const int BatchesX = 26;
            public const int BatchesZ = 26;
            public const int OctreeVoxels = 32;
            public const int OctreesY = 100;
            /// <summary>Density of a zero signed distance (solid iff density &gt;= 126 for a near voxel).</summary>
            public const int NearZero = 126;
            public const int OctreesX = 128;
            public const int OctreesZ = 128;
            public const int BatchVoxels = 160;
        }

        /// <summary>The spawn table's radii ("spawn.place").</summary>
        public static class Spawn
        {
            public const int RadiusM = 150;
            public const int DespawnM = 250;
        }

        /// <summary>The store ("store.*").</summary>
        public static class Store
        {
            public const int CoalesceMsPerMB = 50;
            public const int CoalesceMs = 250;
            public const int KeepWindowM = 1600;
            public const int MaxMB = 2048;
            public const int GraceMs = 10000;
        }

        /// <summary>The window ("window.*").</summary>
        public static class Window
        {
            public const int UniverseMin = -35;
            public const int CraterMin = 0;
            public const int LookAheadBatches = 1;
            public const int RingBatches = 5;
            public const int StreamCenterDownM = 8;
            public const int NearBoxM = 16;
            public const int CraterMax = 25;
            public const int UniverseMax = 60;
            public const int NearSurfaceM = 150;
            public const int BiomeRadiusM = 200;
            public const int LookAheadMinSpeedCmS = 500;
        }

        /// <summary>One <c>const</c> line: group, name, value.</summary>
        private struct ConstEntry
        {
            public readonly string Group, Name;
            public readonly long Value;
            public ConstEntry(string group, string name, long value) { Group = group; Name = name; Value = value; }
        }

        private static ConstEntry[] Entries()
        {
            return new[]
            {
                new ConstEntry("C2S8a", "Version", C2S8a.Version),
                new ConstEntry("C2S8a", "RowSize", C2S8a.RowSize),
                new ConstEntry("C2S8a", "HeaderSize", C2S8a.HeaderSize),
                new ConstEntry("C2S8a", "MaxPerTick", C2S8a.MaxPerTick),
                new ConstEntry("C2S8a", "MaxRows", C2S8a.MaxRows),
                new ConstEntry("C2S8a", "Offset", C2S8a.Offset),
                new ConstEntry("C2S8a", "RowsOffset", C2S8a.RowsOffset),
                new ConstEntry("C2S8aFlags", "DrillOn", C2S8aFlags.DrillOn),
                new ConstEntry("C2S8aFlags", "WindowOn", C2S8aFlags.WindowOn),
                new ConstEntry("C2S8aFlags", "TreadmillOn", C2S8aFlags.TreadmillOn),
                new ConstEntry("C2S8aFlags", "NearPending", C2S8aFlags.NearPending),
                new ConstEntry("C2S8aFlags", "NearEmpty", C2S8aFlags.NearEmpty),
                new ConstEntry("C2S8aFlags", "Resync", C2S8aFlags.Resync),
                new ConstEntry("C2S8aFlags", "OffSite", C2S8aFlags.OffSite),
                new ConstEntry("C7S8a", "Version", C7S8a.Version),
                new ConstEntry("C7S8a", "Size", C7S8a.Size),
                new ConstEntry("C7S8a", "Offset", C7S8a.Offset),
                new ConstEntry("C7S8aFlags", "StoreOpen", C7S8aFlags.StoreOpen),
                new ConstEntry("C7S8aFlags", "GetPathPatched", C7S8aFlags.GetPathPatched),
                new ConstEntry("C7S8aFlags", "LoaderPatched", C7S8aFlags.LoaderPatched),
                new ConstEntry("C7S8aFlags", "CenterWidened", C7S8aFlags.CenterWidened),
                new ConstEntry("C7S8aFlags", "Refused", C7S8aFlags.Refused),
                new ConstEntry("Chart", "Site", Chart.Site),
                new ConstEntry("Chart", "Step0Batches", Chart.Step0Batches),
                new ConstEntry("Chart", "StepBatches", Chart.StepBatches),
                new ConstEntry("Chart", "BoxInnerM", Chart.BoxInnerM),
                new ConstEntry("Chart", "AnchorMaxM", Chart.AnchorMaxM),
                new ConstEntry("Chart", "ReturnM", Chart.ReturnM),
                new ConstEntry("Chart", "Chart0OutM", Chart.Chart0OutM),
                new ConstEntry("Chart", "BoxOuterM", Chart.BoxOuterM),
                new ConstEntry("Chart", "StepGapMs", Chart.StepGapMs),
                new ConstEntry("NoticeFlags", "AllSolid", NoticeFlags.AllSolid),
                new ConstEntry("NoticeFlags", "AllEmpty", NoticeFlags.AllEmpty),
                new ConstEntry("NoticeFlags", "Reuse", NoticeFlags.Reuse),
                new ConstEntry("NoticeFlags", "Final", NoticeFlags.Final),
                new ConstEntry("NoticeFlags", "Restore", NoticeFlags.Restore),
                new ConstEntry("NoticeKind", "BatchWritten", NoticeKind.BatchWritten),
                new ConstEntry("NoticeKind", "BatchCleared", NoticeKind.BatchCleared),
                new ConstEntry("NoticeKind", "ChartStep", NoticeKind.ChartStep),
                new ConstEntry("NoticeKind", "StoreReset", NoticeKind.StoreReset),
                new ConstEntry("NoticeLayer", "Drill", NoticeLayer.Drill),
                new ConstEntry("NoticeLayer", "Window", NoticeLayer.Window),
                new ConstEntry("Overlay", "Version", Overlay.Version),
                new ConstEntry("Overlay", "CellVoxels", Overlay.CellVoxels),
                new ConstEntry("Overlay", "MaxCells", Overlay.MaxCells),
                new ConstEntry("Queue", "MaxReloadsInFlight", Queue.MaxReloadsInFlight),
                new ConstEntry("Queue", "MaxCellReloadsPerNotice", Queue.MaxCellReloadsPerNotice),
                new ConstEntry("Queue", "MaxPending", Queue.MaxPending),
                new ConstEntry("SnWorld", "OriginY", SnWorld.OriginY),
                new ConstEntry("SnWorld", "OriginX", SnWorld.OriginX),
                new ConstEntry("SnWorld", "OriginZ", SnWorld.OriginZ),
                new ConstEntry("SnWorld", "FileVersion", SnWorld.FileVersion),
                new ConstEntry("SnWorld", "OctreesPerBatch", SnWorld.OctreesPerBatch),
                new ConstEntry("SnWorld", "BatchesY", SnWorld.BatchesY),
                new ConstEntry("SnWorld", "BatchesX", SnWorld.BatchesX),
                new ConstEntry("SnWorld", "BatchesZ", SnWorld.BatchesZ),
                new ConstEntry("SnWorld", "OctreeVoxels", SnWorld.OctreeVoxels),
                new ConstEntry("SnWorld", "OctreesY", SnWorld.OctreesY),
                new ConstEntry("SnWorld", "NearZero", SnWorld.NearZero),
                new ConstEntry("SnWorld", "OctreesX", SnWorld.OctreesX),
                new ConstEntry("SnWorld", "OctreesZ", SnWorld.OctreesZ),
                new ConstEntry("SnWorld", "BatchVoxels", SnWorld.BatchVoxels),
                new ConstEntry("Spawn", "RadiusM", Spawn.RadiusM),
                new ConstEntry("Spawn", "DespawnM", Spawn.DespawnM),
                new ConstEntry("Store", "CoalesceMsPerMB", Store.CoalesceMsPerMB),
                new ConstEntry("Store", "CoalesceMs", Store.CoalesceMs),
                new ConstEntry("Store", "KeepWindowM", Store.KeepWindowM),
                new ConstEntry("Store", "MaxMB", Store.MaxMB),
                new ConstEntry("Store", "GraceMs", Store.GraceMs),
                new ConstEntry("Window", "UniverseMin", Window.UniverseMin),
                new ConstEntry("Window", "CraterMin", Window.CraterMin),
                new ConstEntry("Window", "LookAheadBatches", Window.LookAheadBatches),
                new ConstEntry("Window", "RingBatches", Window.RingBatches),
                new ConstEntry("Window", "StreamCenterDownM", Window.StreamCenterDownM),
                new ConstEntry("Window", "NearBoxM", Window.NearBoxM),
                new ConstEntry("Window", "CraterMax", Window.CraterMax),
                new ConstEntry("Window", "UniverseMax", Window.UniverseMax),
                new ConstEntry("Window", "NearSurfaceM", Window.NearSurfaceM),
                new ConstEntry("Window", "BiomeRadiusM", Window.BiomeRadiusM),
                new ConstEntry("Window", "LookAheadMinSpeedCmS", Window.LookAheadMinSpeedCmS),
            };
        }

        /// <summary>One <c>field</c> line: block, name, offset from the record start, size, type.</summary>
        private struct FieldEntry
        {
            public readonly string Block, Name, Type;
            public readonly int Offset, Size;
            public FieldEntry(string block, string name, int offset, int size, string type) { Block = block; Name = name; Offset = offset; Size = size; Type = type; }
        }

        private static FieldEntry[] Fields()
        {
            return new[]
            {
                new FieldEntry("s8a", "version", 0x00, 1, "u8"),
                new FieldEntry("s8a", "flags", 0x01, 1, "u8"),
                new FieldEntry("s8a", "rowCount", 0x02, 2, "u16"),
                new FieldEntry("s8a", "firstSeq", 0x04, 4, "u32"),
                new FieldEntry("s8a", "stream", 0x08, 4, "u32"),
                new FieldEntry("s8a", "dropped", 0x0C, 4, "u32"),
                new FieldEntry("s8a", "tickLow", 0x10, 4, "u32"),
                new FieldEntry("s8a", "resyncEpoch", 0x14, 4, "u32"),
                new FieldEntry("s8a", "storeKey", 0x18, 8, "u64"),
                new FieldEntry("s8a", "overlayGen", 0x20, 4, "u32"),
                new FieldEntry("s8a", "chartSeq", 0x24, 4, "u32"),
                new FieldEntry("s8a", "cameraBiome", 0x28, 1, "u8"),
                new FieldEntry("s8a", "pad", 0x29, 1, "u8"),
                new FieldEntry("s8a", "nearPendingCount", 0x2A, 2, "u16"),
                new FieldEntry("s8a", "seabedBelowCamM", 0x2C, 4, "f32"),
                new FieldEntry("s8a", "windowGen", 0x30, 4, "u32"),
                new FieldEntry("s8a", "drillGen", 0x34, 4, "u32"),
                new FieldEntry("s8a", "chartKey", 0x38, 8, "u64"),
                new FieldEntry("notice", "seq", 0x00, 4, "u32"),
                new FieldEntry("notice", "kind", 0x04, 1, "u8"),
                new FieldEntry("notice", "layer", 0x05, 1, "u8"),
                new FieldEntry("notice", "flags", 0x06, 2, "u16"),
                new FieldEntry("notice", "bx", 0x08, 2, "i16"),
                new FieldEntry("notice", "by", 0x0A, 2, "i16"),
                new FieldEntry("notice", "bz", 0x0C, 2, "i16"),
                new FieldEntry("notice", "pad", 0x0E, 2, "u16"),
                new FieldEntry("notice", "version", 0x10, 4, "u32"),
                new FieldEntry("notice", "min", 0x14, 3, "u8[3]"),
                new FieldEntry("notice", "max", 0x17, 3, "u8[3]"),
                new FieldEntry("notice", "octrees", 0x1A, 2, "u16"),
                new FieldEntry("notice", "cells", 0x1C, 4, "u32"),
                new FieldEntry("c7s8a", "version", 0x8C, 1, "u8"),
                new FieldEntry("c7s8a", "flags", 0x8D, 1, "u8"),
                new FieldEntry("c7s8a", "reloadsPending", 0x8E, 2, "u16"),
                new FieldEntry("c7s8a", "ackSeq", 0x90, 4, "u32"),
                new FieldEntry("c7s8a", "appliedSeq", 0x94, 4, "u32"),
                new FieldEntry("c7s8a", "stream", 0x98, 4, "u32"),
                new FieldEntry("c7s8a", "resyncEpoch", 0x9C, 4, "u32"),
                new FieldEntry("c7s8a", "chartSeqApplied", 0xA0, 4, "u32"),
                new FieldEntry("c7s8a", "batchesServed", 0xA4, 4, "u32"),
                new FieldEntry("c7s8a", "reloadFailures", 0xA8, 4, "u32"),
                new FieldEntry("c7s8a", "lastLatencyMs", 0xAC, 2, "u16"),
                new FieldEntry("c7s8a", "nearPendingSeen", 0xAE, 2, "u16"),
                new FieldEntry("c7s8a", "storeKey", 0xB0, 8, "u64"),
                new FieldEntry("c7s8a", "pad", 0xB8, 4, "u32"),
            };
        }

        /// <summary>The <c>field</c> lines (without the <c>field </c> prefix), in text order.</summary>
        public static string[] FieldLines()
        {
            FieldEntry[] f = Fields();
            var lines = new string[f.Length];
            for (int k = 0; k < f.Length; k++)
                lines[k] = f[k].Block + " " + f[k].Name + " 0x" + f[k].Offset.ToString("x", CultureInfo.InvariantCulture) + " " +
                           f[k].Size.ToString(CultureInfo.InvariantCulture) + " " + f[k].Type;
            return lines;
        }

        /// <summary>The <c>format</c> lines (without the <c>format </c> prefix); the text sorts them by key.</summary>
        public static readonly string[] FormatLines =
        {
            "biome.names 0 safeShallows, 1 grassyPlateaus, 2 kelpForest, 3 dunes, 4 bloodKelp, 5 sparseReef, 6 mountains, 7 void, 8 mushroomForest, 9 grandReef, 10 underwaterIslands, 11 crashZone, 12 lostRiver_Canyon; 255 none (Subnautica's own answer)",
            "biome.rule cameraBiome from the window column under the camera: the planet's biome map (the material map's G, PlanetMaps Biome) at the surface point when it holds 1..13: G - 1; else by the surface material: Grass 6; Sand_02 0 when the seabed is <= 60 m deep, else 1; Stone_01 2; Stone_02 3; Stone_03 4; Stone_04 5; Stone_05 or deeper than 1000 m 7; GetBiome(p) outside the crater box returns names[cameraBiome] within BiomeRadiusM of the camera, else void",
            "chart.anchor chart k+1: anchor = the camera's point on the sea sphere; E0', N0' = FrameAt_k(camera), so mapdir is continuous at the camera; psi kept; O' = F_k(camera) - T horizontally (O'.y = 0), so F_k+1(camera) = F_k(camera) - T exactly; a T = 0 step re-centres and keeps the camera's SN position",
            "chart.apply SE swaps SiteBinding.Map at the tick it queues the ChartStep notice (C1, C2 and the S8a header of that tick are all under chart k+1); Subnautica never interpolates across a chartSeq change; SE ignores Subnautica-positioned input (C9 positions, C12 batches) while C7 chartSeqApplied != chartSeq",
            "chart.step chart 0, TreadmillOn only: site-v1 rhoInf > Chart0OutM -> chart 1 with T = Step0Batches*160 times the sign of the larger of |x|, |z| along that axis; chart k >= 1 with the camera at SN (x, z): |x| > BoxOuterM -> T = StepBatches*160*sign(x) along x, likewise z; |x| < BoxInnerM and |z| < BoxInnerM -> T = -StepBatches*160*sign along the larger axis; arc distance from the anchor > AnchorMaxM -> T = 0; one step per tick, an automatic step no sooner than StepGapMs after the previous one; back to chart 0 when the camera's site-v1 rhoInf <= ReturnM",
            "chart.offsite C2 flag OffSite while the chart index is >= 1; SE keeps its picture policy (Subnautica's picture below the altitude band, as on chart 0); Subnautica hides the Subnautica map's above-water landmarks (the Aurora, the crater box's terrain above the sea, the horizon skirt) while OffSite is set and restores them when it clears; on a ChartStep Subnautica serves the previous chart's window files under the shifted ids (new id = old id - T) until the new chart's notices arrive",
            "convert.copyonwrite drill layer: SN voxel v is recomputed iff its 8-sample SE stencil intersects an edited SE range of the tile overlay; else it keeps its base value (the newest store version of the batch, else the install file); T = the base type when the base voxel is solid, else the mapped SE material",
            "convert.material SE voxel material subtype -> SN block type: Sand_02 1, Stone_01 111, Stone_02 150, Stone_03 190, Stone_04 141, Stone_05 84, Grass 219, Iron_02 95, Nickel_01 96, Silicon_01 107, Cobalt_01 108, Magnesium_01 117, Silver_01 118, Gold_01 119, Platinum_01 121, Uraninite_01 122, any other 84; names resolved to indices once per session",
            "convert.orelook Subnautica's palette slots 95, 96, 107, 108, 117, 118, 119, 121 and 122 (null in the installed palette) hold this mod's ore block types for Iron_02, Nickel_01, Silicon_01, Cobalt_01, Magnesium_01, Silver_01, Gold_01, Platinum_01 and Uraninite_01 in that order: a WorldStreamer.Start prefix fills each (filled, the layer of the template rock GreyRock, its material cloned with this mod's generated ore texture); a slot the game's palette already fills is left alone (the ore then shows that type)",
            "convert.sample SN voxel v (world index) -> P = (OriginX, OriginY, OriginZ) + v + 0.5; w = F^-1(P) (site v1 for the drill layer, the current chart for the window); D = trilinear over the 8 LOD0 samples (content/127.5 - 1) around w - PositionLeftBottomCorner; s = clamp(D, -1, 1); |s| >= 1: density 0, type = s > 0 ? T : 0; else density = floor(s*126) + 126, type = s >= 0 ? T : 0",
            "feed.ack rows = SE's unacknowledged notices from C7 ackSeq + 1 of the same stream, seq consecutive from firstSeq, at most MaxPerTick new rows per tick and MaxRows in all; Subnautica applies seq > its last in order, writes ackSeq (reload scheduled) and appliedSeq (batch loaded and the collision level built over the row's box); tickLow = low 32 bits of the C2 tick; stream changes when seq restarts",
            "feed.resync a full queue (MaxPending) drops the oldest unacknowledged notices into dropped, bumps resyncEpoch and sets Resync; Subnautica then re-reads both manifests and reloads every loaded batch whose served version differs; StoreReset (new storeKey, new overlay or new chartKey) clears that layer's lookup first",
            "guard.lift while DrillOn every S4 edit guard lets the edit through on a tile: it lands in MyOctreeStorage's own leaves over the provider, never in the provider; each edited range is mirrored by WriteRange into every other tile containing it and marks the overlay cells it touches; DrillOn off = S4's guards",
            "octree.batch int32 version 4; octrees x-outer, y, z-inner; crater ids store only octrees inside (OctreesX, OctreesY, OctreesZ), window ids all 125; each a u16 node count then 4-byte nodes (type, density, u16 firstChild, 0 = leaf); child c = 4*xbit + 2*ybit + zbit",
            "octree.write 32^3 raster -> nodes bottom-up: CanCollapse (8 leaf children, not mixed below/above the surface, near children all equal) -> a leaf with the first near child's value, else child 0's; otherwise interior = CacheDownsampledType (density = integer mean of near children, else 0; type = ComputeMostCommonChildType); layout CompactOctree.SetInternal (pre-order, 8-node blocks from 1)",
            "overlay.file DE_S8a_overlay.vx2 in the SE save = deflate(magic DES8AOV1, u32 version, u32 siteVersion, u32 cellCount, cells sorted by key: u8 i, j, k, pad, u8 cx, cy, cz, pad, content[4096], material[4096] x fastest) + u64 fnv1a64 of the uncompressed bytes; cell = 16^3 LOD0 voxels of tile (i, j, k)",
            "overlay.persist postfix MySession.Save(out MySessionSnapshot, string, Action<SaveProgress>) when it returns true: snapshot.CompressedVoxelSnapshots[DE_S8a_overlay] = the file; restored in BeforeStart after the tiles by WriteRange(Content|Material, notify) under the restore flag",
            "sculpt.encode heightmap h = clamp(round((60000.00390625 + y - 57000) / 10200 * 65535), 0, 65535) for an SE surface at SN altitude y; pixel p of a 2048 face <-> texcoord p / 2047; faces 0 front, 1 back, 2 left, 3 right, 4 up, 5 down (MyCubemapHelpers); _mat R = CustomMaterialTable value (Sand_02 1 .. Stone_05 6, Grass 7), G = biome.names index + 1 (0 none), B = OreMappings Value (0 none), A = 255; PlanetMaps Material, Ores and Biome on",
            "sculpt.site every pixel whose site-v1 rhoInf <= 2450 m is 0, so the bicubic surface is exactly SN y -3000 within rhoInf 2300 m (the provider tiles); tools/planet/layout_4546b.json shapes the rest once (biome seeds scaled x3 to a 12 km ring, per-biome depth bands and relief, canyons, the Mountains island, the ore depth-rarity table; a seed varies the detail layer only); every surface outside the island stays at least 2 m below the sea, the island tops at most +70 m",
            "spawn.place window columns only, keyed by fnv1a64(chartKey, bx, bz, column, row index); seabed = the top solid voxel of the served file; within RadiusM of the camera, gone beyond DespawnM; MakeEntityTransient, never saved; moved by -T on a chart step",
            "spawn.table per hectare of seabed by depth d, Subnautica TechTypes: d <= 60 Peeper 3 Bladderfish 3 Boomerang 2 BluePalm 6 PurpleTentacle 4 CoralShellPlate 3; d <= 200 Hoopfish 3 Spadefish 2 Reginald 2 RedGrass 8 PurpleFan 3 SmallKoosh 2; d <= 500 Eyeye 2 HoleFish 2 Mesmer 1 BloodGrass 6; d <= 1000 Spinefish 2 GarryFish 1 BloodGrass 2; deeper none",
            "store.key storeKey = fnv1a64(utf8 lower(world folder path at load) + '|' + siteVersion); chartKey = fnv1a64(utf8 'chart|' + U0, N0, psiDeg, O as R round-trip doubles joined by '|' + '|' + sculptHash x16); sculptHash = fnv1a64 over 'name<TAB>bytes<TAB>fnv1a64 x16<LF>' of the 12 planet data files in ordinal order; with the ring field on (Stage F2) the window's and the charts' sculptHash is fnv1a64(utf8 sculptHash x16 + '|ring|' + ringId x16), ringId = fnv1a64(utf8 'ring|' + rules x16 + '|' + library x16 + '|' + index x16): rules = fnv1a64 of the ring rules text (RingRules.Text), library and index = fnv1a64 of library-v1.bin and index-v1.tsv in the brush store ('missing' for a file not built)",
            "ring.field Stage F2 (DEEPENGINEERS_S8A_RING=on): MyPlanetStorageProvider.ReadRange(ref MyVoxelDataRequest, bool) of the site planet serves the ring field (RingField version 2, twin tools/planet/s8a_ring3d.py) for content reads at LOD <= 3 whose box meets SN radius 3530..12270 m and the field's reach over and under the heightmap seabed; content = RingField.ServeBox at the planet-local voxel positions (storage position * 2^lod - StorageSize / 2, site v1 with C = 0, the arctan a polynomial), materials SE's own; every other read, a failure and a field not ready: SE's own generator (the heightmap), which is also the field's base",
            "store.manifest manifest.v1.txt per layer folder: line 1 'de-s8a-store 1 <drill|window> <key x16> <overlayHash|sculptHash x16>'; then '<bx> <by> <bz> <version> <bytes> <fnv1a64 x16> <flags> <file path relative to the terrain folder>' per served batch, sorted by bx, by, bz; rewritten at most once per second and at unload (temp file then replace)",
            "store.path %LOCALAPPDATA%\\DeepEngineers\\terrain\\<storeKey x16>\\drill\\ and %LOCALAPPDATA%\\DeepEngineers\\terrain\\<storeKey x16>\\window\\<chartKey x16>\\, files compiled-batch-<bx>-<by>-<bz>.v<version>.optoctrees and manifest.v1.txt; a version is never overwritten; nothing inside either game install or SNAppData",
            "window.center BatchOctreesStreamer constructor prefix, minLod 0 only: settings.centerMin = (UniverseMin, 0, UniverseMin), settings.centerMax = (UniverseMax, 20, UniverseMax); octreeBounds unchanged",
            "window.loader BatchOctrees.LoadOctrees prefix for ids outside the crater box (bx or bz outside CraterMin..CraterMax): read GetPath(id), version >= FileVersion, all 125 octrees in file order (Octree.Read, then UnloadChildren(streamer.minLod)); a missing or bad file answers false (empty water); crater ids run the original",
            "window.ready NearPending while a window id meeting the camera's +-NearBoxM box is neither written nor known empty and SE's surface is within NearSurfaceM of the camera; Subnautica's areaBuilt = its own value and not NearPending and every window notice meeting that box applied; NearEmpty (all such ids empty or absent) lets areaBuilt ignore unbuilt empty clipmap cells",
            "window.set c = the octree ring centre as WorldStreamer.UpdateStreamingCenter computes it (camera - StreamCenterDownM in y, FloorDiv 160, clamped to the widened centre); ids within RingBatches^3 around c plus LookAheadBatches ahead along the horizontal velocity when |v| >= LookAheadMinSpeedCmS; crater ids excluded; nearest first",
        };

        private static string _text;
        private static ulong _hash;

        /// <summary>
 /// The canonical text (CONTRACT §5.4 "S8a conventions"; ASCII, '\n' after every line, the last included):
 /// <see cref="Header"/>; one <c>const &lt;group&gt; &lt;name&gt; &lt;value&gt;</c> line per constant sorted by group
 /// (ordinal), then value, then name; the <c>field</c> lines in their listed order; the <c>format</c> lines sorted
 /// (ordinal).
 /// </summary>
        public static string CanonicalText()
        {
            if (_text != null) return _text;
            ConstEntry[] e = Entries();
            Array.Sort(e, (a, b) =>
            {
                int c = string.CompareOrdinal(a.Group, b.Group);
                if (c != 0) return c;
                c = a.Value.CompareTo(b.Value);
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
            var sb = new StringBuilder(12288);
            sb.Append(Header).Append('\n');
            foreach (ConstEntry c in e)
                sb.Append("const ").Append(c.Group).Append(' ').Append(c.Name).Append(' ')
                  .Append(c.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (string f in FieldLines()) sb.Append("field ").Append(f).Append('\n');
            var formats = new List<string>(FormatLines);
            formats.Sort(string.CompareOrdinal);
            foreach (string f in formats) sb.Append("format ").Append(f).Append('\n');
            _text = sb.ToString();
            return _text;
        }

        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/> (compiled; equals <see cref="PinnedHash"/> when nothing drifted).</summary>
        public static ulong Hash
        {
            get
            {
                if (_hash == 0) _hash = LayoutHash.Fnv1a64Ascii(CanonicalText());
                return _hash;
            }
        }
    }
}
