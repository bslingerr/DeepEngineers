using System;
using System.Collections.Generic;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // CONTRACT §5.4 "S7t impostors"): the S5
 // mirror's vertical reach. With the picture band back at 1 km, a ship parked in SE's sky above the band must be a shape
 // in Subnautica's view: grids above the picture camera out to ~5 km up ride the S5 grid stream as coarse impostors.
 // The S7a mirror radii (enter 1,500 m above the sea, leave 1,600 m) stay the horizontal rule; this adds a vertical
 // cylinder above the camera. An impostor is a version-1 GridAdd with bit 6 (`Impostor` = 64; the free bit after S5's
 // 0-2, S7a's Ghost 3 and S7p's Projection 4 / Missile 5, outside the version nibble; read only on a version-1 GridAdd):
 // large grids only, cube parts and fat blocks only (no edges, no subparts, no subgrids), no skin, no rooms, no lights, no
 // effects (SE's light rows reach 60 m and its effect rows 250 m: none by construction), capped per query and in total.
 // Subnautica draws it coarse and does not cull it by the S7a radius. A GridAdd that
 // clears the bit for a known id is the upgrade (the grid came within the S7a radius: the parts held back follow); a
 // GridAdd that sets it is the demotion (the parts already sent stay, nothing finer follows). Pure, replay-exact, no game
 // or Unity type; pinned by Wire.Tests S7tImpostorTests.
 // =================================================================================================================

 /// <summary>The S7t impostor conventions (the GridAdd bit, the reach, the caps) and their pure rules.</summary>
    public static class S7tImpostorRules
    {
        /// <summary>GridAdd <c>flags</c> bit 6 (S7t T3): a coarse impostor of a grid above the picture camera.</summary>
        public const uint GridFlagImpostor = 1u << 6;

        /// <summary>The live toggle (SE): <c>de.s7t {impostors: on|off}</c>, env <c>DEEPENGINEERS_IMPOSTORS</c> (default on).</summary>
        public const string EnvVar = "DEEPENGINEERS_IMPOSTORS";

        /// <summary>Horizontal rule = the S7a mirror radii above water (enter 1,500 m, leave 1,600 m; <see cref="S7aConventions.MirrorRadius"/>).</summary>
        public const double HorizontalEnterM = 1500.0, HorizontalLeaveM = 1600.0;
        /// <summary>The vertical reach above the picture camera: in at ≤ 5,000 m up, out above 5,500 m.</summary>
        public const double UpEnterM = 5000.0, UpLeaveM = 5500.0;
        /// <summary>An impostor the camera climbed past leaves once it is wholly more than this below the camera.</summary>
        public const double BelowLeaveM = 100.0;

        /// <summary>Impostors at most (hierarchy roots), new ones per query, the query period (ticks), the block cap and the
 /// held-back parts flushed per tick after an upgrade.</summary>
        public const int MaxImpostors = 16, MaxAdmitsPerQuery = 2, QueryEveryTicks = 30, MaxBlocks = 30000, MaxFlushPerTick = 512;

        /// <summary>The sphere the game query uses: the reach cylinder (radius <see cref="HorizontalLeaveM"/>, from
 /// −<see cref="BelowLeaveM"/> to +<see cref="UpLeaveM"/>) inside a sphere centred <see cref="QueryCentreUpM"/> above the camera.</summary>
        public const double QueryCentreUpM = (UpLeaveM - BelowLeaveM) / 2.0;

        public static double QueryRadiusM
        {
            get
            {
                double half = (UpLeaveM + BelowLeaveM) / 2.0;
                return Math.Sqrt(half * half + HorizontalLeaveM * HorizontalLeaveM) + 1.0;
            }
        }

        /// <summary>A version-1 GridAdd's flags with or without the impostor bit.</summary>
        public static uint GridFlags(uint flags, bool impostor)
        {
            return impostor ? flags | GridFlagImpostor : flags & ~GridFlagImpostor;
        }

        /// <summary>True for a version-1 GridAdd carrying the impostor bit (a legacy or rejected record never is one).</summary>
        public static bool IsImpostor(uint gridFlags)
        {
            return MirrorConventions.Classify(gridFlags) == MirrorRecordVersion.Version1 && (gridFlags & GridFlagImpostor) != 0;
        }

        /// <summary>The part classes an impostor carries: cube parts (armour) and fat blocks (the silhouette); never edges or subparts.</summary>
        public static bool PartAllowed(uint partFlags)
        {
            uint c = MirrorConventions.EffectivePartClass(partFlags);
            return c == MirrorPartClass.CubePart || c == MirrorPartClass.FatBlock;
        }

        /// <summary>
 /// The offset <paramref name="d"/> (entity − camera) split along the local up <paramref name="up"/> (unit; the
 /// planet's radial at the camera): <paramref name="upM"/> = d·up, <paramref name="horizontalM"/> = |d − up·(d·up)|.
 /// </summary>
        public static void Split(Vec3 d, Vec3 up, out double horizontalM, out double upM)
        {
            upM = Vec3.Dot(d, up);
            Vec3 h = d - up * upM;
            horizontalM = h.Length;
        }

        /// <summary>
 /// The reach test of an entity's bounding sphere (centre offset split by <see cref="Split"/>, radius
 /// <paramref name="radiusM"/>), with hysteresis: a candidate enters when its horizontal clearance is ≤ 1,500 m and the
 /// sphere reaches from the camera's level up to 5,000 m; a member stays while the clearance is ≤ 1,600 m, the sphere
 /// still reaches above −100 m and starts below +5,500 m. Non-finite input is outside.
 /// </summary>
 /// <summary>
 /// R1: the reach as a column from the sea up to the top — a ship on the water 1.4 km out is in
 /// SE's picture at the +1,000 m band, so Subnautica keeps it as an impostor through the crossfade: the sphere must reach
 /// above −(<paramref name="camAltM"/> + <see cref="BelowLeaveM"/>) (the sea, with the member margin) instead of the
 /// camera's level. <paramref name="camAltM"/> is the camera's height above the sea (≥ 0; under water 0).
 /// </summary>
        public static bool InReach(double horizontalM, double upM, double radiusM, bool member, double camAltM)
        {
            if (double.IsNaN(camAltM) || double.IsInfinity(camAltM)) return false;
            if (double.IsNaN(horizontalM) || double.IsNaN(upM) || double.IsNaN(radiusM) || double.IsInfinity(horizontalM) || double.IsInfinity(upM)) return false;
            double r = radiusM > 0.0 && !double.IsInfinity(radiusM) ? radiusM : 0.0;
            double clearance = Math.Max(0.0, horizontalM - r);
            double top = upM + r, bottom = upM - r;
            double floor = -(Math.Max(0.0, camAltM) + BelowLeaveM);
            if (!member) return clearance <= HorizontalEnterM && top >= floor && bottom <= UpEnterM;
            return clearance <= HorizontalLeaveM && top >= floor && bottom <= UpLeaveM;
        }

        /// <summary>
 /// The game query sphere for the column of <see cref="InReach(double, double, double, bool, double)"/>: from
 /// −camAlt (the sea) to +<see cref="UpLeaveM"/>, radius <see cref="HorizontalLeaveM"/>: centre
 /// <paramref name="centreUpM"/> above the camera, radius <paramref name="radiusM"/> (R1).
 /// </summary>
        public static void QuerySphere(double camAltM, out double centreUpM, out double radiusM)
        {
            double alt = double.IsNaN(camAltM) || double.IsInfinity(camAltM) ? 0.0 : Math.Max(0.0, camAltM);
            double low = -(alt + BelowLeaveM), high = UpLeaveM;
            centreUpM = (high + low) / 2.0;
            double half = (high - low) / 2.0;
            radiusM = Math.Sqrt(half * half + HorizontalLeaveM * HorizontalLeaveM) + 1.0;
        }

        public static bool InReach(double horizontalM, double upM, double radiusM, bool member)
        {
            if (double.IsNaN(horizontalM) || double.IsNaN(upM) || double.IsNaN(radiusM) || double.IsInfinity(horizontalM) || double.IsInfinity(upM)) return false;
            double r = radiusM > 0.0 && !double.IsInfinity(radiusM) ? radiusM : 0.0;
            double clearance = Math.Max(0.0, horizontalM - r);
            double top = upM + r, bottom = upM - r;
            if (!member) return clearance <= HorizontalEnterM && top >= 0.0 && bottom <= UpEnterM;
            return clearance <= HorizontalLeaveM && top >= -BelowLeaveM && bottom <= UpLeaveM;
        }

        /// <summary>Why a grid is not an impostor candidate (null = it is): large grids only, at most <see cref="MaxBlocks"/> blocks, no preview.</summary>
        public static string Refusal(bool largeGrid, int blocks, bool preview)
        {
            if (preview) return "preview";
            if (!largeGrid) return "small";
            if (blocks > MaxBlocks) return "tooBig";
            return null;
        }

        /// <summary>
 /// The admissions of one query: candidate indices sorted nearest first (by <paramref name="distances"/>, ties by
 /// id), at most <see cref="MaxAdmitsPerQuery"/> and only while fewer than <see cref="MaxImpostors"/> are mirrored;
 /// <paramref name="capDrops"/> counts the candidates left out. Writes into <paramref name="pick"/> (length ≥
 /// MaxAdmitsPerQuery) and returns the count. Allocation-free.
 /// </summary>
        public static int Admit(long[] ids, double[] distances, int count, int mirrored, int[] pick, out int capDrops)
        {
            capDrops = 0;
            if (count <= 0) return 0;
            int room = Math.Max(0, Math.Min(MaxAdmitsPerQuery, MaxImpostors - Math.Max(0, mirrored)));
            int k = 0;
            for (int n = 0; n < room; n++)
            {
                int best = -1;
                for (int i = 0; i < count; i++)
                {
                    bool taken = false;
                    for (int j = 0; j < k; j++) if (pick[j] == i) { taken = true; break; }
                    if (taken) continue;
                    if (best < 0 || distances[i] < distances[best] || (distances[i] == distances[best] && ids[i] < ids[best])) best = i;
                }
                if (best < 0) break;
                pick[k++] = best;
            }
            capDrops = count - k;
            return k;
        }

        /// <summary>
 /// The impostor filter of one diff (SE producer, in place, O(n)): parts already held back stay held (a replace or a
 /// recolour updates the held copy, a remove drops it unsent); while <paramref name="impostor"/>, an add of a class
 /// <see cref="PartAllowed"/> refuses is held instead of sent. Returns the number of parts newly held.
 /// <typeparamref name="T"/> is the producer's part snapshot; <paramref name="key"/> and <paramref name="flags"/> read it,
 /// <paramref name="recolour"/> copies a recolour's colour into the held copy.
 /// </summary>
        public static int Filter<T>(bool impostor, IDictionary<ulong, T> held, List<T> adds, List<T> replaces, List<T> removes, List<T> recolors,
                                    Func<T, ulong> key, Func<T, uint> flags, Func<T, T, T> recolour)
        {
            if (held == null) return 0;
            int newly = 0;
            // removes: a held part was never sent
            Compact(removes, p => !held.Remove(key(p)));
            // adds: a held key is updated in place; an impostor holds the classes it does not carry
            Compact(adds, p =>
            {
                ulong k = key(p);
                if (held.ContainsKey(k)) { held[k] = p; return false; }
                if (impostor && !PartAllowed(flags(p))) { held[k] = p; newly++; return false; }
                return true;
            });
            // replaces: a held key is updated in place (a part sent before a demotion is replaced as usual: Subnautica holds it)
            Compact(replaces, p =>
            {
                ulong k = key(p);
                if (!held.ContainsKey(k)) return true;
                held[k] = p;
                return false;
            });
            // recolours of a held part: the colour goes into the held copy
            Compact(recolors, p =>
            {
                ulong k = key(p);
                T h;
                if (!held.TryGetValue(k, out h)) return true;
                held[k] = recolour(h, p);
                return false;
            });
            return newly;
        }

        /// <summary>Keeps the elements <paramref name="keep"/> accepts, in order, in O(n).</summary>
        public static void Compact<T>(List<T> list, Func<T, bool> keep)
        {
            if (list == null) return;
            int w = 0;
            for (int i = 0; i < list.Count; i++)
            {
                T v = list[i];
                if (!keep(v)) continue;
                if (w != i) list[w] = v;
                w++;
            }
            if (w < list.Count) list.RemoveRange(w, list.Count - w);
        }
    }
}
