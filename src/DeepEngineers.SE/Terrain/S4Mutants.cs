using System.Collections.Generic;
using DeepEngineers.Terrain;

namespace DeepEngineers.SE.Terrain
{
    /// <summary>
 /// Seeded failures of slice S4 "Planet + terrain" on the SE side (ROADMAP S4 "Seeded failures", §3 rule 1). Dev builds
 /// only; switched through <see cref="Mutants.Set"/> (environment variable <c>DEEPENGINEERS_MUTANTS</c> at Init, C11
 /// <c>de.mutant</c>, or <c>/de mutant</c>). Game-independent, so tests/DeepEngineers.SE.Tests can turn them on.
 /// <para>Tiles are created once per world load in <c>BeforeStart</c>: S4.M2, S4.M4, S4.M5 and S4.M10 act only at the
 /// next world load (set them through <c>DEEPENGINEERS_MUTANTS</c> at launch). S4.M4 and S4.M10 additionally act only
 /// in a world whose autosave is off (<c>AutoSaveInMinutes = 0</c>: the throwaway copy of ROADMAP rule 10); elsewhere they
 /// log a refusal and the tiles are created normally. The others act at once.</para>
 /// <list type="bullet">
 /// <item>S4.M2 tile shifted 2 m: every tile is placed 2 m further out radially (+2 × U0 on its world matrix) while its
 /// provider samples the true lattice point → S4.A-T1 (1) red.</item>
 /// <item>S4.M3 <c>Intersect</c> wrongly Disjoint for every box touching the 8 m cell holding F⁻¹(0, −17.2, 0) → SE's
 /// grid/floating-object prediction (<c>MyVoxelPhysicsBody.UpdateAfterSimulation10</c>, the only caller of
 /// <c>Storage.Intersect</c> on the physics path, VERIFIED) empties that cell: a grid dropped at SN (0, 0) would fall
 /// through, and every at1 ray whose storage surface lies in it reports <c>intersectHole</c>, which the analyzer judges
 /// as S4.A-T1.1i; the rays' own prefetch jobs (<c>CreateMesh</c>) never ask Intersect, so the
 /// cast alone does not show it.</item>
 /// <item>S4.M4 tiles created with <c>Save = true</c> (throwaway copy) → S4.G2-E2 finds the storage in the save.</item>
 /// <item>S4.M5 source check against a wrong manifest (expected files + 1) → <c>DE.Terrain REFUSE</c>, no tiles,
 /// fail-safe.</item>
 /// <item>S4.M6 provider samples shifted half a voxel (the core's <see cref="TerrainMutants.HalfVoxelShift"/>) →
 /// S4.A-T0b red.</item>
 /// <item>S4.M7 A-T1 harness without the prefetch (and without the wait) → the harness reports unbuilt cells.</item>
 /// <item>S4.M8 the request path sets <c>EmptyData</c> (with <c>Target</c> set) on the M3 cell's requests (the request
 /// box contains the cell centre) → the mesher skips the cell: S4.A-T1 finds the hole.</item>
 /// <item>S4.M8b the <c>detectOnly</c> path sets <c>EmptyData</c> on every box touching the M3 cell → only S4.N3 red
 /// (detectOnly runs on edit paths only).</item>
 /// <item>S4.M9.&lt;path&gt; one edit guard removed (cutout, shipdrill, explosion, meteor, voxelhand, writerange) →
 /// S4.N2 red for that path. Acts only in a world whose autosave is off:
 /// elsewhere the guard keeps blocking and logs <c>DE.Mutant REFUSE S4.M9.&lt;path&gt;</c> once.</item>
 /// <item>S4.M10 random tile entity ids (throwaway copy) → S4.G2-E3b red (gear unlocked after reload).</item>
 /// <item>S4.M1a–d are offline A-T0 mutants of the core (tests/DeepEngineers.Terrain.Tests): refused here.</item>
 /// </list>
 /// </summary>
    internal static class S4Mutants
    {
        /// <summary>S4.M2.</summary>
        public static volatile bool TileShift2m;
        /// <summary>S4.M3.</summary>
        public static volatile bool IntersectHole;
        /// <summary>S4.M4.</summary>
        public static volatile bool SaveTrue;
        /// <summary>S4.M5.</summary>
        public static volatile bool WrongManifest;
        /// <summary>S4.M7.</summary>
        public static volatile bool NoPrefetch;
        /// <summary>S4.M8.</summary>
        public static volatile bool EmptyDataOnSurface;
        /// <summary>S4.M8b.</summary>
        public static volatile bool EmptyDataDetectOnly;
        /// <summary>S4.M10.</summary>
        public static volatile bool RandomIds;

        /// <summary>S4.M9.&lt;path&gt;: bit i = guard path <see cref="TerrainPaths.Names"/>[i] removed.</summary>
        private static int _guardOff;

        /// <summary>True when S4.M9.&lt;path&gt; removed the guard of path index <paramref name="pathIndex"/>.</summary>
        public static bool GuardOff(int pathIndex)
        {
            return (System.Threading.Volatile.Read(ref _guardOff) & (1 << pathIndex)) != 0;
        }

