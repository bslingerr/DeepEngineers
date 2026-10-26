using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    // =================================================================================================================
 // S11 "Creatures and hazards", SE side: the game-free rules of the record consumer (compiled into
 // tests/DeepEngineers.SE.Tests). The shared numbers are Wire's S11Conventions/S11Rules.
 // =================================================================================================================

 /// <summary>The S11 seeded failures of the SE side (dev builds; <c>de.mutant S11.M4|S11.M5</c> or DEEPENGINEERS_MUTANTS).</summary>
    internal static class S11SeMutants
    {
        /// <summary>S11.M4: the grab size class not re-checked on SE (with Subnautica's S11.M4 a too-large grid is held: S11.C1 red).</summary>
        public static volatile bool NoSizeClass;
        /// <summary>S11.M5: no timeout release (with Subnautica's S11.M5, no release record: the grid stays held, S11.C3 red).</summary>
        public static volatile bool NoTimeout;
        /// <summary>S11.M11 (pass 6): debris not filtered (a remnant is grabbed and pushed: S11.C4 red; Subnautica has the same name).</summary>
        public static volatile bool NoDebrisFilter;
        /// <summary>S11.M12: the hold by force (the pre-kinematic rule, format hold.force): a fast mouth is not followed (S11.C5 red).</summary>
        public static volatile bool ForceMode;
        /// <summary>S11.M13: the bite-point anchor of 20261024 (the grid point under the mouth), no blend: the grid hangs off the jaw (S11.C6 red).</summary>
        public static volatile bool BiteAnchor;
        /// <summary>S11.M14: no surface rule (SE never lets go at the surface; set it on Subnautica too: S11.C7 red).</summary>
        public static volatile bool NoSurface;

        public static string Active
        {
            get
            {
                var parts = new List<string>(3);
                if (NoSizeClass) parts.Add("S11.M4");
                if (NoTimeout) parts.Add("S11.M5");
                if (NoDebrisFilter) parts.Add("S11.M11");
                if (ForceMode) parts.Add("S11.M12");
                if (BiteAnchor) parts.Add("S11.M13");
                if (NoSurface) parts.Add("S11.M14");
                return string.Join(",", parts.ToArray());
            }
        }

        public static void ClearAll() { NoSizeClass = false; NoTimeout = false; NoDebrisFilter = false; ForceMode = false; BiteAnchor = false; NoSurface = false; }

        /// <summary>Switches an S11 SE mutant; the log text, or null when not an S11 id this side owns.</summary>
        public static string Set(string id, bool on)
        {
            string state = on ? "ON" : "OFF";
            switch ((id ?? "").Trim().ToUpperInvariant())
            {
                case "S11.M4":
                    NoSizeClass = on;
                    return "S11.M4 " + state + " (SE does not re-check the grab size class; set it on Subnautica too)";
                case "S11.M5":
                    NoTimeout = on;
                    return "S11.M5 " + state + " (no timeout release of a held grid; set it on Subnautica too: no release record)";
                case "S11.M12":
                    ForceMode = on;
                    return "S11.M12 " + state + " (a held grid is pulled by the old spring-damper force instead of the kinematic hold)";
                case "S11.M13":
                    BiteAnchor = on;
                    return "S11.M13 " + state + " (a held grid hangs by the point under the mouth, not its centre in the jaw)";
                case "S11.M14":
                    NoSurface = on;
                    return "S11.M14 " + state + " (a held grid is not let go at the surface; set it on Subnautica too)";
                case "S11.M11":
                    NoDebrisFilter = on;
                    return "S11.M11 " + state + " (grid remnants are grabbed and pushed like complete grids; set it on Subnautica too)";
                case "S11.M1": case "S11.M2": case "S11.M3": case "S11.M6": case "S11.M7": case "S11.M8": case "S11.M9": case "S11.M10": case "S11.M15": case "S11.M16":
                    return "REFUSE " + id.Trim() + ": a Subnautica-side S11 mutant (send de.mutant to Subnautica: /de sn mutant ...)";
                default:
                    return null;
            }
        }
    }

    /// <summary>The per-type per-second caps of the hazard and heal tables, and the HUD notice cadence (one per type per 5 s).</summary>
    internal sealed class S11CapBook
    {
        private readonly Dictionary<int, S11RateWindow> _windows = new Dictionary<int, S11RateWindow>();
        private readonly Dictionary<int, ulong> _noticeAt = new Dictionary<int, ulong>();
        public const ulong NoticeEveryMs = 5000;
        public double Capped;

        /// <summary>The SE amount granted now for <paramref name="key"/> (a hazard type, or −1 for heals) under its per-second cap.</summary>
        public double Grant(int key, ulong nowMs, double want, double capPerS)
        {
            S11RateWindow w;
            _windows.TryGetValue(key, out w);
            double g = w.Grant((long)nowMs, want, capPerS);
            _windows[key] = w;
            if (want > g) Capped += want - g;
            return g;
        }

        /// <summary>True when the HUD notice of this key is due (then it waits 5 s).</summary>
        public bool NoticeDue(int key, ulong nowMs)
        {
            ulong at;
            if (_noticeAt.TryGetValue(key, out at) && nowMs < at) return false;
            _noticeAt[key] = nowMs + NoticeEveryMs;
            return true;
        }

        public void Clear() { _windows.Clear(); _noticeAt.Clear(); Capped = 0; }
    }

    /// <summary>
 /// The EMP power losses: per block (key) the time it is switched on again. A second EMP on a block S11 switched off
 /// extends it (the later time); a block the player switched off before is never touched (the caller passes only
 /// enabled producers); <see cref="Due"/> hands back the keys to switch on.
 /// </summary>
    internal sealed class S11EmpBook
    {
        private readonly Dictionary<long, ulong> _restoreAt = new Dictionary<long, ulong>();
        private readonly Dictionary<long, long> _grid = new Dictionary<long, long>();

        public int Count { get { return _restoreAt.Count; } }
        public bool IsOurs(long key) { return _restoreAt.ContainsKey(key); }

        /// <summary>Records that <paramref name="key"/> (on <paramref name="grid"/>) is off until now + seconds. True when it was not ours yet (the caller switches it off).</summary>
        public bool Begin(long key, long grid, ulong nowMs, double seconds)
        {
            ulong until = nowMs + (ulong)Math.Max(0.0, Math.Round(seconds * 1000.0));
            ulong cur;
            if (_restoreAt.TryGetValue(key, out cur))
            {
                if (until > cur) _restoreAt[key] = until;
                return false;
            }
            _restoreAt[key] = until;
            _grid[key] = grid;
            return true;
        }

        /// <summary>The keys due now (removed from the book).</summary>
        public List<long> Due(ulong nowMs, List<long> into)
        {
            into.Clear();
            foreach (KeyValuePair<long, ulong> kv in _restoreAt) if (nowMs >= kv.Value) into.Add(kv.Key);
            foreach (long k in into) { _restoreAt.Remove(k); _grid.Remove(k); }
            return into;
        }

        /// <summary>Every key (world unload: all of them are switched on again).</summary>
        public List<long> All(List<long> into)
        {
            into.Clear();
            into.AddRange(_restoreAt.Keys);
            _restoreAt.Clear();
            _grid.Clear();
            return into;
        }

        public long GridOf(long key) { long g; return _grid.TryGetValue(key, out g) ? g : 0; }

        /// <summary>Review 1: the Enabled a save writes: on when S11's EMP switched the block off, else the block's own.</summary>
        public static bool SavedEnabled(bool switchedOffByEmp, bool builderEnabled) { return switchedOffByEmp || builderEnabled; }
    }

    /// <summary>One held grid on SE: the creature's key, the grid, the times, the target pose and the relative orientation.</summary>
    internal sealed class S11Held
    {
        public uint Key;
        public long Grid;
        public int Creature;
        public ulong StartMs, LastHoldMs, PrevMs;
        /// <summary>Subnautica's emit time of the last attach record (DamageRecord.snTimeSec; 0 = none).</summary>
        public double PrevSnTime;
        public double PlannedS;
        public Vec3 Target, PrevTarget, TargetVel;
        public Quat AttachQ = Quat.Identity, RelQ = Quat.Identity;
        /// <summary>S11b 20261023-220012: the bite anchor, the grid-local point under the attach point at the start (format hold.force).</summary>
        public Vec3 AnchorLocal;
        public bool HasAnchor;
        /// <summary>format hold.kinematic: the largest anchor-to-mouth error measured on any tick of the hold, the fastest mouth, the snaps.</summary>
        public double MaxErrorM, MaxTargetSpeed;
        public long Snaps;
        public bool HasRel, HasPrev;
        public long Holds, Forces;
        public double LastErrorM, LastErrorDeg = double.NaN;
        /// <summary> (format hold.kinematic, the jaw centre): the anchor's world position at the start and the blend (ms).</summary>
        public Vec3 StartAnchor;
        public double BlendMs;
        /// <summary>The grid's centre of bounds to the jaw centre (the attach point now): last, and the largest after the blend.</summary>
        public double LastJawM = double.NaN, MaxJawM;
        /// <summary>format surface.release: the shallowest top of the grid's bounds seen (Water Mod depth, m; NaN = no reading).</summary>
        public double MinMouthDepthM = double.NaN;
        /// <summary>Review 8: the orientation relative to the attach frame that aligns the grid's longest axis with the attach forward.</summary>
        public Quat RelAlignedQ = Quat.Identity;
        public bool HasAligned;
    }

    /// <summary>The grab registry (C): one creature per grid, one grid per creature; start, hold, release, timeout.</summary>
    internal sealed class S11GrabBook
    {
        private readonly Dictionary<uint, S11Held> _byKey = new Dictionary<uint, S11Held>();
        private readonly List<uint> _scratch = new List<uint>();

        public int Count { get { return _byKey.Count; } }
        public IEnumerable<S11Held> All { get { return _byKey.Values; } }

        public bool TryGet(uint key, out S11Held h) { return _byKey.TryGetValue(key, out h); }

        public bool GridHeld(long grid, uint exceptKey)
        {
            foreach (S11Held h in _byKey.Values) if (h.Grid == grid && h.Key != exceptKey) return true;
            return false;
        }

        /// <summary>A start: AlreadyHeld when another creature holds the grid; a repeated start of the same key restarts it.</summary>
        public S11Reject Start(uint key, long grid, int creature, ulong nowMs, double plannedS, out S11Held h)
        {
            h = null;
            if (GridHeld(grid, key)) return S11Reject.AlreadyHeld;
            h = new S11Held { Key = key, Grid = grid, Creature = creature, StartMs = nowMs, LastHoldMs = nowMs, PlannedS = plannedS };
            _byKey[key] = h;
            return S11Reject.None;
        }

        /// <summary>A hold or release record for a key SE does not hold is <c>NoGrab</c>.</summary>
        public S11Reject Touch(uint key, long grid, ulong nowMs, out S11Held h)
        {
            if (!_byKey.TryGetValue(key, out h) || h.Grid != grid) { h = null; return S11Reject.NoGrab; }
            h.LastHoldMs = nowMs;
            return S11Reject.None;
        }

        public bool Remove(uint key) { return _byKey.Remove(key); }

        /// <summary>The keys whose last hold record is older than Hold.TimeoutMs (none under S11.M5).</summary>
        public List<uint> TimedOut(ulong nowMs, bool timeoutOff)
        {
            _scratch.Clear();
            if (timeoutOff) return _scratch;
            foreach (S11Held h in _byKey.Values) if (S11GrabRules.TimedOut((long)h.LastHoldMs, (long)nowMs)) _scratch.Add(h.Key);
            return _scratch;
        }

        public List<uint> Keys()
        {
            _scratch.Clear();
            _scratch.AddRange(_byKey.Keys);
            return _scratch;
        }

        public void Clear() { _byKey.Clear(); }
    }

    /// <summary>The s11.* row texts of the SE side (CultureInfo.InvariantCulture).</summary>
    internal static class S11SeText
    {
        public static string G(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        public static string HazardNotice(S11HazardRow row, double se)
        {
            return "Subnautica " + row.Effect + ": -" + se.ToString("0.#", CultureInfo.InvariantCulture) + " HP";
        }
    }
}
