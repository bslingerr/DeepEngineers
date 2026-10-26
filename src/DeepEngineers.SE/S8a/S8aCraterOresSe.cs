using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DeepEngineers.SE.Terrain;
using DeepEngineers.Wire;
using Sandbox.Definitions;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using VRage.Game;
using VRage.Voxels;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S8a Stage E, SE side of the Subnautica map's ores (<see cref="S8aCraterOres"/>): the deposit file watcher (Subnautica
 /// reports its large resource deposits into the world's store folder; every <see cref="PollMs"/> the file is re-read when
 /// its length or time changed and the tiles' rule takes the new pockets: <c>S8aCraterOreRule.SetDeposits</c>, read by the
 /// provider's material function on the readers' threads) and the voxel reads of <c>de.s8a ores</c> (S8a.N10.crater).
 /// The pockets reach voxels SE reads after the update (the provider is asked whenever SE reads an unmodified range).
 /// </summary>
    internal static class S8aCraterOresSe
    {
        public const ulong PollMs = 5000;
        private static ulong _nextMs;
        private static long _len = -1;
        private static DateTime _time;
        private static S8aCraterOreRule _ruleSeen;
        private static string _path;
        public static int Loads, Errors;

        public static void Tick(ulong nowMs)
        {
            if (nowMs < _nextMs) return;
            _nextMs = nowMs + PollMs;
            S8aCraterOreRule rule = TerrainTiles.CraterOres;
            if (rule == null || S8aHost.StoreKey == 0) return;
            S8aHost.ApplyPlanetRules();       // the tiles' rule may come after the world's rules (planet v2 R8)
            string path = S8aCraterOres.DepositsPath(S8aStore.DefaultRoot(), S8aHost.StoreKey);
            try
            {
                var fi = new FileInfo(path);
                long len = fi.Exists ? fi.Length : -1;
                DateTime time = fi.Exists ? fi.LastWriteTimeUtc : default(DateTime);
                if (ReferenceEquals(rule, _ruleSeen) && len == _len && time == _time && path == _path) return;
                string text = "";
                if (fi.Exists)
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                }
                List<S8aCraterDeposit> list = S8aCraterOres.Parse(text);
                rule.SetDeposits(list);
                _ruleSeen = rule;
                _len = len;
                _time = time;
                _path = path;
                Loads++;
                Jsonl.WriteLine(S4Rows.Row("s8a.deposits", "tick", (long)GameState.GameplayTick, "reported", list.Count, "pockets", rule.DepositCount, "path", path,
                                           "cubeRule", rule.CubeRule, "depositsPerKm2", rule.DepositsPerKm2, "capped", rule.Capped));
                DeLog.Info("DE.S8a crater deposits: " + list.Count + " reported, " + rule.DepositCount + " pockets (" + path + ")");
            }
            catch (Exception e)
            {
                Errors++;
                DeLog.Info("DE.S8a crater deposits not read: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>The next poll reads the deposits again (the rule's cap changed).</summary>
        public static void ForceReload() { _ruleSeen = null; _nextMs = 0; }

        /// <summary>The material (subtype) and content byte of the tile voxel holding SN point <paramref name="sn"/>; false outside the tiles.</summary>
        public static bool ReadVoxel(Vec3 sn, out string subtype, out byte content)
        {
            subtype = null;
            content = 0;
            if (TerrainTiles.Map == null) return false;
            Vec3 w = TerrainTiles.Map.SnToSe(sn);
            TerrainTiles.TileInfo t = TerrainTiles.TileAt(w);
            if (t == null || t.Map == null || t.Map.Closed) return false;
            var st = t.Map.Storage as MyStorageBase;
            if (st == null) return false;
            Vector3D corner = t.Map.PositionLeftBottomCorner;
            var v = new Vector3I((int)Math.Floor(w.X - corner.X), (int)Math.Floor(w.Y - corner.Y), (int)Math.Floor(w.Z - corner.Z));
            if (v.X < 0 || v.Y < 0 || v.Z < 0 || v.X >= st.Size.X || v.Y >= st.Size.Y || v.Z >= st.Size.Z) return false;
            var data = new MyStorageData(MyStorageDataTypeFlags.ContentAndMaterial);
            data.Resize(v, v);
            st.ReadRange(data, MyStorageDataTypeFlags.ContentAndMaterial, 0, v, v);
            content = data[MyStorageDataTypeEnum.Content][0];
            byte m = data[MyStorageDataTypeEnum.Material][0];
            MyVoxelMaterialDefinition def = m == 255 ? null : MyDefinitionManager.Static.GetVoxelMaterialDefinition(m);
            subtype = def != null ? def.Id.SubtypeName : "#" + m.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>
 /// de.s8a ores (S8a.N10.crater): (1) the cube rule's pockets in solid rock, columns drawn deterministically over the map,
 /// at most one per depth band per column, until <paramref name="want"/> pockets were read; (2) a pocket under each
 /// reported deposit (or the given SN points with an expected subtype). Each row: kind, sn, depth, expected, got, content.
 /// </summary>
        public static Dictionary<string, object> Probe(int want, IList<object> points)
        {
            var r = new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", true } };
            S8aCraterOreRule rule = TerrainTiles.CraterOres;
            var rows = new List<object>();
            int solid = 0, match = 0, scanned = 0, depSolid = 0, depMatch = 0, cubeCovered = 0, depCovered = 0;
            var bands = new Dictionary<string, int>(StringComparer.Ordinal);
            ulong k = S8aCraterOres.Seed ^ 0x0E50E5UL;
            double cell = S8aCraterOres.CellM;
            for (int c = 0; c < 6000 && solid < want; c++)
            {
                k = S8aCraterOres.Mix(k + (ulong)c);
                long cx = (long)Math.Floor(((k & 0xFFFFUL) / 65535.0 * 2.0 - 1.0) * 1900.0 / cell);
                long cz = (long)Math.Floor((((k >> 16) & 0xFFFFUL) / 65535.0 * 2.0 - 1.0) * 1900.0 / cell);
                var seenBand = new HashSet<string>(StringComparer.Ordinal);
                for (long cy = -1; cy >= -(long)(1600.0 / cell) && solid < want; cy--)
                {
                    scanned++;
                    double px, py, pz, rad;
                    int row = S8aCraterOres.Pocket(cx, cy, cz, out px, out py, out pz, out rad);
                    if (row < 0 || rule.MaterialOfRow(row) == 255) continue;
                    if (rule.RowAt(px, py, pz) != row) { cubeCovered++; continue; }   // the cover rule: a centre within the map's cover holds no ore
                    S8aCraterOreRow o = S8aCraterOres.Rows[row];
                    string band = o.MinDepthM.ToString(CultureInfo.InvariantCulture) + "-" + o.MaxDepthM.ToString(CultureInfo.InvariantCulture);
                    if (seenBand.Contains(band)) continue;
                    string got;
                    byte content;
                    if (!ReadVoxel(new Vec3(px, py, pz), out got, out content) || content < 255) continue;
                    seenBand.Add(band);
                    solid++;
                    bool ok = got == o.Subtype;
                    if (ok) match++;
                    int nb;
                    bands.TryGetValue(band, out nb);
                    bands[band] = nb + 1;
                    rows.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "kind", "cube" }, { "sn", new object[] { px, py, pz } }, { "depth", -py }, { "band", band }, { "expected", o.Subtype }, { "got", got }, { "content", (long)content },
                    });
                }
            }
            // the deposits: the given points, else every reported one inside the tiles
            var deps = new List<KeyValuePair<Vec3, string>>();
            if (points != null)
            {
                foreach (object po in points)
                {
                    var pl = po as IList<object>;
                    if (pl == null || pl.Count < 3) continue;
                    string exp = pl.Count >= 4 ? Convert.ToString(pl[3], CultureInfo.InvariantCulture) : null;
                    deps.Add(new KeyValuePair<Vec3, string>(new Vec3(Convert.ToDouble(pl[0], CultureInfo.InvariantCulture), Convert.ToDouble(pl[1], CultureInfo.InvariantCulture),
                                                                    Convert.ToDouble(pl[2], CultureInfo.InvariantCulture)), exp));
                }
            }
            else
            {
                foreach (S8aCraterDeposit d in rule.Deposits)
                    deps.Add(new KeyValuePair<Vec3, string>(new Vec3(d.X, d.Y - S8aCraterOres.DepositSinkM, d.Z), S8aCraterOres.Rows[d.Row].Subtype));
            }
            foreach (KeyValuePair<Vec3, string> kv in deps)
            {
                // the cover rule: the pocket's centre may lie within the map's cover; the first point straight down that keeps it
                Vec3 at = kv.Key;
                int wantRow = kv.Value != null ? S8aCraterOres.RowOf(kv.Value) : -1;
                if (wantRow >= 0)
                {
                    bool found = false;
                    for (int step = 0; step < (int)S8aCraterOres.DepositRadiusM && !found; step++)
                        if (rule.RowAt(at.X, kv.Key.Y - step, at.Z) == wantRow) { at = new Vec3(at.X, kv.Key.Y - step, at.Z); found = true; }
                    if (!found) { depCovered++; continue; }
                }
                string got;
                byte content;
                if (!ReadVoxel(at, out got, out content)) continue;
                bool solidHere = content == 255;
                if (solidHere) depSolid++;
                bool ok = kv.Value != null && got == kv.Value;
                if (ok && solidHere) depMatch++;
                rows.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "kind", "deposit" }, { "sn", new object[] { at.X, at.Y, at.Z } }, { "depth", -at.Y }, { "expected", kv.Value }, { "got", got }, { "content", (long)content },
                });
            }
            r["rows"] = rows;
            r["cubes"] = (long)solid;
            r["cubesMatch"] = (long)match;
            r["scanned"] = (long)scanned;
            var bo = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in bands) bo[kv.Key] = (long)kv.Value;
            r["bands"] = bo;
            r["deposits"] = (long)deps.Count;
            r["depositsSolid"] = (long)depSolid;
            r["depositsMatch"] = (long)depMatch;
            r["reported"] = (long)rule.DepositCount;
            r["cubesCovered"] = (long)cubeCovered;
            r["depositsCovered"] = (long)depCovered;
            r["line"] = "de.s8a ores: " + match + "/" + solid + " cube pockets read their ore (" + bands.Count + " bands, " + scanned + " cubes scanned); deposits " +
                        depMatch + "/" + depSolid + " solid of " + deps.Count + " (" + rule.DepositCount + " reported)";
            return r;
        }
    }
}
