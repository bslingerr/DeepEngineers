using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DeepEngineers.Wire
{
    /// <summary>One placed chunk entity in Subnautica coordinates of a chart (a baked prefab by class id, or an entity slot).</summary>
    public struct S8aSampleRecord
    {
        public ulong Key;
        public bool Slot;
        public string ClassId, Family;
        public int Level, SlotBiome, SlotAllowed;
        public double SlotDensity;
        public double X, Y, Z, Qx, Qy, Qz, Qw, Sx, Sy, Sz;
    }

    /// <summary>
 /// S8a planet v2, the sample (route B's first slice; docs/S8A-PLANET-V2.md section 12): the chunk's entities as placed, handed
 /// from SE (which owns the planet, the placement and the charts) to Subnautica (which spawns them), as the ore outcrops are.
 /// SE writes <c>&lt;store&gt;\&lt;storeKey&gt;\sample-entities.tsv</c> (rewritten whole, atomically, at most twice a second, when the camera
 /// moved <see cref="RewriteMoveM"/> or the chart changed): line 1 <c>de-s8a-sample 1 seq chartSeq chartKey x16 count sampleId x16
 /// camNativeX camNativeY camNativeZ</c> (the camera's point in the real map's coordinates, the oracle's reference), then per record
 /// (tab-separated) key x16, kind b|s, class id ('-' for a slot), family, cell level, SN x y z, rotation x y z w, scale x y z, slot
 /// biome, slot allowed types, slot density. Only records within their level's radius (<see cref="RadiusOf"/>) +
 /// <see cref="FileMarginM"/> of the camera are written. Subnautica keeps one object per record within its level's radius
 /// (despawn beyond + <see cref="DespawnExtraM"/>), moves it when its record moves (a chart step), never saves it.
 /// </summary>
    public static class S8aSampleEntities
    {
        public const string Magic = "de-s8a-sample 1";
        public const string FileName = "sample-entities.tsv";
        public const double FileMarginM = 60.0, DespawnExtraM = 40.0, RewriteMoveM = 20.0, WriteHz = 2.0;
        public const double SlotJitterM = 4.0;
        public static readonly string[] Families = { "flora", "prop", "creature", "outcrop", "light", "vfx", "other", "slot" };

        /// <summary>Subnautica's cell levels (LargeWorldEntity.CellLevel: Near 0, Medium 1, Far 2, VeryFar 3) -> the spawn radius on the planet.</summary>
        public static double RadiusOf(int level)
        {
            switch (level)
            {
                case 0: return 100.0;
                case 1: return 150.0;
                case 2: return 250.0;
                default: return 400.0;
            }
        }

        public static string PathOf(string root, ulong storeKey) { return Path.Combine(S8aStore.StoreFolder(root, storeKey), FileName); }

        private static string F(double v, string fmt) { return v.ToString(fmt, CultureInfo.InvariantCulture); }

        public static string Format(long seq, uint chartSeq, ulong chartKey, ulong sampleId, double camNx, double camNy, double camNz, IList<S8aSampleRecord> recs)
        {
            var sb = new StringBuilder(64 + recs.Count * 120);
            sb.Append(Magic).Append(' ').Append(seq.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(chartSeq.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(chartKey.ToString("x16", CultureInfo.InvariantCulture)).Append(' ').Append(recs.Count.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(sampleId.ToString("x16", CultureInfo.InvariantCulture)).Append(' ').Append(F(camNx, "0.###")).Append(' ').Append(F(camNy, "0.###")).Append(' ')
              .Append(F(camNz, "0.###")).Append('\n');
            foreach (S8aSampleRecord r in recs)
            {
                sb.Append(r.Key.ToString("x16", CultureInfo.InvariantCulture)).Append('\t').Append(r.Slot ? "s" : "b").Append('\t').Append(string.IsNullOrEmpty(r.ClassId) ? "-" : r.ClassId)
                  .Append('\t').Append(r.Family ?? "other").Append('\t').Append(r.Level.ToString(CultureInfo.InvariantCulture))
                  .Append('\t').Append(F(r.X, "0.####")).Append('\t').Append(F(r.Y, "0.####")).Append('\t').Append(F(r.Z, "0.####"))
                  .Append('\t').Append(F(r.Qx, "0.######")).Append('\t').Append(F(r.Qy, "0.######")).Append('\t').Append(F(r.Qz, "0.######")).Append('\t').Append(F(r.Qw, "0.######"))
                  .Append('\t').Append(F(r.Sx, "0.####")).Append('\t').Append(F(r.Sy, "0.####")).Append('\t').Append(F(r.Sz, "0.####"))
                  .Append('\t').Append(r.SlotBiome.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(r.SlotAllowed.ToString(CultureInfo.InvariantCulture))
                  .Append('\t').Append(F(r.SlotDensity, "0.######")).Append('\n');
            }
            return sb.ToString();
        }

        public static bool TryParse(string text, out long seq, out uint chartSeq, out ulong chartKey, out ulong sampleId, out double[] camNative, out List<S8aSampleRecord> recs)
        {
            seq = 0;
            chartSeq = 0;
            chartKey = sampleId = 0;
            camNative = new double[3];
            recs = new List<S8aSampleRecord>();
            if (string.IsNullOrEmpty(text)) return false;
            string[] lines = text.Split('\n');
            string[] h = lines[0].TrimEnd('\r').Split(' ');
            if (h.Length < 10 || h[0] + " " + h[1] != Magic) return false;
            int count;
            if (!long.TryParse(h[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out seq) ||
                !uint.TryParse(h[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out chartSeq) ||
                !ulong.TryParse(h[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out chartKey) ||
                !int.TryParse(h[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) ||
                !ulong.TryParse(h[6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out sampleId)) return false;
            for (int k = 0; k < 3; k++)
                if (!double.TryParse(h[7 + k], NumberStyles.Float, CultureInfo.InvariantCulture, out camNative[k])) return false;
            for (int li = 1; li < lines.Length; li++)
            {
                string[] f = lines[li].TrimEnd('\r').Split('\t');
                if (f.Length < 18) continue;
                var r = new S8aSampleRecord();
                var v = new double[10];
                bool ok = ulong.TryParse(f[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r.Key) && (f[1] == "b" || f[1] == "s") &&
                          int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out r.Level);
                for (int k = 0; k < 10 && ok; k++) ok = double.TryParse(f[5 + k], NumberStyles.Float, CultureInfo.InvariantCulture, out v[k]);
                ok = ok && int.TryParse(f[15], NumberStyles.Integer, CultureInfo.InvariantCulture, out r.SlotBiome) &&
                     int.TryParse(f[16], NumberStyles.Integer, CultureInfo.InvariantCulture, out r.SlotAllowed) &&
                     double.TryParse(f[17], NumberStyles.Float, CultureInfo.InvariantCulture, out r.SlotDensity);
                if (!ok) continue;
                r.Slot = f[1] == "s";
                r.ClassId = f[2] == "-" ? null : f[2];
                r.Family = f[3];
                r.X = v[0]; r.Y = v[1]; r.Z = v[2]; r.Qx = v[3]; r.Qy = v[4]; r.Qz = v[5]; r.Qw = v[6]; r.Sx = v[7]; r.Sy = v[8]; r.Sz = v[9];
                recs.Add(r);
            }
            return recs.Count == count;
        }

        /// <summary>The deterministic offset of a slot's j-th extra instance (Subnautica draws insideUnitSphere x 4 m; here a key hash).</summary>
        public static void SlotJitter(ulong key, int j, out double dx, out double dy, out double dz)
        {
            ulong h = key ^ (0x9E3779B97F4A7C15UL * (ulong)(j + 1));
            h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33; h *= 0xc4ceb9fe1a85ec53UL; h ^= h >> 33;
            dx = ((h & 0xFFFF) / 65535.0 * 2.0 - 1.0) * SlotJitterM * 0.577;
            dy = (((h >> 16) & 0xFFFF) / 65535.0 * 2.0 - 1.0) * SlotJitterM * 0.577;
            dz = (((h >> 32) & 0xFFFF) / 65535.0 * 2.0 - 1.0) * SlotJitterM * 0.577;
        }
    }
}
