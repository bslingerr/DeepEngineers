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
 /// ROADMAP S6: the C# S6 conventions and math (CombatConventions, CollisionSkinMath, SkinCodec, DamageRules,
 /// ShapeBudget) against the independent Python reading (tools/worldmap/wire_ref.py <c>s6vectors -</c>, generated on
 /// the fly, never committed; the S5VectorTests pattern): conventions text and hash, rounding, the section lattice,
 /// segment splits, cell boxes, model-shape OBBs and their split, the segmentation stand-in, sections and SkinSet
 /// payloads byte for byte with their content hashes, bad payloads, the refresh rule, subgrid placement and hit point
 /// frames, the character capsule, the shape budget, K corner errors, the S6.M5 mutant family (red), the dedup window,
 /// C8 record checks in the contract's order, factors and hit cells.
 /// </summary>
    [TestClass]
    public unsafe class S6VectorTests
    {
        private static List<VRec> _records;
        private static readonly object Gate = new object();

        private sealed class VRec
        {
            public string Kind;
            public readonly Dictionary<string, string> F = new Dictionary<string, string>(StringComparer.Ordinal);

            private string Raw(string k)
            {
                string v;
                if (!F.TryGetValue(k, out v)) throw new KeyNotFoundException(Kind + "." + k);
                return v;
            }

            public bool Has(string k) { return F.ContainsKey(k); }
            public ulong U64(string k) { return ulong.Parse(Raw(k), CultureInfo.InvariantCulture); }
            public int I(string k) { return int.Parse(Raw(k), CultureInfo.InvariantCulture); }
            public uint U(string k) { return uint.Parse(Raw(k), CultureInfo.InvariantCulture); }
            public bool B(string k) { return I(k) != 0; }
            public double D(string k) { return Dbl(Raw(k)); }
            public string S(string k) { return Encoding.UTF8.GetString(Hex(Raw(k).Substring(1))); }
            public byte[] X(string k) { return Hex(Raw(k).Substring(1)); }

            private string[] Items(string k)
            {
                string v = Raw(k);
                if (v[0] != 'L') throw new FormatException(Kind + "." + k + " is not a list");
                return v.Length == 1 ? new string[0] : v.Substring(1).Split(',');
            }

            public double[] Ds(string k) { return Items(k).Select(Dbl).ToArray(); }
            public int[] Is(string k) { return Items(k).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray(); }
            public ulong[] U64s(string k) { return Items(k).Select(x => ulong.Parse(x, CultureInfo.InvariantCulture)).ToArray(); }
            public Vec3 V(string k) { double[] d = Ds(k); return new Vec3(d[0], d[1], d[2]); }
            public Quat Q(string k) { double[] d = Ds(k); return new Quat(d[0], d[1], d[2], d[3]); }

            private static double Dbl(string s)
            {
                if (s[0] != 'd') throw new FormatException("not a double: " + s);
                return BitConverter.Int64BitsToDouble((long)ulong.Parse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
        }

        private static byte[] Hex(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = byte.Parse(hex.Substring(2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return b;
        }

        private static List<VRec> Records()
        {
            lock (Gate)
            {
                if (_records != null) return _records;
                string text = S5Repo.RunPython(Path.Combine("tools", "worldmap", "wire_ref.py"), "s6vectors", "-");
                var recs = new List<VRec>();
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    string[] tok = line.Split(' ');
                    var r = new VRec { Kind = tok[0] };
                    for (int i = 1; i < tok.Length; i++)
                    {
                        int eq = tok[i].IndexOf('=');
                        r.F[tok[i].Substring(0, eq)] = tok[i].Substring(eq + 1);
                    }
                    recs.Add(r);
                }
                Console.WriteLine("S6 vectors: " + recs.Count + " records");
                _records = recs;
                return recs;
            }
        }

        private static IEnumerable<VRec> Kind(string kind) { return Records().Where(r => r.Kind == kind); }

        private static SkinBoxData Box(double[] d, int off, uint flags)
        {
            return new SkinBoxData
            {
                Centre = new Vec3(d[off], d[off + 1], d[off + 2]), Half = new Vec3(d[off + 3], d[off + 4], d[off + 5]),
                E0 = new Vec3(d[off + 6], d[off + 7], d[off + 8]), E1 = new Vec3(d[off + 9], d[off + 10], d[off + 11]),
                E2 = new Vec3(d[off + 12], d[off + 13], d[off + 14]), Flags = flags,
            };
        }

        private static double BoxDiff(SkinBoxData a, SkinBoxData b)
        {
            return new[] { (a.Centre - b.Centre).Length, (a.Half - b.Half).Length, (a.E0 - b.E0).Length, (a.E1 - b.E1).Length, (a.E2 - b.E2).Length }.Max();
        }

        private static SeMatrix Rows(double[] d)
        {
            return new SeMatrix(new Vec3(d[0], d[1], d[2]), new Vec3(d[3], d[4], d[5]), new Vec3(d[6], d[7], d[8]),
                d.Length >= 12 ? new Vec3(d[9], d[10], d[11]) : Vec3.Zero);
        }

        private static DamageRecord RecordAt(byte[] b, int offset)
        {
            fixed (byte* p = &b[offset]) return *(DamageRecord*)p;
        }

        [TestMethod]
        public void S6Vectors_FileHasEveryKind()
        {
            var counts = Records().GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count());
            foreach (string k in new[] { "conv", "dtype", "round", "floordiv", "section", "split", "cellbox", "obb", "splitobb", "merge", "build",
                                         "decode", "refresh", "place", "capsule", "budget", "cornererr", "m5", "m5case", "dedup", "damage",
                                         "validate", "factor", "factorclass", "scale", "hitcell", "nearest", "hitid" })
                Assert.IsTrue(counts.ContainsKey(k) && counts[k] > 0, "missing kind " + k);
        }

        [TestMethod]
        public void S6Vectors_Conventions()
        {
            VRec c = Kind("conv").Single();
            Assert.AreEqual(c.S("text"), CombatConventions.CanonicalText(), "Python and C# canonical texts");
            Assert.AreEqual(c.U64("hash"), CombatConventions.Hash());
            Assert.AreEqual(CombatConventions.PinnedHash, c.U64("hash"));
            Assert.AreEqual(54, c.I("lines"));
            Assert.AreEqual(2068, c.I("bytes"));
            foreach (VRec r in Kind("dtype"))
            {
                string name = r.S("name");
                Assert.AreEqual(name.Length == 0 ? null : name, CombatConventions.SeDamageTypeName((ushort)r.I("t")));
            }
        }

        [TestMethod]
        public void S6Vectors_RoundingAndLattice()
        {
            foreach (VRec r in Kind("round")) Assert.AreEqual(r.I("r"), CollisionSkinMath.RoundAway(r.D("x")), "x=" + r.D("x").ToString("R"));
            foreach (VRec r in Kind("floordiv")) Assert.AreEqual(r.I("q"), CollisionSkinMath.FloorDiv(r.I("a"), r.I("b")));
            int n = 0;
            foreach (VRec r in Kind("section"))
            {
                int ix, iy, iz;
                bool ok = CollisionSkinMath.TrySectionIndex(r.V("c"), r.D("g"), r.B("large"), out ix, out iy, out iz);
                Assert.AreEqual(r.B("ok"), ok, "c=" + r.V("c"));
                if (!ok) continue;
                Assert.AreEqual(r.I("ix"), ix); Assert.AreEqual(r.I("iy"), iy); Assert.AreEqual(r.I("iz"), iz);
                Assert.AreEqual(r.U("id"), CollisionSkinMath.SectionId(ix, iy, iz));
                Assert.AreEqual(r.V("centre"), CollisionSkinMath.SectionCentre(ix, iy, iz, r.D("g"), r.B("large")), "exact");
                n++;
            }
            Assert.IsTrue(n > 100);
        }

        [TestMethod]
        public void S6Vectors_SplitsAndCellBoxes()
        {
            foreach (VRec r in Kind("split"))
            {
                int[] s = r.Is("seg");
                var o = new List<CellRange>();
                CollisionSkinMath.SplitSegment(new CellRange(s[0], s[1], s[2], s[3], s[4], s[5]), r.B("large"), o);
                int[] want = r.Is("pieces");
                CollectionAssert.AreEqual(want, o.SelectMany(p => new[] { p.MinX, p.MinY, p.MinZ, p.MaxX, p.MaxY, p.MaxZ }).ToArray(), "seg " + string.Join(",", s));
            }
            foreach (VRec r in Kind("cellbox"))
            {
                int[] c = r.Is("r");
                SkinBoxData b = CollisionSkinMath.CellBox(new CellRange(c[0], c[1], c[2], c[3], c[4], c[5]), r.D("g"));
                Assert.AreEqual(r.V("c"), b.Centre, "exact");
                Assert.AreEqual(r.V("h"), b.Half, "exact");
            }
        }

        [TestMethod]
        public void S6Vectors_ObbsAndTheirSplit()
        {
            int rotated = 0;
            foreach (VRec r in Kind("obb"))
            {
                SkinBoxData b = CollisionSkinMath.ObbFromModelAabb(r.V("mn"), r.V("mx"), Rows(r.Ds("orient")), r.V("t"), r.D("scale"));
                SkinBoxData want = Box(r.Ds("box"), 0, r.U("flags"));
                Assert.AreEqual(want.Flags, b.Flags);
                Assert.IsTrue(BoxDiff(b, want) < 1e-12, "obb " + BoxDiff(b, want));
                if (b.IsRotated) rotated++;
            }
            Assert.IsTrue(rotated > 10);
            foreach (VRec r in Kind("splitobb"))
            {
                var o = new List<SkinBoxData>();
                CollisionSkinMath.SplitObb(Box(r.Ds("box"), 0, r.U("flags")), CollisionSkinMath.MaxHalfExtent, o);
                double[] p = r.Ds("pieces");
                Assert.AreEqual(p.Length / 15, o.Count);
                for (int i = 0; i < o.Count; i++)
                {
                    Assert.IsTrue(BoxDiff(o[i], Box(p, 15 * i, r.U("flags"))) < 1e-12);
                    Assert.AreEqual(r.U("flags"), o[i].Flags);
                }
            }
        }

        [TestMethod]
        public void S6Vectors_SegmentationStandIn()
        {
            foreach (VRec r in Kind("merge"))
            {
                int[] c = r.Is("cells");
                var cells = new List<I32x3>();
                for (int i = 0; i < c.Length; i += 3) cells.Add(new I32x3(c[i], c[i + 1], c[i + 2]));
                var o = new List<CellRange>();
                CollisionSkinMath.MergeCellsGreedy(cells, o);
                CollectionAssert.AreEqual(r.Is("boxes"), o.SelectMany(p => new[] { p.MinX, p.MinY, p.MinZ, p.MaxX, p.MaxY, p.MaxZ }).ToArray());
                string err;
                Assert.IsTrue(CollisionSkinMath.CheckCoverage(cells, o, out err), err);
            }
        }

        [TestMethod]
        public void S6Vectors_SectionsAndPayloadsByteForByte()
        {
            foreach (VRec r in Kind("build"))
            {
                int[] s = r.Is("segs");
                var segs = new List<CellRange>();
                for (int i = 0; i < s.Length; i += 6) segs.Add(new CellRange(s[i], s[i + 1], s[i + 2], s[i + 3], s[i + 4], s[i + 5]));
                double[] ob = r.Ds("obbs");
                int[] of = r.Is("obbflags");
                var obbs = new List<SkinBoxData>();
                for (int i = 0; i < of.Length; i++) obbs.Add(Box(ob, 15 * i, (uint)of[i]));
                int dropped;
                List<SkinSectionData> secs = CollisionSkinMath.BuildSections(segs, obbs, r.D("g"), r.B("large"), out dropped);
                Assert.AreEqual(r.I("dropped"), dropped);
                CollectionAssert.AreEqual(r.U64s("ids"), secs.Select(x => (ulong)x.SectionId).ToArray());
                CollectionAssert.AreEqual(r.U64s("hashes"), secs.Select(x => x.ContentHash).ToArray(), "content hashes");
                double[] rho = r.Ds("rho");
                for (int i = 0; i < secs.Count; i++) Assert.AreEqual(rho[i], CollisionSkinMath.RhoMax(secs[i]), 1e-12);
                byte[] payload = SkinCodec.Encode(r.U64("entity"), r.U("version"), secs);
                CollectionAssert.AreEqual(r.X("payload"), payload, "SkinSet payload byte for byte");
                string err;
                Assert.IsTrue(SkinCodec.Validate(secs, out err), err);
            }
        }

        [TestMethod]
        public void S6Vectors_Decode()
        {
            int bad = 0;
            foreach (VRec r in Kind("decode"))
            {
                byte[] p = r.X("payload");
                var got = new List<SkinSectionData>();
                SkinSetHeader h;
                int ignored;
                string err;
                bool ok = SkinCodec.TryDecode(p, 0, p.Length, out h, got, out ignored, out err);
                Assert.AreEqual(r.B("ok"), ok, r.S("reason") + ": " + err);
                if (ok)
                {
                    Assert.AreEqual(r.Has("ignored") ? r.I("ignored") : 0, ignored);
                    if (ignored == 0) CollectionAssert.AreEqual(p, SkinCodec.Encode(h.entityId, h.skinVersion, got), "re-encode");
                }
                else bad++;
            }
            Assert.IsTrue(bad >= 15);
        }

        [TestMethod]
        public void S6Vectors_RefreshPlacementAndHitPoint()
        {
            foreach (VRec r in Kind("refresh"))
                Assert.AreEqual(r.B("need"), CollisionSkinMath.NeedsRefresh(r.D("sA"), r.V("uA"), r.D("s"), r.V("u"), r.D("rho")));
            foreach (VRec r in Kind("place"))
            {
                Vec3 o = r.V("o");
                Quat q = r.Q("q");
                double s = r.D("s");
                SeMatrix rel = Rows(r.Ds("rel"));
                bool sub = r.B("subgrid");
                Assert.IsTrue((CollisionSkinMath.SectionRootPosition(o, q, s, rel, r.V("csec")) - r.V("rootpos")).Length < 1e-9);
                Quat rq = CollisionSkinMath.SectionRootRotation(q, rel);
                Assert.IsTrue(Quat.RotationDistance(rq, r.Q("rootrot")) < 1e-12);
                Assert.IsTrue(Quat.RotationDistance(CollisionSkinMath.SubgridRotation(rel), r.Q("subrot")) < 1e-12);
                Assert.IsTrue((CollisionSkinMath.UpInSection(rq) - r.V("up")).Length < 1e-12);
                Assert.IsTrue((CollisionSkinMath.LocalToWorld(o, q, s, rel, sub, r.V("l")) - r.V("w")).Length < 1e-9);
                Assert.IsTrue((CollisionSkinMath.HitLocal(o, q, s, rel, sub, r.V("w")) - r.V("hit")).Length < 1e-9);
                Assert.AreEqual(r.D("tilt"), CollisionSkinMath.TiltDegrees(q), 1e-6);
            }
        }

        [TestMethod]
        public void S6Vectors_CapsuleAndBudget()
        {
            foreach (VRec r in Kind("capsule"))
            {
                float[] rows = r.Ds("rows").Select(x => (float)x).ToArray();
                Vec3 c, a, cw;
                Quat qw;
                CollisionSkinMath.CharacterCapsule(rows, r.B("valid"), out c, out a);
                Assert.IsTrue((c - r.V("c")).Length < 1e-12 && (a - r.V("a")).Length < 1e-12);
                CollisionSkinMath.CharacterCapsuleWorld(r.V("o"), r.Q("q"), r.D("s"), c, a, out cw, out qw);
                Assert.IsTrue((cw - r.V("cw")).Length < 1e-9);
                Assert.IsTrue(Quat.RotationDistance(qw, r.Q("qw")) < 1e-12);
            }
            foreach (VRec r in Kind("budget"))
            {
                int[] pending = r.Is("pending");
                var grant = new int[pending.Length];
                int next = ShapeBudget.Plan(pending, pending.Length, r.I("cursor"), r.I("budget"), grant);
                CollectionAssert.AreEqual(r.Is("grants"), grant);
                Assert.AreEqual(r.I("next"), next);
            }
        }

        [TestMethod]
        public void S6Vectors_CornerErrors()
        {
            foreach (VRec r in Kind("cornererr"))
            {
                double[] ax = r.Ds("axes");
                double e = CollisionSkinMath.PrescaleCornerError(r.Q("q"), r.D("s"), r.V("h"), new Vec3(ax[0], ax[1], ax[2]), new Vec3(ax[3], ax[4], ax[5]), new Vec3(ax[6], ax[7], ax[8]));
                Assert.AreEqual(r.D("err"), e, 1e-12);
                if (r.B("level") && r.D("tilt") == 0.0) Assert.IsTrue(e < 1e-9, "level hull, level boxes: exact");
            }
        }

        /// <summary>
 /// S6.M5 model (CONTRACT §2.9, ROADMAP S6.M5; HYPOTHESIS of Unity 2019.4's PhysX shape under a scaled, rotated
 /// hierarchy: rotation = the transform's world rotation W, extents = h·lossyScale, centre = position +
 /// W·(lossyScale ⊙ center), lossyScale = diag(W⁻¹·S·W)): a grid-aligned box as a BoxCollider on the Entity
 /// (center Mirror(b)), a rotated box on a unit-scale child at Mirror(b) with the rigid frame.
 /// </summary>
        private static double M5Error(Vec3 o, Quat q, double s, Vec3 b, Vec3 h, Vec3 e0, Vec3 e1, Vec3 e2, bool rotated)
        {
            Quat w;
            Vec3 pos;
            if (rotated)
            {
                Vec3 m2 = WireMath.Mirror(e2);
                w = q * Quat.FromColumns(WireMath.Mirror(e0), WireMath.Mirror(e1), -m2);
                pos = WireMath.Apply(o, q, s, b);
            }
            else
            {
                w = q;
                Vec3 ls0 = Lossy(w, s), mb = WireMath.Mirror(b);
                pos = o + w.Rotate(new Vec3(ls0.X * mb.X, ls0.Y * mb.Y, ls0.Z * mb.Z));
            }
            Vec3 ls = Lossy(w, s);
            double worst = 0;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        Vec3 corner = b + e0 * (sx * h.X) + e1 * (sy * h.Y) + e2 * (sz * h.Z);
                        Vec3 exact = WireMath.Apply(o, q, s, corner);
                        Vec3 placed = pos + w.Rotate(new Vec3(sx * h.X * ls.X, sy * h.Y * ls.Y, -sz * h.Z * ls.Z));
                        worst = Math.Max(worst, (placed - exact).Length);
                    }
            return worst;
        }

        private static Vec3 Lossy(Quat w, double s)
        {
            Vec3 cx = w.Rotate(Vec3.UnitX), cy = w.Rotate(Vec3.UnitY), cz = w.Rotate(Vec3.UnitZ);
            return new Vec3(Vec3.Dot(cx, WireMath.ScaleH(cx, s)), Vec3.Dot(cy, WireMath.ScaleH(cy, s)), Vec3.Dot(cz, WireMath.ScaleH(cz, s)));
        }

        [TestMethod]
        public void S6Vectors_M5FamilyIsRed()
        {
            foreach (VRec r in Kind("m5case"))
            {
                double[] ax = r.Ds("axes");
                Vec3 e0 = new Vec3(ax[0], ax[1], ax[2]), e1 = new Vec3(ax[3], ax[4], ax[5]), e2 = new Vec3(ax[6], ax[7], ax[8]);
                double m5 = M5Error(r.V("o"), r.Q("q"), r.D("s"), r.V("b"), r.V("h"), e0, e1, e2, r.B("rotated"));
                Assert.AreEqual(r.D("err"), m5, 1e-9, "M5 model, C# and Python");
                Assert.AreEqual(r.D("kerr"), CollisionSkinMath.PrescaleCornerError(r.Q("q"), r.D("s"), r.V("h"), e0, e1, e2), 1e-12);
            }
            foreach (VRec r in Kind("m5"))
            {
                double bound = r.D("bound");
                Console.WriteLine("S6.M5 depth " + r.D("depth") + " m tilt " + r.D("tilt") + " deg: K worst " + r.D("worst").ToString("F4") +
                                  " m, bound " + bound.ToString("F4") + " m, M5 level boxes " + r.D("m5level").ToString("F4") +
                                  " m, M5 rotated boxes " + r.D("m5rot").ToString("F4") + " m");
                Assert.IsTrue(r.D("worst") <= bound, "the K method is inside its own bound");
                Assert.IsTrue(r.D("m5level") > 2 * bound, "S6.M5 (level boxes under the scaled Frame) red at depth " + r.D("depth"));
            }
        }

        [TestMethod]
        public void S6Vectors_Dedup()
        {
            foreach (VRec r in Kind("dedup"))
            {
                var d = new DedupWindow(r.I("window")) { Bypass = r.B("bypass") };
                ulong[] seqs = r.U64s("seqs");
                int[] want = r.Is("results");
                var resets = new HashSet<int>(r.Is("resets"));
                for (int i = 0; i < seqs.Length; i++)
                {
                    if (resets.Contains(i)) d.Reset(d.Generation + 1);
                    Assert.AreEqual((DamageReject)want[i], d.Admit((uint)seqs[i]), "step " + i + " seq " + seqs[i]);
                }
                Assert.AreEqual(r.I("admitted"), d.Admitted);
                Assert.AreEqual(r.I("duplicates"), d.Duplicates);
                Assert.AreEqual(r.I("tooold"), d.TooOld);
                Assert.AreEqual(r.I("reordered"), d.Reordered);
                Assert.AreEqual(r.I("gaps"), d.Gaps);
                Assert.AreEqual(r.U64("hw"), (ulong)d.HighWater);
            }
        }

        [TestMethod]
        public void S6Vectors_DamageChecks()
        {
            foreach (VRec r in Kind("damage"))
            {
                DamageRecord rec = RecordAt(r.X("rec"), 0);
                uint gen = r.U("gen"), epoch = r.U("epoch");
                Assert.AreEqual((DamageReject)r.I("header"), DamageRules.CheckHeader(rec, gen, epoch));
                Assert.AreEqual((DamageReject)r.I("payload"), DamageRules.CheckPayload(rec));
                Assert.AreEqual((DamageReject)r.I("framesub"), DamageRules.CheckFrame(rec, true));
                Assert.AreEqual((DamageReject)r.I("frameroot"), DamageRules.CheckFrame(rec, false));
            }
            VRec v = Kind("validate").Single();
            byte[] all = v.X("recs");
            int[] want = v.Is("results");
            Assert.AreEqual(all.Length / 128, want.Length);
            var dw = new DedupWindow();
            DamageTargetCheck target = (in DamageRecord rec, out bool sub) =>
            {
                ulong e = rec.targetEntityId;
                uint sec = rec.sectionId;
                sub = false;
                if (rec.targetKind == DamageTargetKind.Character)
                    return e != 200 ? DamageReject.Entity : (sec == 0 ? DamageReject.None : DamageReject.Section);
                if (e == 100) return sec == 7 ? DamageReject.None : DamageReject.Section;
                if (e == 101)
                {
                    sub = true;
                    if (rec.rootEntityId != 100) return DamageReject.Entity;
                    return sec == 8 ? DamageReject.None : DamageReject.Section;
                }
                return DamageReject.Entity;
            };
            for (int i = 0; i < want.Length; i++)
                Assert.AreEqual((DamageReject)want[i], DamageRules.Validate(RecordAt(all, 128 * i), v.U("gen"), v.U("epoch"), dw, target), "record " + i);
            Assert.AreEqual(v.I("admitted"), dw.Admitted);
            Assert.AreEqual(v.I("duplicates"), dw.Duplicates);
        }

        [TestMethod]
        public void S6Vectors_FactorsAndCells()
        {
            foreach (VRec r in Kind("factor"))
            {
                double want = r.D("k"), got = DamageRules.Factor(r.U("kind"), r.B("large"));
                Assert.IsTrue(want.Equals(got), "kind " + r.U("kind"));
            }
            foreach (VRec r in Kind("factorclass"))
            {
                double want = r.D("k"), got = DamageRules.FactorForClass(r.U("kind"), r.U("cls"));
                Assert.IsTrue(want.Equals(got), "kind " + r.U("kind") + " class " + r.U("cls"));
            }
            foreach (VRec r in Kind("scale")) Assert.AreEqual(r.B("ok"), DamageRules.ScaleMatches((float)r.D("expected"), r.D("k")));
            foreach (VRec r in Kind("hitcell"))
            {
                int[] c = r.Is("cell");
                I32x3 got = DamageRules.HitCell(r.V("l"), r.D("g"));
                Assert.AreEqual(c[0], got.x); Assert.AreEqual(c[1], got.y); Assert.AreEqual(c[2], got.z);
            }
            int searchedCount = 0, missing = 0;
            foreach (VRec r in Kind("nearest"))
            {
                int[] o = r.Is("occ");
                var occ = new HashSet<Tuple<int, int, int>>();
                for (int i = 0; i < o.Length; i += 3) occ.Add(Tuple.Create(o[i], o[i + 1], o[i + 2]));
                I32x3 cell;
                bool searched;
                bool found = DamageRules.NearestOccupied(r.V("l"), r.D("g"), c => occ.Contains(Tuple.Create(c.x, c.y, c.z)), out cell, out searched);
                Assert.AreEqual(r.B("found"), found);
                if (!found) { missing++; continue; }
                int[] want = r.Is("cell");
                Assert.AreEqual(want[0], cell.x); Assert.AreEqual(want[1], cell.y); Assert.AreEqual(want[2], cell.z);
                Assert.AreEqual(r.B("searched"), searched);
                if (searched) searchedCount++;
            }
            Assert.IsTrue(searchedCount > 3 && missing > 0, "the vectors cover the fallback and NoBlock");
            foreach (VRec r in Kind("hitid")) Assert.AreEqual(r.S("text"), DamageRules.HitId(r.U("gen"), r.U("seq")));
        }
    }
}
