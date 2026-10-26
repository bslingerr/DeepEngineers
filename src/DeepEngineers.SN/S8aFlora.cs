using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DeepEngineers.Wire;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>

 /// are <c>LargeWorldEntity</c> objects of its entity cells, placed once on the seabed and never re-anchored when the terrain changes
 /// A carve that turns an object's ground into water hides it:
 /// renderers and colliders off, transient (nothing reaches a save; a reloaded cell brings it back and is tested again).
 /// v2.1:
 /// <list type="bullet">
 /// <item>Ground is read from Subnautica's own voxels (<see cref="S8aVoxelGround"/>: the served batch file now, the install's
 /// original before any carve), so objects that wake beyond collider range are judged too (colliders: physics only as the fallback
 /// when no file answers).</item>
 /// <item>When a carve's notice arrives every candidate in the box with ground within <see cref="GroundM"/> is remembered; after the
 /// carve (+<see cref="SettleMs"/>) a remembered one without ground is hidden; one that never had ground there (on a wreck, a rock)
 /// stays.</item>
 /// <item>Objects that wake later inside a carved box (<c>LargeWorldEntity.Start</c> postfix) are hidden only when their ground was
 /// carved: solid within <see cref="LateGroundM"/> under them in the install's original and none now (so a fragment on a wreck over
 /// an old hole stays). Where no original exists (the window: the planet's batches) the late rule does not apply.</item>
 /// <item>Carved boxes are kept per store key and chart, never merged into large boxes, and persisted in the store folder
 /// (<see cref="CarvesFile"/>), so a restart judges the props over old holes again.</item>
 /// <item>Candidates per carve box come from this mod's registry of started entities (every <c>LargeWorldEntity.Start</c>, bucketed by
 /// 16 m; no scene-wide search on the main thread) and from <c>Physics.OverlapBox</c>. a test run (nd 20261024-031621, S8a.ND.props 0/5):
 /// OverlapBox alone never saw a kinematic Near-level entity more than 16 m from the camera, because Subnautica turns its collisions
 /// off there
 /// so the five test props and every far pickup or outcrop were never tested; only static
 /// corals were. A free (non-kinematic) body over a carve is woken (<c>Rigidbody.WakeUp</c>) and falls by itself. This mod's deposits
 /// (S8aOreDeposits) are never candidates. Candidates: flora, corals, mushrooms, rocks, props and debris, outcrops
 /// (<c>BreakableResource</c>), pickupable items lying in the world (Pink Caps: TechType.PinkMushroom; scrap metal), deposits,
 /// fragments; cell levels Near, Medium, Far; never creatures, vehicles, the player, bases, constructables, free physics bodies.
 /// S8a.M26: v1's rule (no pickupable item, Near and Medium only).</item>
 /// <item>An object wider than <see cref="LargeExtentM"/> is hidden only when its root and 3 of 4 footprint points lost their ground.</item>
 /// </list>
 /// Row <c>s8a.flora</c> per test with per-family counts (tested / hidden / kept). Main thread.
 /// </summary>
    internal static class S8aFlora
    {
        public const float GroundM = 0.8f, LateGroundM = 1.2f, AboveM = 0.5f, MarginM = 1.0f, LargeExtentM = 3.0f;
        public const ulong SettleMs = 400, LateSettleMs = 800;
        public const int MaxPerFrame = 2, MaxLatePerFrame = 24, MaxCarves = 2048;
        public const string CarvesFile = "flora-carves.tsv";

        private sealed class Pending
        {
            public Bounds Box;
            public ulong DueMs;
            public uint Seq;
            public bool Applied;
            public HashSet<int> Grounded;
        }

        private struct Carve { public ulong Store, Chart; public Bounds Box; }

        private static readonly List<Pending> Queue = new List<Pending>();
        private static readonly List<Carve> Carves = new List<Carve>();
        private struct LateItem { public LargeWorldEntity E; public ulong DueMs; }
        private static readonly Queue<LateItem> Late = new Queue<LateItem>();
        private static readonly HashSet<int> HiddenIds = new HashSet<int>();
        // a test run: every started entity by 16 m bucket of its position (re-bucketed when found elsewhere; dead ones pruned as the
 // buckets are read, and a few buckets per frame round-robin)
        public const float BucketM = 16f;
        private static readonly Dictionary<long, List<LargeWorldEntity>> Registry = new Dictionary<long, List<LargeWorldEntity>>();
        private static readonly List<long> RegistryKeys = new List<long>();
        private static int _sweep;
        public static long Registered, RegistryFound, Woken;
        // a test run (nd 20261024-045342, S8a.ND.props "cand 0 reg 0"): the registry is fed by three paths (LargeWorldEntity.Start, Subnautica's
 // CellManager.RegisterEntity(LargeWorldEntity), this mod's own spawns: "start", "cell", "dev"), each entity once (Known), the first
 // path kept for the diagnostics (capped)
        private static readonly HashSet<int> Known = new HashSet<int>();
        private static readonly Dictionary<int, string> PathOf = new Dictionary<int, string>();
        public static long RegisteredStart, RegisteredCell, RegisteredDev;
        public static int RegistryCount { get { int n = 0; foreach (List<LargeWorldEntity> l in Registry.Values) n += l.Count; return n; } }

        private static long BucketOf(Vector3 p)
        {
            long bx = (long)Mathf.Floor(p.x / BucketM), by = (long)Mathf.Floor(p.y / BucketM), bz = (long)Mathf.Floor(p.z / BucketM);
            return ((bx & 0x1FFFFF) << 42) | ((by & 0x1FFFFF) << 21) | (bz & 0x1FFFFF);
        }

        /// <summary>An entity into the registry by one of its paths ("start", "cell", "dev"); once per entity.</summary>
        public static void Register(LargeWorldEntity e, string via)
        {
            if (e == null) return;
            int id = e.GetInstanceID();
            if (!Known.Add(id)) return;
            if (via == "cell") RegisteredCell++; else if (via == "dev") RegisteredDev++; else RegisteredStart++;
            if (PathOf.Count > 200000) PathOf.Clear();
            PathOf[id] = via;
            Register(e);
        }

        private static void Register(LargeWorldEntity e)
        {
            long k = BucketOf(e.transform.position);
            List<LargeWorldEntity> l;
            if (!Registry.TryGetValue(k, out l)) { l = new List<LargeWorldEntity>(); Registry[k] = l; RegistryKeys.Add(k); }
            l.Add(e);
            Registered++;
        }

        /// <summary>The registered entities whose position lies in the box (dead ones dropped, moved ones re-bucketed).</summary>
        private static void FromRegistry(Bounds b, List<LargeWorldEntity> into)
        {
            long x0 = (long)Mathf.Floor(b.min.x / BucketM), x1 = (long)Mathf.Floor(b.max.x / BucketM);
            long y0 = (long)Mathf.Floor(b.min.y / BucketM), y1 = (long)Mathf.Floor(b.max.y / BucketM);
            long z0 = (long)Mathf.Floor(b.min.z / BucketM), z1 = (long)Mathf.Floor(b.max.z / BucketM);
            List<LargeWorldEntity> moved = null;
            for (long bx = x0; bx <= x1; bx++)
                for (long by = y0; by <= y1; by++)
                    for (long bz = z0; bz <= z1; bz++)
                    {
                        long k = ((bx & 0x1FFFFF) << 42) | ((by & 0x1FFFFF) << 21) | (bz & 0x1FFFFF);
                        List<LargeWorldEntity> l;
                        if (!Registry.TryGetValue(k, out l)) continue;
                        for (int i = l.Count - 1; i >= 0; i--)
                        {
                            LargeWorldEntity e = l[i];
                            if (e == null) { Known.Remove(e.GetInstanceID()); l.RemoveAt(i); continue; }
                            Vector3 p = e.transform.position;
                            if (BucketOf(p) != k) { l.RemoveAt(i); (moved ?? (moved = new List<LargeWorldEntity>())).Add(e); }
                            if (b.Contains(p)) into.Add(e);
                        }
                    }
            if (moved != null) foreach (LargeWorldEntity e in moved) { Register(e); Registered--; }
        }

        /// <summary>A few registry buckets per frame: dead entries out.</summary>
        private static void SweepRegistry(int buckets)
        {
            for (int n = 0; n < buckets && RegistryKeys.Count > 0; n++)
            {
                if (_sweep >= RegistryKeys.Count) _sweep = 0;
                long k = RegistryKeys[_sweep];
                List<LargeWorldEntity> l = Registry[k];
                l.RemoveAll(e => { if (e != null) return false; Known.Remove(e.GetInstanceID()); return true; });
                if (l.Count == 0) { Registry.Remove(k); RegistryKeys[_sweep] = RegistryKeys[RegistryKeys.Count - 1]; RegistryKeys.RemoveAt(RegistryKeys.Count - 1); }
                else _sweep++;
            }
        }
        private static ulong _store, _chart;
        public static long Tests, Candidates, Hidden, Errors, LateTested, LateHidden, NotLoaded, CarvesLoaded;
        public static readonly Dictionary<string, long[]> Families = new Dictionary<string, long[]>(StringComparer.Ordinal);

        public static string FamiliesText()
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, long[]> kv in Families)
                sb.Append(sb.Length > 0 ? "," : "").Append(kv.Key).Append(':').Append(kv.Value[0]).Append('/').Append(kv.Value[1]).Append('/').Append(kv.Value[2]);
            return sb.ToString();
        }

        /// <summary>The store and chart the carves belong to (S8aTerrain on a store or chart change): the store's persisted carves load.</summary>
        public static void SetContext(ulong storeKey, ulong chartKey)
        {
            if (storeKey == _store && chartKey == _chart) return;
            bool storeChanged = storeKey != _store;
            _store = storeKey;
            _chart = chartKey;
            if (!storeChanged || storeKey == 0) return;
            lock (Carves) Carves.Clear();
            try
            {
                string path = Path.Combine(S8aStore.StoreFolder(S8aSnPaths.Root(), storeKey), CarvesFile);
                if (!File.Exists(path)) return;
                foreach (string line in File.ReadAllLines(path))
                {
                    string[] f = line.Split('\t');
                    if (f.Length < 7) continue;
                    ulong ck;
                    if (!ulong.TryParse(f[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ck)) continue;
                    var v = new float[6];
                    bool ok = true;
                    for (int k = 0; k < 6 && ok; k++) ok = float.TryParse(f[1 + k], NumberStyles.Float, CultureInfo.InvariantCulture, out v[k]);
                    if (!ok) continue;
                    var b = new Bounds();
                    b.SetMinMax(new Vector3(v[0], v[1], v[2]), new Vector3(v[3], v[4], v[5]));
                    lock (Carves) AddCarve(new Carve { Store = storeKey, Chart = ck, Box = b });
                    CarvesLoaded++;
                }
            }
            catch (Exception e) { Errors++; if (Errors <= 3) DeLog.Warn("DE.S8a flora carves not read: " + e.GetType().Name + ": " + e.Message); }
        }

        private static void AddCarve(Carve c)
        {
            foreach (Carve o in Carves)
                if (o.Store == c.Store && o.Chart == c.Chart && o.Box.Contains(c.Box.min) && o.Box.Contains(c.Box.max)) return;
            if (Carves.Count >= MaxCarves) Carves.RemoveAt(0);
            Carves.Add(c);
        }

        private static Bounds BoxOf(int[] mins, int[] maxs)
        {
            var min = new Vector3(mins[0] - 2048f - MarginM, mins[1] - 3040f - MarginM, mins[2] - 2048f - MarginM);
            var max = new Vector3(maxs[0] + 1 - 2048f + MarginM, maxs[1] + 1 - 3040f + GroundM + MarginM, maxs[2] + 1 - 2048f + MarginM);
            return new Bounds((min + max) * 0.5f, max - min);
        }

        /// <summary>
 /// The candidates rooted in a box: the registry's entities there, then OverlapBox's colliders up to their LargeWorldEntity
 /// (an entity that started before this plugin). A free body there is woken (it falls by itself), never a candidate.
 /// </summary>
        private static List<LargeWorldEntity> InBox(Bounds b)
        {
            var seen = new HashSet<int>();
            var out_ = new List<LargeWorldEntity>();
            var all = new List<LargeWorldEntity>();
            FromRegistry(b, all);
            int fromRegistry = all.Count;
            Collider[] cols = Physics.OverlapBox(b.center, b.extents + new Vector3(2f, 2f, 2f), Quaternion.identity, ~0, QueryTriggerInteraction.Collide);
            foreach (Collider c in cols)
            {
                if (c == null) continue;
                LargeWorldEntity e = c.GetComponentInParent<LargeWorldEntity>();
                if (e != null) all.Add(e);
            }
            for (int i = 0; i < all.Count; i++)
            {
                LargeWorldEntity e = all[i];
                if (e == null || !seen.Add(e.GetInstanceID())) continue;
                if (!b.Contains(e.transform.position)) continue;
                if (!Candidate(e))
                {
                    WakeIfFree(e);
                    continue;
                }
                if (i < fromRegistry) RegistryFound++;
                out_.Add(e);
            }
            return out_;
        }

        /// <summary>A free body of a candidate kind (not a creature, vehicle, player, base or constructable): woken, so it falls into the hole.</summary>
        private static void WakeIfFree(LargeWorldEntity e)
        {
            try
            {
                if (e == null || !e.isActiveAndEnabled) return;
                Rigidbody rb = e.GetComponent<Rigidbody>();
                if (rb == null || rb.isKinematic || S8aOreDeposits.IsOurs(e.gameObject)) return;
                GameObject go = e.gameObject;
                if (go.GetComponentInChildren<Creature>() != null || go.GetComponentInChildren<Vehicle>() != null || go.GetComponentInChildren<Player>() != null ||
                    go.GetComponentInParent<Base>() != null || go.GetComponentInChildren<Constructable>() != null) return;
                if (rb.IsSleeping()) { rb.WakeUp(); Woken++; }
            }
            catch (Exception) { }
        }

        /// <summary>
 /// a test run (S8a.NL.look: the far frames had no plant and no creature, yet "entities" counted this mod's water-column spawns and
 /// deposits): the started entities within <paramref name="radius"/> of <paramref name="cam"/> by kind (creature, flora: plant,
 /// coral, mushroom, pickflora; other), this mod's deposits and spawns apart.
 /// </summary>
        public static void Life(Vector3 cam, float radius, out int creatures, out int flora, out int other, out int ours)
        {
            creatures = flora = other = ours = 0;
            var l = new List<LargeWorldEntity>();
            FromRegistry(new Bounds(cam, new Vector3(2f * radius, 2f * radius, 2f * radius)), l);
            var seen = new HashSet<int>();
            float r2 = radius * radius;
            foreach (LargeWorldEntity e in l)
            {
                if (e == null || !seen.Add(e.GetInstanceID())) continue;
                GameObject go = e.gameObject;
                if (!go.activeInHierarchy || (go.transform.position - cam).sqrMagnitude > r2) continue;
                if (S8aOreDeposits.IsOurs(go) || go.name.StartsWith("DE.S8a", StringComparison.Ordinal)) { ours++; continue; }
                if (HiddenIds.Contains(e.GetInstanceID())) continue;
                if (go.GetComponentInChildren<Creature>() != null) { creatures++; continue; }
                string f = Family(go);
                if (f == "plant" || f == "coral" || f == "mushroom" || f == "pickflora") flora++;
                else other++;
            }
        }

        /// <summary>The test props' diagnostics (S8a.ND.props): candidate by the rule, in the registry's box, collisions on.</summary>
        public static string Diagnose(GameObject go)
        {
            if (go == null) return "";
            LargeWorldEntity e = go.GetComponent<LargeWorldEntity>();
            if (e == null) return "noLwe";
            var l = new List<LargeWorldEntity>();
            Vector3 p = e.transform.position;
            FromRegistry(new Bounds(p, Vector3.one), l);
            bool reg = l.Exists(x => x == e);
            string via;
            if (!PathOf.TryGetValue(e.GetInstanceID(), out via)) via = "-";
            Rigidbody rb = e.GetComponent<Rigidbody>();
            int g = S8aVoxelGround.GroundWithin(p, AboveM, GroundM, false), o = S8aVoxelGround.GroundWithin(p, AboveM, LateGroundM, true);
            string why = CandidateWhy(e);
            return "cand " + (why == null ? "1" : "0 (" + why + ")") + " reg " + (reg ? "1" : "0") + " via " + via + " act " + (go.activeInHierarchy ? 1 : 0) +
                   " en " + (e.enabled ? 1 : 0) + " dc " + (rb == null ? "-" : rb.detectCollisions ? "1" : "0") +
                   " kin " + (rb == null ? "-" : rb.isKinematic ? "1" : "0") + " level " + e.cellLevel + " ground " + g + " orig " + o;
        }

        /// <summary>A carve's notice (the old terrain): the candidates in its box that have ground now.</summary>
        public static void OnCarveNotice(int[] mins, int[] maxs, uint seq, ulong nowMs)
        {
            if (mins == null || maxs == null) return;
            Bounds b = BoxOf(mins, maxs);
            var grounded = new HashSet<int>();
            try
            {
                foreach (LargeWorldEntity e in InBox(b))
                    if (Grounded(e, GroundM, false) == 1) grounded.Add(e.GetInstanceID());
            }
            catch (Exception ex) { Errors++; if (Errors <= 3) DeLog.Warn("DE.S8a flora snapshot " + ex.GetType().Name + ": " + ex.Message); }
            Merge(b, seq, nowMs, grounded, false);
        }

        /// <summary>A carve applied: the changed box in world blocks (SN = block - (2048, 3040, 2048)); kept and persisted.</summary>
        public static void OnCarveApplied(int[] mins, int[] maxs, uint seq, ulong nowMs)
        {
            if (mins == null || maxs == null) return;
            Bounds b = BoxOf(mins, maxs);
            Merge(b, seq, nowMs, null, true);
            lock (Carves) AddCarve(new Carve { Store = _store, Chart = _chart, Box = b });
            if (_store == 0) return;
            try
            {
                string path = Path.Combine(S8aStore.StoreFolder(S8aSnPaths.Root(), _store), CarvesFile);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, _chart.ToString("x16", CultureInfo.InvariantCulture) + "\t" + F(b.min.x) + "\t" + F(b.min.y) + "\t" + F(b.min.z) + "\t" +
                                         F(b.max.x) + "\t" + F(b.max.y) + "\t" + F(b.max.z) + "\n");
            }
            catch (Exception e) { Errors++; if (Errors <= 3) DeLog.Warn("DE.S8a flora carves not written: " + e.GetType().Name + ": " + e.Message); }
        }

        private static string F(float v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }

        private static void Merge(Bounds b, uint seq, ulong nowMs, HashSet<int> grounded, bool applied)
        {
            lock (Queue)
            {
                foreach (Pending p in Queue)
                {
                    if (!p.Box.Intersects(b)) continue;
                    p.Box.Encapsulate(b);
                    if (grounded != null) { if (p.Grounded == null) p.Grounded = grounded; else p.Grounded.UnionWith(grounded); }
                    if (applied) { p.Applied = true; p.DueMs = nowMs + SettleMs; }
                    p.Seq = seq;
                    return;
                }
                Queue.Add(new Pending { Box = b, Seq = seq, Applied = applied, DueMs = nowMs + SettleMs, Grounded = grounded });
            }
        }

        /// <summary>The <c>LargeWorldEntity.Start</c> postfix: an object that wakes inside a carved box of this store and chart is tested later.</summary>
        public static void OnEntityStart(LargeWorldEntity e)
        {
            if (e == null) return;
            try { Register(e, "start"); } catch (Exception) { }
            lock (Carves)
            {
                if (Carves.Count == 0) return;
                Vector3 p = e.transform.position;
                bool inside = false;
                for (int i = 0; i < Carves.Count && !inside; i++)
                    inside = Carves[i].Store == _store && Carves[i].Chart == _chart && Carves[i].Box.Contains(p);
                if (!inside) return;
            }
            lock (Late) Late.Enqueue(new LateItem { E = e, DueMs = (ulong)Wire.Native.Win32.TickCount64() + LateSettleMs });
        }

        public static void Update(ulong nowMs)
        {
            try { SweepRegistry(4); } catch (Exception) { }
            int done = 0;
            lock (Queue)
            {
                for (int i = Queue.Count - 1; i >= 0 && done < MaxPerFrame; i--)
                {
                    Pending p = Queue[i];
                    if (!p.Applied || nowMs < p.DueMs) continue;
                    Queue.RemoveAt(i);
                    done++;
                    try { Test(p, nowMs); }
                    catch (Exception e) { Errors++; if (Errors <= 3) DeLog.Warn("DE.S8a flora test " + e.GetType().Name + ": " + e.Message); }
                }
            }
            int late = 0;
            var fam = new Dictionary<string, long[]>(StringComparer.Ordinal);
            lock (Late)
            {
                while (Late.Count > 0 && late < MaxLatePerFrame && Late.Peek().DueMs <= nowMs)
                {
                    LargeWorldEntity e = Late.Dequeue().E;
                    late++;
                    try
                    {
                        if (e == null || !e.isActiveAndEnabled || !Candidate(e) || HiddenIds.Contains(e.GetInstanceID())) continue;
                        // carved: ground in the install's original, none now (an object on a wreck or a rock never had it)
                        int before = Grounded(e, LateGroundM, true), now = Grounded(e, GroundM, false);
                        if (before < 0 || now < 0) { NotLoaded++; continue; }
                        LateTested++;
                        bool hide = before == 1 && now == 0;
                        Count(fam, Family(e.gameObject), hide);
                        if (hide) { HideObject(e.gameObject); LateHidden++; }
                    }
                    catch (Exception ex) { Errors++; if (Errors <= 3) DeLog.Warn("DE.S8a flora late " + ex.GetType().Name + ": " + ex.Message); }
                }
            }
            if (fam.Count > 0) Row(nowMs, 0, "late", fam, null);
        }

        private static void Test(Pending p, ulong nowMs)
        {
            Tests++;
            var fam = new Dictionary<string, long[]>(StringComparer.Ordinal);
            foreach (LargeWorldEntity e in InBox(p.Box))
            {
                int id = e.GetInstanceID();
                if (HiddenIds.Contains(id)) continue;
                string f = Family(e.gameObject);
                bool hide;
                if (p.Grounded != null) hide = p.Grounded.Contains(id) && Grounded(e, GroundM, false) == 0;     // its ground was carved
                else
                {
                    int before = Grounded(e, LateGroundM, true), now = Grounded(e, GroundM, false);
                    hide = before == 1 && now == 0;
                }
                Count(fam, f, hide);
                if (hide) HideObject(e.gameObject);
            }
            Row(nowMs, p.Seq, p.Grounded != null ? "notice" : "reload", fam, p);
        }

        private static void Count(Dictionary<string, long[]> fam, string f, bool hide)
        {
            long[] c;
            if (!fam.TryGetValue(f, out c)) fam[f] = c = new long[3];
            c[0]++;
            if (hide) c[1]++; else c[2]++;
            long[] t;
            if (!Families.TryGetValue(f, out t)) Families[f] = t = new long[3];
            t[0]++;
            if (hide) t[1]++; else t[2]++;
            Candidates++;
            if (hide) Hidden++;
        }

        private static void Row(ulong nowMs, uint seq, string why, Dictionary<string, long[]> fam, Pending p)
        {
            long cand = 0, hid = 0, kept = 0;
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, long[]> kv in fam)
            {
                cand += kv.Value[0]; hid += kv.Value[1]; kept += kv.Value[2];
                sb.Append(sb.Length > 0 ? "," : "").Append(kv.Key).Append(':').Append(kv.Value[0]).Append('/').Append(kv.Value[1]).Append('/').Append(kv.Value[2]);
            }
            var j = new JsonOut().Str("t", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Num("tickMs", nowMs)
                .Num("frame", DeLog.Frame).Str("level", "info").Str("event", "s8a.flora").Str("ev", "s8a.flora")
                .Obj("data").Num("snFrame", DeLog.Frame).Num("seq", (long)seq).Str("why", why).Num("candidates", cand).Num("hidden", hid).Num("kept", kept)
                .Str("families", sb.ToString()).Num("voxelReads", S8aVoxelGround.Reads).Num("voxelUnknown", S8aVoxelGround.Unknown);
            if (p != null)
                j.Num("minX", (double)p.Box.min.x).Num("minY", (double)p.Box.min.y).Num("minZ", (double)p.Box.min.z)
                 .Num("maxX", (double)p.Box.max.x).Num("maxY", (double)p.Box.max.y).Num("maxZ", (double)p.Box.max.z);
            S8aTerrain.Row(j.EndObj().ToString());
        }

        /// <summary>An object the rule may hide (see the class remarks).</summary>
        private static bool Candidate(LargeWorldEntity e) { return CandidateWhy(e) == null; }

        /// <summary>
 /// Why an object is not a candidate (null: it is). a test run: the object must be active in the scene, not this LargeWorldEntity
 /// component enabled (a component Unity never started, or one disabled, is still the object's: its renderers show).
 /// </summary>
        private static string CandidateWhy(LargeWorldEntity e)
        {
            if (e == null) return "gone";
            GameObject go = e.gameObject;
            if (!go.activeInHierarchy) return "inactive";
            bool v1 = S8aMutants.M26;
            if (v1 && !e.enabled) return "disabled (v1)";
            if (e.cellLevel != LargeWorldEntity.CellLevel.Near && e.cellLevel != LargeWorldEntity.CellLevel.Medium && (v1 || e.cellLevel != LargeWorldEntity.CellLevel.Far)) return "level " + e.cellLevel;
            if (S8aOreDeposits.IsOurs(go)) return "ours";
            if (go.GetComponentInChildren<Creature>() != null) return "creature";
            if (go.GetComponentInChildren<Vehicle>() != null) return "vehicle";
            if (go.GetComponentInChildren<Player>() != null) return "player";
            if (go.GetComponentInParent<Base>() != null) return "base";
            if (go.GetComponentInChildren<Constructable>() != null) return "constructable";
            if (v1 && go.GetComponentInChildren<Pickupable>() != null) return "pickup (v1)";          // S8a.M26: v1's rule
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic) return "free body";
            return null;
        }

        /// <summary>The object's family for the counts: by its component, else by its name.</summary>
        public static string Family(GameObject go)
        {
            if (go.GetComponent<Drillable>() != null) return "deposit";
            if (go.GetComponent<BreakableResource>() != null) return "outcrop";
            string n = go.name.ToLowerInvariant();
            if (n.Contains("fragment")) return "fragment";
            if (go.GetComponent<Pickupable>() != null) return n.Contains("mushroom") || n.Contains("plant") || n.Contains("seed") ? "pickflora" : "pickup";
            if (n.Contains("mushroom")) return "mushroom";
            if (n.Contains("coral")) return "coral";
            if (n.Contains("kelp") || n.Contains("plant") || n.Contains("grass") || n.Contains("fern") || n.Contains("bulb") || n.Contains("flora")) return "plant";
            if (n.Contains("rock") || n.Contains("boulder") || n.Contains("stone")) return "rock";
            if (n.Contains("wreck") || n.Contains("debris") || n.Contains("metal") || n.Contains("scrap") || n.Contains("starship")) return "debris";
            return "other";
        }

        /// <summary>1 ground within depth under the object (a large one: its root, or 2 of 4 footprint points), 0 none, -1 unknown.</summary>
        private static int Grounded(LargeWorldEntity e, float depth, bool original)
        {
            Vector3 root = e.transform.position;
            int g = Ground(root, depth, original);
            if (g != 0) return g;
            Bounds b;
            if (!RendererBounds(e.gameObject, out b)) return 0;
            float ext = Mathf.Max(b.extents.x, b.extents.z);
            if (ext * 2f <= LargeExtentM) return 0;
            int gone = 0;
            foreach (Vector2 d in new[] { new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1) })
            {
                var q = new Vector3(b.center.x + d.x * 0.7f * b.extents.x, root.y, b.center.z + d.y * 0.7f * b.extents.z);
                if (Ground(q, depth, original) == 0) gone++;
            }
            return gone < 3 ? 1 : 0;
        }

        private static int Ground(Vector3 p, float depth, bool original)
        {
            int g = S8aVoxelGround.GroundWithin(p, AboveM, depth, original);
            if (g >= 0 || original) return g;
            // no file answers (should not happen for a loaded cell): the terrain colliders, when near enough to exist
            int mask = Voxeland.GetTerrainLayerMask();
            if (Physics.Raycast(p + Vector3.up * AboveM, Vector3.down, AboveM + depth, mask, QueryTriggerInteraction.Ignore)) return 1;
            return Physics.CheckSphere(p, 6f, mask, QueryTriggerInteraction.Ignore) ? 0 : -1;
        }

        private static bool RendererBounds(GameObject go, out Bounds b)
        {
            b = default(Bounds);
            bool any = false;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>())
            {
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return any;
        }

        private static void HideObject(GameObject go)
        {
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>()) r.enabled = false;
            foreach (Collider c in go.GetComponentsInChildren<Collider>()) c.enabled = false;
            LargeWorldEntity lwe = go.GetComponent<LargeWorldEntity>();
            HiddenIds.Add(lwe != null ? lwe.GetInstanceID() : go.GetInstanceID());
        }

        /// <summary>Whether an object is hidden by this rule (the test props' status).</summary>
        public static bool IsHidden(GameObject go)
        {
            if (go == null) return false;
            LargeWorldEntity lwe = go.GetComponent<LargeWorldEntity>();
            return HiddenIds.Contains(lwe != null ? lwe.GetInstanceID() : go.GetInstanceID());
        }
    }
}