        public static string Active
        {
            get
            {
                var parts = new List<string>(8);
                if (TerrainMutants.SignFlip) parts.Add("S4.M1a");
                if (TerrainMutants.OneVoxelOffset) parts.Add("S4.M1b");
                if (TerrainMutants.SwapXZ) parts.Add("S4.M1c");
                if (TerrainMutants.ChildOrderZYX) parts.Add("S4.M1d");
                if (TileShift2m) parts.Add("S4.M2");
                if (IntersectHole) parts.Add("S4.M3");
                if (SaveTrue) parts.Add("S4.M4");
                if (WrongManifest) parts.Add("S4.M5");
                if (TerrainMutants.HalfVoxelShift) parts.Add("S4.M6");
                if (NoPrefetch) parts.Add("S4.M7");
                if (EmptyDataOnSurface) parts.Add("S4.M8");
                if (EmptyDataDetectOnly) parts.Add("S4.M8b");
                int g = System.Threading.Volatile.Read(ref _guardOff);
                for (int i = 0; i < TerrainPaths.Names.Length; i++)
                    if ((g & (1 << i)) != 0) parts.Add("S4.M9." + TerrainPaths.Names[i]);
                if (RandomIds) parts.Add("S4.M10");
                return string.Join(",", parts.ToArray());
            }
        }

        public static void ClearAll()
        {
            TileShift2m = false;
            IntersectHole = false;
            SaveTrue = false;
            WrongManifest = false;
            NoPrefetch = false;
            EmptyDataOnSurface = false;
            EmptyDataDetectOnly = false;
            RandomIds = false;
            System.Threading.Volatile.Write(ref _guardOff, 0);
            TerrainMutants.ClearAll();
        }

        /// <summary>Switches an S4 mutant; returns the log text (starting with <c>S4.</c>), a REFUSE text, or null when not an S4 id.</summary>
        public static string Set(string id, bool on)
        {
            string key = (id ?? "").Trim().ToUpperInvariant();
            string state = on ? "ON" : "OFF";
            const string nextLoad = " (acts at the next world load: the tiles are created in BeforeStart; set it through DEEPENGINEERS_MUTANTS at launch)";
            switch (key)
            {
                case "S4.M2":
                    TileShift2m = on;
                    return "S4.M2 " + state + " (every tile placed +2 m radially while its provider samples the true lattice point)" + nextLoad;
                case "S4.M3":
                    IntersectHole = on;
                    return "S4.M3 " + state + " (Intersect(box) answers Disjoint for every box touching the 8 m cell at F^-1(0, -17.2, 0))";
                case "S4.M4":
                    SaveTrue = on;
                    return "S4.M4 " + state + " (tiles created with Save = true; throwaway copy with autosave off only)" + nextLoad;
                case "S4.M5":
                    WrongManifest = on;
                    return "S4.M5 " + state + " (source manifest compared against expected files + 1: REFUSE and fail-safe)" + nextLoad;
                case "S4.M6":
                    TerrainMutants.HalfVoxelShift = on;
                    return "S4.M6 " + state + " (provider samples shifted +0.5 m on every axis; cells SE already built keep their content until rebuilt)";
                case "S4.M7":
                    NoPrefetch = on;
                    return "S4.M7 " + state + " (A-T1 harness casts without PrefetchShapeOnRay and without waiting for the cells)";
                case "S4.M8":
                    EmptyDataOnSurface = on;
                    return "S4.M8 " + state + " (ReadRange request path sets EmptyData on the M3 cell's requests, bytes unchanged)";
                case "S4.M8B":
                    EmptyDataDetectOnly = on;
                    return "S4.M8b " + state + " (ReadRange detectOnly sets EmptyData on every box touching the M3 cell)";
                case "S4.M10":
                    RandomIds = on;
                    return "S4.M10 " + state + " (tiles created with random entity ids; throwaway copy with autosave off only)" + nextLoad;
                case "S4.M9":
                    return "REFUSE S4.M9: name the path: S4.M9.cutout|shipdrill|explosion|meteor|voxelhand|writerange";
                case "S4.M1A":
                case "S4.M1B":
                case "S4.M1C":
                case "S4.M1D":
                    return "REFUSE " + id.Trim() + ": an offline A-T0 mutant of the core reader (tests/DeepEngineers.Terrain.Tests), not switchable in SE";
            }
            if (key.StartsWith("S4.M9.", System.StringComparison.Ordinal))
            {
                string path = key.Substring(6).ToLowerInvariant();
                int idx = TerrainPaths.IndexOf(path);
                if (idx < 0) return "REFUSE " + id.Trim() + ": unknown guard path (cutout|shipdrill|explosion|meteor|voxelhand|writerange)";
                int bit = 1 << idx;
                int cur, next;
                do
                {
                    cur = System.Threading.Volatile.Read(ref _guardOff);
                    next = on ? cur | bit : cur & ~bit;
                }
                while (System.Threading.Interlocked.CompareExchange(ref _guardOff, next, cur) != cur);
                return "S4.M9." + path + " " + state + " (the " + path + " edit guard lets edits of the DE tiles through; only in a world with autosave off, else refused at the edit)";
            }
            return null;
        }
    }
}
