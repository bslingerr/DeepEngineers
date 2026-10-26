using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using DeepEngineers.Terrain;
using DeepEngineers.Wire;

namespace DeepEngineers.SE.Terrain
{
    /// <summary>The edit-guard paths of CONTRACT §3.1 "Edit guards", in a fixed order (S4.M9.&lt;path&gt;, counters).</summary>
    internal static class TerrainPaths
    {
        public const int Cutout = 0, ShipDrill = 1, Explosion = 2, Meteor = 3, VoxelHand = 4, WriteRange = 5;
        public static readonly string[] Names = { "cutout", "shipdrill", "explosion", "meteor", "voxelhand", "writerange" };

        public static int IndexOf(string name)
        {
            if (name == null) return -1;
            for (int i = 0; i < Names.Length; i++) if (string.Equals(Names[i], name.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }

    /// <summary>
 /// The SE terrain gate's decision table (CONTRACT §1.2 row "SE terrain", conditions 1–6; condition 7 is decided by
 /// the tile creation itself): game-independent half of <see cref="TerrainGate"/>, so the decisions are unit-tested.
 /// </summary>
    internal static partial class TerrainGate
    {
        public enum Result { NotEvaluated, Skip, Ok, Refuse }

        /// <summary>What the SE half gathered (game thread, BeforeStart).</summary>
        internal struct Facts
        {
            /// <summary>(1) the global gate's predicate (offline single player), evaluated early.</summary>
            public bool GlobalOk;
            public string GlobalWhy;
            /// <summary>(2) <c>SiteBinding.PlanetCount</c>.</summary>
            public int PlanetCount;
            /// <summary>(3) source manifest equal to the pins.</summary>
            public bool ManifestOk;
            public string ManifestField, ManifestFound, ManifestExpected;
            /// <summary>(4) id 45460 registered to SnTerrainProvider.</summary>
            public bool ProviderRegistered;
            public string ProviderError;
            /// <summary>(5) null when every SE member the tiles need resolved; else the missing ones.</summary>
            public string MissingMembers;
            /// <summary>(6) null when all 34 entity ids are free; else the first taken one.</summary>
            public string IdTaken;
        }

        internal struct Decision
        {
            public Result Result;
            /// <summary>Condition name: multiplayer, planet, source, provider, members, ids (Ok: "").</summary>
            public string Condition;
            public string Reason;
            /// <summary>True for every REFUSE (conditions 3–7): the fail-safe of CONTRACT §7 goes on.</summary>
            public bool FailSafe;
            /// <summary>The log line (SKIP/REFUSE; null for Ok, whose line carries the tile count).</summary>
            public string Line;
        }

        /// <summary>CONTRACT §1.2 conditions 1–6 in order; the first failing one decides.</summary>
        internal static Decision Decide(Facts f)
        {
            if (!f.GlobalOk) return Skip("multiplayer", "multiplayer " + (f.GlobalWhy ?? ""));
            if (f.PlanetCount == 0) return Skip("planet", "no " + SiteV1.GeneratorSubtypeId);
            if (f.PlanetCount >= 2) return Skip("planet", "planets=" + f.PlanetCount.ToString(CultureInfo.InvariantCulture));
            if (!f.ManifestOk) return Refuse("source", (f.ManifestField ?? "?") + " found=" + (f.ManifestFound ?? "?") + " expected=" + (f.ManifestExpected ?? "?"));
            if (!f.ProviderRegistered) return Refuse("provider", f.ProviderError ?? "id " + SnProviderId + " not registered");
            if (f.MissingMembers != null) return Refuse("members", f.MissingMembers);
            if (f.IdTaken != null) return Refuse("ids", f.IdTaken);
            return new Decision { Result = Result.Ok, Condition = "", Reason = "conditions 1-6 hold" };
        }

        /// <summary>The provider id (CONTRACT §3.1 "Identity and registration").</summary>
        public const int SnProviderId = 45460;

        private static Decision Skip(string condition, string reason)
        {
            return new Decision { Result = Result.Skip, Condition = condition, Reason = reason, FailSafe = false, Line = "DE.Terrain SKIP " + reason };
        }

        internal static Decision Refuse(string condition, string reason)
        {
            return new Decision { Result = Result.Refuse, Condition = condition, Reason = reason, FailSafe = true, Line = "DE.Terrain REFUSE " + condition + " " + reason };
        }
    }

    /// <summary>
 /// The tile lattice of CONTRACT §3 (<c>terrain.*</c> rows) in plain doubles: min corners, world translation (min
 /// corner + 512, identity rotation), storage names, ids (through <see cref="SiteV1"/>), the S4.M2 shift and the S4
 /// mutant cell.
 /// </summary>
    internal static class TerrainLattice
    {
        public const int TileCount = 34;
        public const double HalfSize = SiteV1.Tile / 2.0;
        /// <summary>The SN point whose 8 m cell S4.M3/M8/M8b break (the Safe Shallows seabed near the anchor).</summary>
        public static readonly Vec3 MutantCellSn = new Vec3(0.0, -17.2, 0.0);

        /// <summary>tileMin = C + the lattice min corner of slot (i, j, k) (doubles).</summary>
        public static Vec3 TileMin(Vec3 c, int i, int j, int k)
        {
            return c + SiteV1.TileMinRelative(i, j, k);
        }

        /// <summary>Where the map's min corner is placed: tileMin, or tileMin + 2·U0 under S4.M2.</summary>
        public static Vec3 PlaceMin(Vec3 tileMin, bool shift2m)
        {
            return shift2m ? tileMin + SiteV1.AnchorU0 * 2.0 : tileMin;
        }

        /// <summary><c>MatrixD.CreateTranslation(placeMin + (512, 512, 512))</c>'s translation.</summary>
        public static Vec3 WorldTranslation(Vec3 placeMin)
        {
            return placeMin + new Vec3(HalfSize, HalfSize, HalfSize);
        }

        /// <summary><c>DE.Terrain.&lt;i&gt;.&lt;j&gt;.&lt;k&gt;.s&lt;siteVersion&gt;</c>.</summary>
        public static string StorageName(int i, int j, int k)
        {
            return "DE.Terrain." + i.ToString(CultureInfo.InvariantCulture) + "." + j.ToString(CultureInfo.InvariantCulture) + "." +
                   k.ToString(CultureInfo.InvariantCulture) + ".s" + SiteV1.SiteVersion.ToString(CultureInfo.InvariantCulture);
        }

        public static string IdHex(long id)
        {
            return "0x" + id.ToString("x16", CultureInfo.InvariantCulture);
        }

        /// <summary>True when world point <paramref name="w"/> lies in [0, 1024)³ of the tile with min <paramref name="tileMin"/>.</summary>
        public static bool Inside(Vec3 tileMin, Vec3 w)
        {
            Vec3 l = w - tileMin;
            return l.X >= 0 && l.Y >= 0 && l.Z >= 0 && l.X < SiteV1.Tile && l.Y < SiteV1.Tile && l.Z < SiteV1.Tile;
        }

        /// <summary>The tile-local min corner of the 8 m cell holding F⁻¹(<see cref="MutantCellSn"/>), when that point is in the tile.</summary>
        public static bool MutantCell(SiteMap map, Vec3 tileMin, out int x0, out int y0, out int z0)
        {
            Vec3 l = map.SnToSe(MutantCellSn) - tileMin;
            x0 = (int)Math.Floor(l.X / ProviderCore.MutantCellSize) * ProviderCore.MutantCellSize;
            y0 = (int)Math.Floor(l.Y / ProviderCore.MutantCellSize) * ProviderCore.MutantCellSize;
            z0 = (int)Math.Floor(l.Z / ProviderCore.MutantCellSize) * ProviderCore.MutantCellSize;
            return l.X >= 0 && l.Y >= 0 && l.Z >= 0 && l.X < SiteV1.Tile && l.Y < SiteV1.Tile && l.Z < SiteV1.Tile;
        }

        /// <summary>
 /// S4.M4/S4.M10 act only in a throwaway copy (ROADMAP rule 10): a world whose autosave is off
 /// (<c>Settings.AutoSaveInMinutes == 0</c>). Returns null when allowed, else the refusal text.
 /// </summary>
        public static string ThrowawayRefusal(string mutant, uint autoSaveInMinutes)
        {
            return autoSaveInMinutes == 0 ? null
                : "REFUSE " + mutant + ": autosave is on (" + (autoSaveInMinutes == uint.MaxValue ? "unknown" : autoSaveInMinutes.ToString(CultureInfo.InvariantCulture) + " min") +
                  "); only on a throwaway world copy with autosave off (tools/lab/make_terrain_copy.py); tiles created normally";
        }

        /// <summary>
 /// S4.M9.&lt;path&gt;: a removed guard lets real edits of the tiles through (ore, debris, an
 /// autosave could keep their effects), so it acts only in a throwaway copy, like S4.M4 and S4.M10. Null when the
 /// edit may pass, else the refusal text (the guard then blocks as usual).
 /// </summary>
        public static string GuardOffRefusal(int path, uint autoSaveInMinutes)
        {
            string id = "S4.M9." + (path >= 0 && path < TerrainPaths.Names.Length ? TerrainPaths.Names[path] : "?");
            string r = ThrowawayRefusal(id, autoSaveInMinutes);
            return r == null ? null : r.Replace("tiles created normally", "the guard keeps blocking");
        }

        /// <summary>
 /// The batch slots under the tiles, nearest <paramref name="focusSn"/> first: a batch is under the tiles when one of 27 points of its SN box (corners, edge and face
 /// centres, centre) maps through F into one of the tile boxes <c>[min, min + 1024)³</c>.
 /// </summary>
        public static int[] PrefetchPlan(SiteMap map, Vec3[] tileMins, Vec3 focusSn)
        {
            const double K = SnOctreeFormat.BatchVoxels;
            var slots = new List<int>(4096);
            var keys = new List<double>(4096);
            for (int bx = 0; bx < SnOctreeFormat.BatchesX; bx++)
                for (int by = 0; by < SnOctreeFormat.BatchesY; by++)
                    for (int bz = 0; bz < SnOctreeFormat.BatchesZ; bz++)
                    {
                        double x0 = SnOctreeFormat.OriginX + bx * K, y0 = SnOctreeFormat.OriginY + by * K, z0 = SnOctreeFormat.OriginZ + bz * K;
                        bool under = false;
                        for (int f = 0; f < 27 && !under; f++)
                        {
                            Vec3 w = map.SnToSe(new Vec3(x0 + (f % 3) * K / 2, y0 + (f / 3 % 3) * K / 2, z0 + (f / 9) * K / 2));
                            foreach (Vec3 m in tileMins)
                                if (Inside(m, w)) { under = true; break; }
                        }
                        if (!under) continue;
                        double dx = x0 + K / 2 - focusSn.X, dy = y0 + K / 2 - focusSn.Y, dz = z0 + K / 2 - focusSn.Z;
                        slots.Add(SnOctreeFormat.BatchSlot(bx, by, bz));
                        keys.Add(dx * dx + dy * dy + dz * dz);
                    }
            int[] a = slots.ToArray();
            Array.Sort(keys.ToArray(), a);
            return a;
        }
    }

    /// <summary>
 /// The fail-safe's state (CONTRACT §7 "Tiles cannot be created in a DE world"): never fail-open, for the rest of the
 /// session (a reload evaluates again). Game-independent half; the HUD warning and the save block are in the SE half.
 /// </summary>
    internal static partial class TerrainFailSafe
    {
        public const string HudText = "DeepEngineers terrain missing: saving disabled";
        /// <summary>CONTRACT §7 footprint: max(|x|, |z|) ≤ 2,100 m and y &lt; 0 (SN).</summary>
        public const double FootprintHalf = 2100.0;

        private static volatile bool _active;
        private static volatile string _reason;
        private static long _saveBlocked;

        /// <summary><c>seFlags.failSafeActive</c> (bit 3).</summary>
        public static bool Active { get { return _active; } }
        public static string Reason { get { return _reason; } }
        public static long SaveBlocked { get { return Interlocked.Read(ref _saveBlocked); } }

        /// <summary>A grid at SN point <paramref name="sn"/> is in the footprint (the S8 grid hold applies to it).</summary>
        public static bool InFootprint(Vec3 sn)
        {
            return Math.Max(Math.Abs(sn.X), Math.Abs(sn.Z)) <= FootprintHalf && sn.Y < 0;
        }

        /// <summary>Turns the state on; false when it already was (the first reason stays).</summary>
        internal static bool SetActive(string reason)
        {
            if (_active) return false;
            _reason = reason;
            _active = true;
            return true;
        }

        internal static long CountSaveBlocked()
        {
            return Interlocked.Increment(ref _saveBlocked);
        }

        /// <summary>World unload: a reload evaluates the gate again.</summary>
        internal static void ResetSession()
        {
            _active = false;
            _reason = null;
            Interlocked.Exchange(ref _saveBlocked, 0);
        }
    }

    /// <summary>Game-independent half of the edit guards: per-path counters and the once-per-second log limit (thread-safe).</summary>
    internal static partial class TerrainGuards
    {
        public const long LogIntervalMs = 1000;
        private static readonly long[] _counts = new long[6];
        private static readonly long[] _nextLogMs = new long[6];

        public static long Count(string path)
        {
            int i = TerrainPaths.IndexOf(path);
            return i < 0 ? 0 : Interlocked.Read(ref _counts[i]);
        }

        public static long Count(int path)
        {
            return Interlocked.Read(ref _counts[path]);
        }

        /// <summary>Counts one blocked edit of <paramref name="path"/>; true when this call is the one that logs (≤ 1 per second per path).</summary>
        internal static bool RecordBlocked(int path, long nowMs, out long count)
        {
            count = Interlocked.Increment(ref _counts[path]);
            long next = Interlocked.Read(ref _nextLogMs[path]);
            if (nowMs < next) return false;
            return Interlocked.CompareExchange(ref _nextLogMs[path], nowMs + LogIntervalMs, next) == next;
        }

        /// <summary>Tests only.</summary>
        internal static void ResetCounts()
        {
            for (int i = 0; i < _counts.Length; i++)
            {
                Interlocked.Exchange(ref _counts[i], 0);
                Interlocked.Exchange(ref _nextLogMs[i], 0);
            }
        }
    }
}
