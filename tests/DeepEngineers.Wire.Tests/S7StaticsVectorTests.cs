using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// ROADMAP S7 (B3): the C# statics code (S7StaticsCodec, S7StaticsMath, S7StaticsFilter, S7StaticsBatchSet) against
 /// the independent Python reading of CONTRACT §2.3 / §5.4 "S7 conventions" (tools/worldmap/s7_ref.py, the committed
 /// vectors tests/s7/vectors/s7_statics.tsv; `python -I tools/worldmap/s7_ref.py check` proves the file is what the
 /// reference generates): box / MeshObb centres, staticAxes rows, half extents, s and the determinant; capsule end
 /// points and radii; spheres; the lowest y and the sea rule; Unity collider sizes and the shear test; FNV-1a 32 class
 /// hashes and the 1 cm dedup keys; the batch lattice; host names, splits, the 300/350 m hysteresis and the
 /// live/census precedence; the filter table (with S7.M7); payloads byte for byte (and the census copy); malformed
 /// payloads with the expected status; the grow-only batch set.
 /// </summary>
    [TestClass]
    public unsafe class S7StaticsVectorTests
    {
        private const double PosTol = 1e-6, DirTol = 1e-9;
        private static List<Rec> _records;

        private sealed class Rec
        {
            public string Kind;
            public int Line;
            public readonly Dictionary<string, string> F = new Dictionary<string, string>(StringComparer.Ordinal);

            private string Raw(string k)
            {
                string v;
                if (!F.TryGetValue(k, out v)) throw new KeyNotFoundException(Kind + "." + k + " (line " + Line + ")");
                return v;
            }

            public double D(string k) { return Dbl(Raw(k)); }
            public int I(string k) { return int.Parse(Raw(k), CultureInfo.InvariantCulture); }
            public long L(string k) { return long.Parse(Raw(k), CultureInfo.InvariantCulture); }
            public ulong U64(string k) { return ulong.Parse(Raw(k), CultureInfo.InvariantCulture); }
            public bool B(string k) { return I(k) != 0; }
            public string S(string k) { string v = Raw(k); return Encoding.UTF8.GetString(Hex(v.Substring(1))); }
            public byte[] X(string k) { return Hex(Raw(k).Substring(1)); }

            public double[] Ds(string k)
            {
                string v = Raw(k);
                if (v[0] != 'L') throw new FormatException(Kind + "." + k + " is not a list");
                return v.Length == 1 ? new double[0] : v.Substring(1).Split(',').Select(Dbl).ToArray();
            }

            public Vec3 V(string k, int offset = 0) { double[] d = Ds(k); return new Vec3(d[offset], d[offset + 1], d[offset + 2]); }
            public override string ToString() { return Kind + " line " + Line; }
        }

        private static double Dbl(string s) { return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }

        private static byte[] Hex(string h)
        {
            var b = new byte[h.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = byte.Parse(h.Substring(2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return b;
        }

        private static List<Rec> Records()
        {
            if (_records != null) return _records;
            string path = Path.Combine(S5Repo.Root, "tests", "s7", "vectors", "s7_statics.tsv");
            Assert.IsTrue(File.Exists(path), "vector file missing: " + path + " (python -I tools/worldmap/s7_ref.py vectors)");
            var list = new List<Rec>();
            int n = 0;
            foreach (string raw in File.ReadAllText(path, Encoding.UTF8).Split('\n'))
            {
                n++;
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                string[] cols = line.Split('\t');
                var r = new Rec { Kind = cols[0], Line = n };
                for (int i = 1; i < cols.Length; i++)
                {
                    int eq = cols[i].IndexOf('=');
                    r.F[cols[i].Substring(0, eq)] = cols[i].Substring(eq + 1);
                }
                list.Add(r);
            }
            _records = list;
            return list;
        }

        private static IEnumerable<Rec> Of(string kind)
        {
            List<Rec> l = Records().Where(r => r.Kind == kind).ToList();
            Assert.IsTrue(l.Count > 0, "no '" + kind + "' records in the vector file");
            return l;
        }

        private static SiteMap Site()
        {
            Rec s = Of("site").First();
            Vec3 c = s.V("C");
            Assert.AreEqual(SiteMap.SiteV1Test.C.X, c.X); Assert.AreEqual(SiteMap.SiteV1Test.C.Y, c.Y); Assert.AreEqual(SiteMap.SiteV1Test.C.Z, c.Z);
            Assert.AreEqual(SiteMap.SiteV1Test.Rs, s.D("Rs"), "the vectors use site v1 around the test centre");
            return SiteMap.SiteV1Test;
        }

        private static StaticPrimitive PrimOf(Rec r)
        {
            double[] c = r.Ds("c"), q = r.Ds("q"), s = r.Ds("size");
            return new StaticPrimitive
            {
                kind = (byte)r.I("kind"),
                centreSn = new F32x3((float)c[0], (float)c[1], (float)c[2]),
                qSn = new F32x4((float)q[0], (float)q[1], (float)q[2], (float)q[3]),
                size = new F32x3((float)s[0], (float)s[1], (float)s[2]),
            };
        }

        private static void Near(Vec3 want, Vec3 got, double tol, string what)
        {
            double d = Vec3.MaxAbsDiff(want, got);
            Assert.IsTrue(d <= tol, what + ": |diff| " + d.ToString("R", CultureInfo.InvariantCulture) + " > " + tol.ToString(CultureInfo.InvariantCulture) + " (want " + want + ", got " + got + ")");
        }

        // ------------------------------------------------------------------------------------------- geometry

        [TestMethod]
        public void Vectors_BoxAndMeshObb()
        {
            SiteMap site = Site();
            foreach (Rec r in Of("box"))
            {
                StaticPrimitive p = PrimOf(r);
                S7StaticSe se = S7StaticsMath.ToSe(site, ref p);
                Assert.IsTrue(se.Ok, r + ": " + se.Drop);
                Near(r.V("centre"), se.Centre, PosTol, r + " centre");
                Near(r.V("rows", 0), se.Axes.Right, DirTol, r + " Right");
                Near(r.V("rows", 3), se.Axes.Up, DirTol, r + " Up");
                Near(r.V("rows", 6), se.Axes.Backward, DirTol, r + " Backward");
                Near(r.V("half"), se.Half, PosTol, r + " half extents");
                Assert.AreEqual(r.D("s"), se.S, 1e-12, r + " s");
                Assert.AreEqual(r.D("det"), se.Determinant, 1e-9, r + " det");
            }
        }

        [TestMethod]
        public void Vectors_CapsuleAndSphere()
        {
            SiteMap site = Site();
            foreach (Rec r in Of("capsule"))
            {
                StaticPrimitive p = PrimOf(r);
                S7StaticSe se = S7StaticsMath.ToSe(site, ref p);
                Assert.IsTrue(se.Ok, r.ToString());
                Near(r.V("a"), se.A, PosTol, r + " end A");
                Near(r.V("b"), se.B, PosTol, r + " end B");
                Near(r.V("centre"), se.Centre, PosTol, r + " centre");
                Assert.AreEqual(r.D("radius"), se.Radius, 1e-9, r + " radius");
                Assert.AreEqual(r.D("s"), se.S, 1e-12, r + " s");
            }
            foreach (Rec r in Of("sphere"))
            {
                StaticPrimitive p = PrimOf(r);
                S7StaticSe se = S7StaticsMath.ToSe(site, ref p);
                Near(r.V("centre"), se.Centre, PosTol, r + " centre");
                Assert.AreEqual(r.D("radius"), se.Radius, 1e-9, r + " radius");
            }
        }

        [TestMethod]
        public void Vectors_LowestY_AndTheSea()
        {
            foreach (Rec r in Of("lowest"))
            {
                StaticPrimitive p = PrimOf(r);
                Assert.AreEqual(r.D("y"), S7StaticsMath.LowestY(ref p), 1e-9, r.ToString());
                Assert.AreEqual(r.B("above"), S7StaticsMath.EntirelyAboveSea(ref p), r.ToString());
            }
        }

        [TestMethod]
        public void Vectors_UnitySizes_AndShear()
        {
            foreach (Rec r in Of("boxhalf"))
            {
                Near(r.V("half"), S7StaticsMath.BoxHalf(r.V("size"), r.V("scale")), 1e-12, r + " box half");
                Near(r.V("mesh"), S7StaticsMath.MeshHalf(r.V("size"), r.V("scale")), 1e-12, r + " mesh half");
            }
            foreach (Rec r in Of("capsize"))
            {
                Near(r.V("out"), S7StaticsMath.CapsuleSize(r.D("radius"), r.D("height"), r.I("dir"), r.V("scale")), 1e-12, r + " capsule size");
                Assert.AreEqual(r.D("sphere"), S7StaticsMath.SphereRadius(r.D("radius"), r.V("scale")), 1e-12, r + " sphere radius");
            }
            foreach (Rec r in Of("shear"))
            {
                Assert.AreEqual(r.D("shear"), S7StaticsMath.Shear(r.V("c0"), r.V("c1"), r.V("c2")), 1e-12, r.ToString());
                Assert.AreEqual(r.B("skewed"), S7StaticsMath.IsSkewed(r.V("c0"), r.V("c1"), r.V("c2")), r.ToString());
            }
        }

        // ------------------------------------------------------------------------------------------- ids, batches, hosts

        [TestMethod]
        public void Vectors_HashesAndDedupKeys()
        {
            foreach (Rec r in Of("hash"))
                Assert.AreEqual((uint)r.L("fnv32"), S7StaticsMath.ClassIdHash(r.S("id")), r.ToString());
            foreach (Rec r in Of("dedup"))
            {
                double[] c = r.Ds("c"), q = r.Ds("q"), size = r.Ds("size"), qs = r.Ds("qs"), rot = r.Ds("rot"), qr = r.Ds("qr");
                for (int i = 0; i < 3; i++) Assert.AreEqual((int)q[i], S7StaticsMath.Quant((float)c[i]), r + " quant " + i);
                for (int i = 0; i < 3; i++) Assert.AreEqual((int)qs[i], S7StaticsMath.Quant((float)size[i]), r + " size quant " + i);
                for (int i = 0; i < 4; i++) Assert.AreEqual((int)qr[i], S7StaticsMath.QuantRot((float)rot[i]), r + " rotation quant " + i);
                // review F16: the key carries the size (1 cm) and the rotation (1/1000) as well
                Assert.AreEqual(r.U64("key"), S7StaticsMath.DedupKey((byte)r.I("kind"), (float)c[0], (float)c[1], (float)c[2], (float)size[0], (float)size[1], (float)size[2],
                                                                      (float)rot[0], (float)rot[1], (float)rot[2], (float)rot[3], (uint)r.L("cls")), r.ToString());
            }
        }

        [TestMethod]
        public void Vectors_BatchLattice()
        {
            foreach (Rec r in Of("batch"))
            {
                Vec3 p = r.V("p");
                double[] b = r.Ds("b");
                int bx, by, bz;
                S7StaticsMath.BatchOfSn(p, out bx, out by, out bz);
                Assert.AreEqual((int)b[0], bx, r.ToString()); Assert.AreEqual((int)b[1], by, r.ToString()); Assert.AreEqual((int)b[2], bz, r.ToString());
                Near(r.V("mins"), S7StaticsMath.BatchMinsSn(bx, by, bz), 0.0, r + " mins");
                Assert.AreEqual(r.D("d"), S7StaticsMath.DistanceToBatchSn(p, bx + 1, by, bz - 1), 1e-9, r + " distance");
            }
        }

        [TestMethod]
        public void Vectors_HostsPartsLifeCycleAndPrecedence()
        {
            foreach (Rec r in Of("host"))
            {
                double[] b = r.Ds("b");
                Assert.AreEqual(r.S("name"), S7StaticsMath.HostName((int)b[0], (int)b[1], (int)b[2], (uint)r.L("epoch"), r.I("part")), r.ToString());
                Assert.AreEqual(r.S("census"), S7StaticsMath.CensusFileName((int)b[0], (int)b[1], (int)b[2]), r.ToString());
            }
            foreach (Rec r in Of("parts"))
            {
                int count = r.I("count"), parts = S7StaticsMath.HostParts(count);
                Assert.AreEqual(r.I("parts"), parts, r.ToString());
                double[] ranges = r.Ds("ranges");
                for (int k = 0; k < parts; k++)
                {
                    int start, n;
                    S7StaticsMath.HostRange(k, count, out start, out n);
                    Assert.AreEqual((int)ranges[2 * k], start, r + " start " + k);
                    Assert.AreEqual((int)ranges[2 * k + 1], n, r + " n " + k);
                }
            }
            foreach (Rec r in Of("wanted"))
                Assert.AreEqual(r.B("want"), S7StaticsMath.HostWanted(r.B("exists"), r.D("d")), r.ToString());
            foreach (Rec r in Of("supersedes"))
                Assert.AreEqual(r.B("out"), S7StaticsMath.Supersedes(r.B("have"), r.B("curCensus"), (uint)r.I("curEpoch"), r.B("inCensus"), (uint)r.I("inEpoch")), r.ToString());
        }

        [TestMethod]
        public void Vectors_FilterTable()
        {
            foreach (Rec r in Of("filter"))
            {
                var c = new S7StaticsCandidate
                {
                    Enabled = r.B("enabled"), Trigger = r.B("trigger"), Layer = r.I("layer"), Proxy = r.B("proxy"), Bridge = r.B("bridge"),
                    UnderWorld = r.B("world"), Creature = r.B("creature"), Pickupable = r.B("pickupable"), Vehicle = r.B("vehicle"),
                    Freeze = r.B("freeze"), WorldForces = r.B("worldForces"), Rigidbody = r.B("rb"), RigidbodyKinematic = r.B("kin"),
                    LiveMixin = r.B("live"), AllowListed = r.B("allow"), AllowListedWorldForces = r.B("allowWf"),
                };
                Assert.AreEqual((S7StaticsReject)r.I("reason"), S7StaticsFilter.Decide(ref c, r.B("mutant")), r.ToString());
            }
            // review F5: the reporter's layer mask (every layer the player collides with, minus TerrainCollider and Trigger)
            foreach (Rec r in Of("filtermask"))
            {
                var c = new S7StaticsCandidate
                {
                    Enabled = r.B("enabled"), Trigger = r.B("trigger"), Layer = r.I("layer"), Proxy = r.B("proxy"), Bridge = r.B("bridge"),
                    UnderWorld = r.B("world"), Creature = r.B("creature"), Pickupable = r.B("pickupable"), Vehicle = r.B("vehicle"),
                    Freeze = r.B("freeze"), WorldForces = r.B("worldForces"), Rigidbody = r.B("rb"), RigidbodyKinematic = r.B("kin"),
                    LiveMixin = r.B("live"), AllowListed = r.B("allow"), AllowListedWorldForces = r.B("allowWf"),
                };
                int mask = S7StaticsFilter.PlayerSolidMask(l => l != 9, 30, 21);
                Assert.AreEqual(r.I("mask"), mask, r + " mask");
                Assert.AreEqual((S7StaticsReject)r.I("reason"), S7StaticsFilter.Decide(ref c, r.B("mutant"), mask), r.ToString());
            }
        }

        // ------------------------------------------------------------------------------------------- codec

        [TestMethod]
        public void Vectors_PayloadsByteForByte()
        {
            foreach (Rec r in Of("payload"))
            {
                double[] b = r.Ds("b");
                int n = r.I("count");
                double[] flat = r.Ds("prims");
                Assert.AreEqual(13 * n, flat.Length, r.ToString());
                var prims = new StaticPrimitive[n];
                for (int i = 0; i < n; i++)
                {
                    int o = 13 * i;
                    prims[i] = new StaticPrimitive
                    {
                        kind = (byte)flat[o], flags = (byte)flat[o + 1],
                        centreSn = new F32x3((float)flat[o + 2], (float)flat[o + 3], (float)flat[o + 4]),
                        qSn = new F32x4((float)flat[o + 5], (float)flat[o + 6], (float)flat[o + 7], (float)flat[o + 8]),
                        size = new F32x3((float)flat[o + 9], (float)flat[o + 10], (float)flat[o + 11]),
                        classIdHash = (uint)flat[o + 12],
                    };
                }
                var buf = new byte[S7StaticsCodec.PayloadBytes(n)];
                int len = S7StaticsCodec.Encode((int)b[0], (int)b[1], (int)b[2], (uint)r.L("epoch"), prims, n, buf);
                CollectionAssert.AreEqual(r.X("hex"), buf.Take(len).ToArray(), r + " payload bytes");
                var cen = new byte[len];
                fixed (byte* p = cen) S7StaticsCodec.Encode((int)b[0], (int)b[1], (int)b[2], (uint)r.L("epoch"), prims, 0, n, p, len, S7Conventions.StaticFlags.Census);
                CollectionAssert.AreEqual(r.X("census"), cen, r + " census payload bytes");
                var dst = new StaticPrimitive[Math.Max(1, n)];
                BatchStaticsHeader h;
                int count;
                Assert.AreEqual(S7StaticsStatus.Ok, S7StaticsCodec.TryDecode(r.X("hex"), len, out h, dst, out count), r.ToString());
                Assert.AreEqual(n, count);
            }
        }

        [TestMethod]
        public void Vectors_MalformedPayloads()
        {
            var dst = new StaticPrimitive[8];
            foreach (Rec r in Of("bad"))
            {
                byte[] data = r.X("hex");
                BatchStaticsHeader h;
                int count;
                S7StaticsStatus st = S7StaticsCodec.TryDecode(data, data.Length, out h, dst, out count);
                Assert.AreEqual((S7StaticsStatus)r.I("status"), st, r.ToString());
                Assert.AreEqual(0, count, r + ": nothing partially accepted");
            }
        }

        [TestMethod]
        public void Vectors_BatchSet()
        {
            var set = new S7StaticsBatchSet(4, 5, 6);
            foreach (Rec r in Of("set"))
            {
                double[] c = r.Ds("c"), sz = r.Ds("size"), rot = r.Ds("rot");
                var p = new StaticPrimitive
                {
                    kind = (byte)r.I("kind"), centreSn = new F32x3((float)c[0], (float)c[1], (float)c[2]),
                    qSn = new F32x4((float)rot[0], (float)rot[1], (float)rot[2], (float)rot[3]),
                    size = new F32x3((float)sz[0], (float)sz[1], (float)sz[2]), classIdHash = (uint)r.L("cls"),
                };
                Assert.AreEqual((S7StaticsAdd)r.I("result"), set.Add(ref p), r.ToString());
                Assert.AreEqual(r.I("count"), set.Count, r.ToString());
            }
        }
    }
}
