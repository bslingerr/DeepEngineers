using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using UnityEngine;
using WorldStreaming;

namespace DeepEngineers.SN
{
    /// <summary>
 /// terrain 4 "chart.
 /// vertically"): while SE's C2 S8a header carries <c>OffSite</c> (a treadmill chart), SE keeps Subnautica's picture above the
 /// sea and this side hides what belongs to the Subnautica map, which the chart no longer puts under the camera's sky:
 /// <list type="bullet">
 /// <item>the landmarks (<see cref="S7Landmarks.Set"/>: the Aurora, its effects and story), their state restored on chart 0;</item>
 /// <item>the horizon skirt (<see cref="Active"/>, read by the altitude host's frame);</item>
 /// <item>the crater box's terrain above the sea: every <see cref="RescanMs"/> the clipmap chunks of levels 1-3 over the crater
 /// box whose cells reach above the sea are hidden
 /// and shown again on chart 0
 /// when the cell still holds that chunk and is visible. The camera stays at least BoxInnerM - 2,048 = 1,152 m from the box on
 /// charts &gt;= 1, so its underwater terrain is never in view.</item>
 /// </list>
 /// Main thread; never throws.
 /// </summary>
    internal static class S8aOffSite
    {
        public const ulong RescanMs = 500;
        /// <summary>Above-the-sea cells: block y from the sea (3,040) to this many blocks above it.</summary>
        public const int AboveSeaBlocks = 320;

        public static bool Active { get; private set; }
        public static long Enters, Leaves, ChunksHidden, ChunksShown, Errors;

        private static bool _landmarksWere;
        private static ulong _nextScanMs;
        private static readonly Dictionary<ClipmapCell, ClipmapChunk> Hidden = new Dictionary<ClipmapCell, ClipmapChunk>();

        public static void Update(ulong nowMs, bool offSite)
        {
            try
            {
                if (offSite && !Active)
                {
                    Active = true;
                    Enters++;
                    _landmarksWere = S7Landmarks.Hidden;
                    S7Landmarks.Set(true, "s8a off-site chart");
                    _nextScanMs = 0;
                    DeLog.Info("DE.S8a off-site: the Subnautica map's landmarks, its terrain above the sea and the horizon skirt hidden (chart.offsite)");
                }
                else if (!offSite && Active)
                {
                    Active = false;
                    Leaves++;
                    S7Landmarks.Set(_landmarksWere, "s8a back on the site chart");
                    ShowAll();
                    DeLog.Info("DE.S8a on site again: landmarks " + (_landmarksWere ? "hidden" : "shown") + " as before, " + ChunksShown + " chunks shown");
                }
                if (Active && nowMs >= _nextScanMs)
                {
                    _nextScanMs = nowMs + RescanMs;
                    HideCraterAboveSea();
                }
            }
            catch (Exception e)
            {
                Errors++;
                if (Errors <= 3) DeLog.Warn("DE.S8a off-site " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void HideCraterAboveSea()
        {
            LargeWorldStreamer lws = LargeWorldStreamer.main;
            WorldStreamer ws = lws != null ? lws.streamerV2 : null;
            if (ws == null || ws.clipmapStreamer == null) return;
            int[] mins = { 0, 3040 - 32, 0 }, maxs = { 4095, 3040 + AboveSeaBlocks, 4095 };   // the crater box in blocks (SN -2,048..2,048)
            for (int l = 1; l < 4; l++)
            {
                ClipmapLevel lv = ws.clipmapStreamer.GetLevel(l);
                if (lv == null) continue;
                int[] cmin, cmax;
                S8aCellRange.Range(l, lv.cellSize, mins, maxs, out cmin, out cmax);
                int seaCellTop = S8aCellRange.FloorDiv(3040, lv.cellSize);
                for (int x = cmin[0]; x <= cmax[0]; x++)
                    for (int y = Math.Max(cmin[1], seaCellTop); y <= cmax[1]; y++)
                        for (int z = cmin[2]; z <= cmax[2]; z++)
                        {
                            ClipmapCell cell = lv.GetCell(new Int3(x, y, z));
                            if (cell == null) continue;
                            ClipmapChunk chunk = S8aLiveEdit.ChunkOf(cell);
                            if (chunk == null) continue;
                            ClipmapChunk had;
                            if (!Hidden.TryGetValue(cell, out had) || had != chunk) ChunksHidden++;
                            chunk.HideTerrain();          // every scan: the visibility updater may have shown it again
                            chunk.HideGrass();
                            Hidden[cell] = chunk;
                        }
            }
        }

        private static void ShowAll()
        {
            foreach (KeyValuePair<ClipmapCell, ClipmapChunk> kv in Hidden)
            {
                if (S8aLiveEdit.ChunkOf(kv.Key) != kv.Value || S8aLiveEdit.CellState(kv.Key) != 6) continue;   // rebuilt or not visible: its own state stands
                kv.Value.Show();
                ChunksShown++;
            }
            Hidden.Clear();
        }
    }
}
