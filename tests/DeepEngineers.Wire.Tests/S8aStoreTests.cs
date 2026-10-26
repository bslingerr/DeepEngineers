using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S8a store (CONTRACT §3.3 "The store", §5.4 formats store.key, store.path, store.manifest, overlay.file) against
 /// the Python twin's vectors (tests/s8a/vectors/store.tsv, manifest.txt, overlay.vx2): store keys (lower-cased path
 /// + '|' + site version, UTF-8), file names and relative paths, the sculpt hash, the manifest text byte for byte, the
 /// overlay payload hash and the overlay file decoded (zlib's raw deflate inflated here); plus the manifest and overlay
 /// codecs' round trips and refusals and the store root rule (S8a.N6).
 /// </summary>
    [TestClass]
    public class S8aStoreTests
    {
        /// <summary>s8a_ref.overlay_cells: splitmix64 cells; a repeated key keeps the first draw (its bytes consumed).</summary>
        private static List<S8aOverlayCell> Cells(ulong seed, int count)
        {
            var rng = new S8aSyntheticRaster.SplitMix64(seed);
            var cells = new List<S8aOverlayCell>();
            var keys = new HashSet<ulong>();
            while (cells.Count < count)
            {
                ulong r = rng.Next();
                var c = new S8aOverlayCell { I = (byte)(r & 3), J = (byte)((r >> 2) & 3), K = (byte)((r >> 4) & 1), Cx = (byte)((r >> 8) & 63), Cy = (byte)((r >> 16) & 63), Cz = (byte)((r >> 24) & 63) };
                for (int i = 0; i < S8aOverlayCell.Voxels; i++) c.Content[i] = (byte)(rng.Next() & 0xFF);
                for (int i = 0; i < S8aOverlayCell.Voxels; i++) c.Material[i] = (byte)(rng.Next() & 0xFF);
                if (keys.Add(c.Key)) cells.Add(c);
            }
            return cells;
        }

        [TestMethod]
        public void Vectors_KeysNamesPathsSculptManifestOverlay()
        {
            int keys = 0, files = 0, sculpt = 0, manifest = 0, overlay = 0;
            foreach (string[] f in S8aVectors.Lines("store.tsv"))
            {
                switch (f[0])
                {
                    case "storekey":
                        Assert.AreEqual(S8aVectors.X16(f[3]), S8aStore.StoreKey(f[1], S8aVectors.I(f[2])), "store key of " + f[1]);
                        keys++;
                        break;
                    case "file":
                    {
                        int bx = S8aVectors.I(f[1]), by = S8aVectors.I(f[2]), bz = S8aVectors.I(f[3]);
                        uint v = S8aVectors.U(f[4]);
                        Assert.AreEqual(f[5], S8aStore.BatchFileName(bx, by, bz, v));
                        Assert.AreEqual(f[6], S8aStore.RelativeDrillPath(0x00000000DEADBEEFUL, bx, by, bz, v));
                        Assert.AreEqual(f[7], S8aStore.RelativeWindowPath(1, 0xFEDCBA9876543210UL, bx, by, bz, v));
                        int px, py, pz;
                        uint pv;
                        Assert.IsTrue(S8aStore.TryParseBatchFileName(f[5], out px, out py, out pz, out pv), f[5]);
                        Assert.AreEqual(bx, px); Assert.AreEqual(by, py); Assert.AreEqual(bz, pz); Assert.AreEqual(v, pv);
                        files++;
                        break;
                    }
                    case "sculpt":
                    {
                        var names = new List<string>();
                        var sizes = new List<long>();
                        var fnvs = new List<ulong>();
                        foreach (string e in f[1].Split(';'))
                        {
                            string[] p = e.Split(',');
                            names.Add(p[0]);
                            sizes.Add(long.Parse(p[1]));
                            fnvs.Add(S8aVectors.X16(p[2]));
                        }
                        Assert.AreEqual(12, names.Count);
                        Assert.AreEqual(S8aVectors.X16(f[2]), S8aStore.SculptHash(names, sizes, fnvs));
                        sculpt++;
                        break;
                    }
                    case "manifest":
                    {
                        string text = File.ReadAllText(S8aVectors.PathOf("manifest.txt"), Encoding.UTF8).Replace("\r\n", "\n");
                        byte[] bytes = Encoding.UTF8.GetBytes(text);
                        Assert.AreEqual(S8aVectors.I(f[2]), bytes.Length, "manifest bytes");
                        Assert.AreEqual(S8aVectors.X16(f[1]), LayoutHash.Fnv1a64(bytes), "manifest fnv");
                        S8aManifest m;
                        string err;
                        Assert.IsTrue(S8aManifest.TryParse(text, out m, out err), err);
                        Assert.AreEqual(S8aStore.DrillLayer, m.Layer);
                        Assert.AreEqual(0xABUL, m.Key);
                        Assert.AreEqual(0x5555555555555555UL, m.Hash);
                        Assert.AreEqual(4, m.Count);
                        Assert.AreEqual(text, m.ToText(), "parse -> text is the twin's text byte for byte");
                        S8aManifest.Entry e;
                        Assert.IsTrue(m.TryGet(-3, 2, 40, out e));
                        Assert.AreEqual(9u, e.Version); Assert.AreEqual(9L, e.Bytes); Assert.AreEqual(0x4444444444444444UL, e.Fnv); Assert.AreEqual(16, e.Flags);
                        manifest++;
                        break;
                    }
                    case "overlay":
                    {
                        ulong seed = ulong.Parse(f[1]);
                        int count = S8aVectors.I(f[2]);
                        int siteVersion = S8aVectors.I(f[3]);
                        List<S8aOverlayCell> cells = Cells(seed, count);
                        byte[] payload = S8aOverlayFile.Payload(siteVersion, cells);
                        Assert.AreEqual(S8aVectors.I(f[5]), payload.Length, "payload length");
                        Assert.AreEqual(S8aVectors.X16(f[4]), LayoutHash.Fnv1a64(payload), "payload hash");
                        Assert.AreEqual(LayoutHash.Fnv1a64(payload), S8aOverlayFile.Hash(siteVersion, cells));
                        if (count == 5)
                        {
                            // the twin's file (zlib raw deflate + the hash) decodes to the same cells
                            byte[] file = File.ReadAllBytes(S8aVectors.PathOf("overlay.vx2"));
                            int sv;
                            List<S8aOverlayCell> back;
                            ulong h;
                            string err;
                            Assert.IsTrue(S8aOverlayFile.TryDecode(file, out sv, out back, out h, out err), err);
                            Assert.AreEqual(siteVersion, sv);
                            Assert.AreEqual(S8aVectors.X16(f[4]), h);
                            Assert.AreEqual(count, back.Count);
                            cells.Sort((a, b) => a.Key.CompareTo(b.Key));
                            for (int i = 0; i < count; i++)
                            {
                                Assert.AreEqual(cells[i].Key, back[i].Key, "cell " + i);
                                CollectionAssert.AreEqual(cells[i].Content, back[i].Content, "content " + i);
                                CollectionAssert.AreEqual(cells[i].Material, back[i].Material, "material " + i);
                            }
                            // our own encoding decodes too (another deflate stream, the same payload and hash)
                            ulong h2;
                            byte[] ours = S8aOverlayFile.Encode(siteVersion, cells, out h2);
                            Assert.AreEqual(h, h2);
                            Assert.IsTrue(S8aOverlayFile.TryDecode(ours, out sv, out back, out h, out err), err);
                            Assert.AreEqual(h2, h);
                        }
                        overlay++;
                        break;
                    }
                    default:
                        Assert.Fail("unknown row " + f[0]);
                        break;
                }
            }
            Assert.AreEqual(4, keys);
            Assert.AreEqual(3, files);
            Assert.AreEqual(1, sculpt);
            Assert.AreEqual(1, manifest);
            Assert.AreEqual(2, overlay);
        }

        [TestMethod]
        public void StoreKey_LowerCasesThePath_AndPathsAreUnderTheRoot()
        {
            Assert.AreEqual(S8aStore.Fnv1a64Utf8("c:\\a|1"), S8aStore.StoreKey("C:\\A", 1));
            Assert.AreEqual(S8aStore.StoreKey("c:\\saves\\de lab", 1), S8aStore.StoreKey("C:\\Saves\\DE Lab", 1));
            Assert.AreNotEqual(S8aStore.StoreKey("C:\\A", 1), S8aStore.StoreKey("C:\\A", 2));
            string root = S8aStore.DefaultRoot();
            Assert.IsTrue(root.EndsWith("\\DeepEngineers\\terrain", StringComparison.OrdinalIgnoreCase), root);
            string drill = S8aStore.DrillFolder(root, 0xAB), window = S8aStore.WindowFolder(root, 0xAB, 0xCD);
            Assert.IsTrue(S8aStore.IsInside(drill, root) && S8aStore.IsInside(window, root));
            Assert.AreEqual(Path.Combine(root, "00000000000000ab", "drill"), drill);
            Assert.AreEqual(Path.Combine(root, "00000000000000ab", "window", "00000000000000cd"), window);
            Assert.AreEqual("compiled-batch-12-18-12.optoctrees", S8aStore.PlainBatchFileName(12, 18, 12));
            int bx, by, bz;
            uint v;
            Assert.IsTrue(S8aStore.TryParseBatchFileName("compiled-batch--3-2-40.v9.optoctrees", out bx, out by, out bz, out v));
            Assert.AreEqual(-3, bx); Assert.AreEqual(2, by); Assert.AreEqual(40, bz); Assert.AreEqual(9u, v);
            Assert.IsFalse(S8aStore.TryParseBatchFileName("compiled-batch-12-18-12.optoctrees", out bx, out by, out bz, out v), "the plain name has no version");
            Assert.IsFalse(S8aStore.TryParseBatchFileName("manifest.v1.txt", out bx, out by, out bz, out v));
        }

        [TestMethod]
        public void CheckRoot_RefusesInstallsSnAppDataAndTheWorldFolder()
        {
            string root = S8aStore.DefaultRoot();
            string[] forbidden = { @"C:\Program Files (x86)\Steam\steamapps\common\Subnautica", @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers", @"C:\Users\user\AppData\Roaming\SpaceEngineers\Saves\1\DE Lab" };
            Assert.IsNull(S8aStore.CheckRoot(root, forbidden));
            Assert.IsNotNull(S8aStore.CheckRoot(@"C:\Program Files (x86)\Steam\steamapps\common\Subnautica\SNAppData\DE", forbidden), "inside the install");
            Assert.IsNotNull(S8aStore.CheckRoot(@"C:\Users\user\AppData\Roaming\SpaceEngineers\Saves\1\DE Lab\store", forbidden), "inside the world folder (S8a.M5)");
            Assert.IsNotNull(S8aStore.CheckRoot(@"C:\Program Files (x86)\Steam", forbidden), "contains an install");
            Assert.IsNotNull(S8aStore.CheckRoot("", forbidden));
            Assert.IsNull(S8aStore.CheckRoot(root, null));
        }

        [TestMethod]
        public void Manifest_RoundTripAtomicWrite_AndRefusals()
        {
            var m = new S8aManifest { Layer = S8aStore.WindowLayer, Key = 0xCD, Hash = 0x1234 };
            m.Set(new S8aManifest.Entry { Bx = 30, By = 5, Bz = -3, Version = 2, Bytes = 1234, Fnv = 0xFF, Flags = 1, RelativePath = S8aStore.RelativeWindowPath(0xAB, 0xCD, 30, 5, -3, 2) });
            m.Set(new S8aManifest.Entry { Bx = -35, By = 0, Bz = 60, Version = 1, Bytes = 4, Fnv = 1, Flags = 0, RelativePath = S8aStore.RelativeWindowPath(0xAB, 0xCD, -35, 0, 60, 1) });
            m.Set(new S8aManifest.Entry { Bx = 30, By = 5, Bz = -3, Version = 3, Bytes = 1300, Fnv = 0xEE, Flags = 0, RelativePath = S8aStore.RelativeWindowPath(0xAB, 0xCD, 30, 5, -3, 3) });
            Assert.AreEqual(2, m.Count, "an id's entry is replaced");
            string text = m.ToText();
            Assert.IsTrue(text.StartsWith("de-s8a-store 1 window 00000000000000cd 0000000000001234\n-35 0 60 1 4 0000000000000001 0 ", StringComparison.Ordinal), text);
            S8aManifest back;
            string err;
            Assert.IsTrue(S8aManifest.TryParse(text.Replace("\n", "\r\n"), out back, out err), err);
            Assert.AreEqual(text, back.ToText());
            string dir = Path.Combine(Path.GetTempPath(), "deepeng-s8a-manifest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                m.WriteAtomic(dir);
                m.WriteAtomic(dir);   // replace
                Assert.IsFalse(File.Exists(Path.Combine(dir, S8aStore.ManifestName + ".tmp")));
                S8aManifest read = S8aManifest.ReadFrom(dir, out err);
                Assert.IsNotNull(read, err);
                Assert.AreEqual(text, read.ToText());
                Assert.IsNull(S8aManifest.ReadFrom(Path.Combine(dir, "nope"), out err));
                Assert.IsNotNull(err);
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
            Assert.IsFalse(S8aManifest.TryParse("de-s8a-store 2 drill 00 00\n", out back, out err));
            Assert.IsFalse(S8aManifest.TryParse("de-s8a-store 1 drill 00000000000000ab 0000000000000000\n1 2 3\n", out back, out err));
            Assert.IsFalse(S8aManifest.TryParse("de-s8a-store 1 drill 00000000000000ab 0000000000000000\n1 2 3 4 5 zz 0 p\n", out back, out err));
            try { m.Set(new S8aManifest.Entry { RelativePath = "a b" }); Assert.Fail("a path with a space"); }
            catch (ArgumentException) { }
        }

        [TestMethod]
        public void Overlay_RoundTrip_SortedKeys_AndRefusals()
        {
            List<S8aOverlayCell> cells = Cells(1, 3);
            ulong h;
            byte[] file = S8aOverlayFile.Encode(1, cells, out h);
            int sv;
            List<S8aOverlayCell> back;
            ulong h2;
            string err;
            Assert.IsTrue(S8aOverlayFile.TryDecode(file, out sv, out back, out h2, out err), err);
            Assert.AreEqual(1, sv);
            Assert.AreEqual(h, h2);
            Assert.AreEqual(3, back.Count);
            for (int i = 1; i < back.Count; i++) Assert.IsTrue(back[i - 1].Key < back[i].Key, "sorted by key");
            Assert.AreEqual(S8aOverlayCell.KeyOf(1, 2, 3, 4, 5, 6), new S8aOverlayCell { I = 1, J = 2, K = 3, Cx = 4, Cy = 5, Cz = 6 }.Key);
            Assert.IsTrue(S8aOverlayCell.KeyOf(0, 0, 1, 0, 0, 0) > S8aOverlayCell.KeyOf(0, 0, 0, 63, 63, 63), "the tile comes before the cell in the key");
            // tampered hash
            file[file.Length - 1] ^= 1;
            Assert.IsFalse(S8aOverlayFile.TryDecode(file, out sv, out back, out h2, out err));
            StringAssert.Contains(err, "hash");
            Assert.IsFalse(S8aOverlayFile.TryDecode(new byte[5], out sv, out back, out h2, out err));
            Assert.IsFalse(S8aOverlayFile.TryDecode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, out sv, out back, out h2, out err), "not a deflate stream");
            // duplicate keys refused; the empty overlay encodes
            var dup = new List<S8aOverlayCell> { cells[0], cells[0] };
            try { S8aOverlayFile.Payload(1, dup); Assert.Fail("duplicate key accepted"); }
            catch (ArgumentException) { }
            byte[] empty = S8aOverlayFile.Encode(1, new List<S8aOverlayCell>(), out h);
            Assert.IsTrue(S8aOverlayFile.TryDecode(empty, out sv, out back, out h2, out err), err);
            Assert.AreEqual(0, back.Count);
            Assert.AreEqual(20, S8aOverlayFile.Payload(1, null).Length);
        }
    }
}
