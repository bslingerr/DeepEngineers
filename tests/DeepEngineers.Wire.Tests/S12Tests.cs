using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>The S12 vector files (written by tools/s12/s12_ref.py --vectors).</summary>
    internal static class S12Vectors
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static List<string[]> Lines(string name)
        {
            string path = Path.Combine(S5Repo.Root, "tests", "s12", "vectors", name);
            Assert.IsTrue(File.Exists(path), "vector file missing: " + path + " (python -I tools/s12/s12_ref.py --vectors)");
            var rows = new List<string[]>();
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (raw.Length == 0 || raw[0] == '#') continue;
                rows.Add(raw.Split('\t'));
            }
            Assert.IsTrue(rows.Count > 0, name + " has no rows");
            return rows;
        }

        public static double D(string s) { return double.Parse(s, NumberStyles.Float, Inv); }
        public static int I(string s) { return int.Parse(s, Inv); }

        /// <summary>A room spec "minx,miny,minz,maxx,maxy,maxz:key;..." as a membership function (first box wins, as the twin).</summary>
        public static S12CellRoom Room(string spec)
        {
            var boxes = new List<int[]>();
            foreach (string part in spec.Split(';'))
            {
                if (part.Length == 0) continue;
                string[] kv = part.Split(':');
                string[] c = kv[0].Split(',');
                boxes.Add(new[] { I(c[0]), I(c[1]), I(c[2]), I(c[3]), I(c[4]), I(c[5]), I(kv[1]) });
            }
            return (x, y, z) =>
            {
                foreach (int[] b in boxes)
                    if (b[0] <= x && x <= b[3] && b[1] <= y && y <= b[4] && b[2] <= z && z <= b[5]) return b[6];
                return 0;
            };
        }

        /// <summary>A seal spec "x,y,z:dirmask;..." (or "-") as a seal function.</summary>
        public static S12CellSeal Seal(string spec)
        {
            if (spec == "-" || spec.Length == 0) return null;
            var seals = new Dictionary<string, int>();
            foreach (string part in spec.Split(';'))
            {
                string[] kv = part.Split(':');
                seals[kv[0]] = I(kv[1]);
            }
            return (x, y, z, d) =>
            {
                int m;
                return seals.TryGetValue(x + "," + y + "," + z, out m) && ((m >> d) & 1) != 0;
            };
        }
    }

    [TestClass]
    public class S12ConventionsTests
    {
        [TestMethod]
        public void CanonicalText_PinnedHashLinesAndBytes()
        {
            string t = S12Conventions.CanonicalText();
            Assert.AreEqual(S12Conventions.Header + "\n", t.Substring(0, S12Conventions.Header.Length + 1));
            int lines = 0;
            foreach (char c in t) { if (c == '\n') lines++; Assert.IsTrue(c < 128, "ASCII only"); }
            Assert.AreEqual(S12Conventions.CanonicalLines, lines, "lines");
            Assert.AreEqual(S12Conventions.CanonicalBytes, Encoding.ASCII.GetByteCount(t), "bytes");
            Assert.AreEqual(S12Conventions.PinnedHash, S12Conventions.Hash(), "hash 0x" + S12Conventions.Hash().ToString("x16"));
        }

        [TestMethod]
        public void CanonicalText_EqualsTheContractBlock()
        {
            string block = S8aVectors.ContractBlock(S12Conventions.Header);
            if (block == null) Assert.Inconclusive("docs/CONTRACT.md has no S12 block");
            string text = S12Conventions.CanonicalText();
            if (block != text)
            {
                string[] a = block.Split('\n'), b = text.Split('\n');
                for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
                {
                    string x = i < a.Length ? a[i] : "<end>", y = i < b.Length ? b[i] : "<end>";
                    if (x != y) Assert.Fail("line " + (i + 1) + " differs:\n CONTRACT: " + x + "\n Wire:     " + y);
                }
            }
        }

        [TestMethod]
        public void C6Block_PastS15_InsideTheRegion_AndEarlierPinsUnchanged()
        {
            Assert.AreEqual(S12Conventions.C6.HeaderSize + S12Conventions.C6.MaxRows * S12Conventions.C6.RowSize, S12Conventions.C6.Size);
            Assert.IsTrue(S12Conventions.C6.Offset >= S15Conventions.Events.Offset + S15Conventions.Events.Size, "past S15's events block");
            Assert.IsTrue(S12Conventions.C6.Offset >= S15Conventions.Sky.ListOffset + S15Conventions.Sky.ListHeader + S15Conventions.Sky.MaxBodies * S15Conventions.Sky.RowSize, "past S15's body list");
            Assert.IsTrue(S12Conventions.C6.Offset + S12Conventions.C6.Size <= 0x1000, "inside C6's 4 KiB region");
            string[] last = S12Conventions.RowFields[S12Conventions.RowFields.Length - 1];
            Assert.AreEqual(S12Conventions.C6.RowSize, Convert.ToInt32(last[1], 16) + int.Parse(last[2], CultureInfo.InvariantCulture), "the row table fills the row");
            string[] hl = S12Conventions.HeaderFields[S12Conventions.HeaderFields.Length - 1];
            Assert.AreEqual(S12Conventions.C6.HeaderSize, Convert.ToInt32(hl[1], 16) + int.Parse(hl[2], CultureInfo.InvariantCulture), "the header table fills the header");
            Assert.AreEqual(LayoutHash.PinnedLayout10, LayoutHash.Compiled, "layout 1.0 unchanged");
            Assert.AreEqual(S16Conventions.PinnedHash, S16Conventions.Hash(), "S16 pin unchanged");
            Assert.AreEqual(S15Conventions.PinnedHash, S15Conventions.Hash(), "S15 pin unchanged");
            Assert.AreEqual(S7Conventions.PinnedHash, S7Conventions.Hash, "S7 pin unchanged");
        }

        [TestMethod]
        public void Release_DeS12IsDevOnly()
        {
            var args = new Dictionary<string, object> { { "look", "off" } };
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s12", args), "release: de.s12 is dev-only");
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s12", null));
            Assert.IsTrue(UserCommandRules.RequestAllowed(true, "de.s12", args));
            Assert.IsFalse(UserCommandRules.ChatAllowed(false, new[] { "s12", "status" }), "release chat: /de s12 refused");
        }

        [TestMethod]
        public void Preset_AsWritten()
        {
            Assert.AreEqual(0.10, S12AirVolume.Hysteresis(2.5), 1e-12, "0.10 m on a large grid");
            Assert.AreEqual(0.02, S12AirVolume.Hysteresis(0.5), 1e-12, "0.02 m on a small grid");
            Assert.AreEqual(450, S12Conventions.Look.AmbientPermille);
            Assert.AreEqual(0, S12Conventions.Look.CausticsPermille, "caustics off indoors");
            Assert.AreEqual(0, S12Conventions.Look.SunShafts, "no sun rays indoors");
            Assert.AreEqual(0.3, S12LookRules.BounceIntensity(2.0), 1e-12);
            Assert.AreEqual(0.8, S12LookRules.BounceIntensity(50.0), 1e-12);
            Assert.IsTrue(S12LookRules.Applies(true, true, -3) && !S12LookRules.Applies(true, true, 3) && !S12LookRules.Applies(false, true, -3) && !S12LookRules.Applies(true, false, -3));
            Assert.AreEqual(20000, S12Conventions.Flood.DrainMs, "a full room drains in 20 s at the fastest");
            Assert.AreEqual(2000, S12Conventions.Flood.RepairDebounceMs, "a door cycling does not drain a breached room");
            var dt = new S12FloodTrack(true);
            Assert.AreEqual(2000.0, dt.RepairMs);
            Assert.AreEqual(5000.0, dt.QuietMs);
            Assert.AreEqual(0.0, S12Fill.Water(0.9, 0.9), 1e-12, "a room rebuilt at the breathable planet's oxygen is dry");
            Assert.AreEqual(0.5, S12Fill.Water(0.45, 0.9), 1e-12);
            Assert.AreEqual(0.7, S12Fill.Water(0.3, 0.0), 1e-12, "without environment oxygen: 1 - O2");
            Assert.IsFalse(S12AirVolume.CameraInRoom(true, true, false), "third person: the character outside gives the camera no air");
            Assert.IsTrue(S12AirVolume.CameraInRoom(true, false, false), "first person / spectator: the camera's own answer");
            // format air.water: the latch is S2b's swim entry and grounded exit
            Assert.AreEqual(-SwimThresholds.SwimEnterEyeY, S12Conventions.Air.WaterEnterMm / 1000.0, 1e-12);
            Assert.AreEqual(SwimThresholds.SwimExitGroundedEyeY, S12Conventions.Air.WaterExitMm / 1000.0, 1e-12);
            Assert.IsFalse(S12AirVolume.Wet(false, 1.80, 0.25, 7.5), "dry stays dry within 0.1 m under the plane (1.875 m)");
            Assert.IsTrue(S12AirVolume.Wet(false, 1.70, 0.25, 7.5), "0.175 m under: water");
            Assert.IsTrue(S12AirVolume.Wet(true, 1.95, 0.25, 7.5), "wet stays wet until 0.1 m above");
            Assert.IsFalse(S12AirVolume.Wet(true, 2.00, 0.25, 7.5), "0.125 m above: air");
            Assert.IsFalse(S12AirVolume.Wet(true, 0.5, 0.0, 7.5), "no plane: dry");
        }
    }

    /// <summary>S12 rules against the Python twin's vectors (tests/s12/vectors/*.tsv) plus hand checks.</summary>
    [TestClass]
    public class S12RulesTests
    {
        [TestMethod]
        public void Depth_Vectors()
        {
            int n = 0;
            foreach (string[] f in S12Vectors.Lines("depth.tsv"))
            {
                double g = S12Vectors.D(f[0]);
                S12CellRoom room = S12Vectors.Room(f[1]);
                S12CellSeal seal = S12Vectors.Seal(f[2]);
                S12Depth d = S12AirVolume.Depth(S12Vectors.D(f[3]), S12Vectors.D(f[4]), S12Vectors.D(f[5]), g, room, seal);
                string what = string.Join(" ", f);
                Assert.AreEqual(S12Vectors.D(f[6]), d.DepthM, 1e-9, "depth " + what);
                Assert.AreEqual(S12Vectors.I(f[7]), d.Room, "room " + what);
                string[] c = f[8].Split(',');
                Assert.AreEqual(S12Vectors.I(c[0]), d.CellX, "cx " + what);
                Assert.AreEqual(S12Vectors.I(c[1]), d.CellY, "cy " + what);
                Assert.AreEqual(S12Vectors.I(c[2]), d.CellZ, "cz " + what);
                Assert.AreEqual(f[9] == "1", d.CellInRoom, "inRoom " + what);
                n++;
            }
            Assert.IsTrue(n > 1000, "rows " + n);
        }

        [TestMethod]
        public void Fill_Vectors()
        {
            foreach (string[] f in S12Vectors.Lines("fill.tsv"))
                Assert.AreEqual(S12Vectors.D(f[4]), S12Fill.FillMs(S12Vectors.I(f[0]), S12Vectors.D(f[1]), S12Vectors.I(f[2]), S12Vectors.D(f[3])), 1e-6, string.Join(" ", f));
        }

        [TestMethod]
        public void Track_Vectors()
        {
            string scenario = null;
            S12FloodTrack t = null;
            int steps = 0;
            foreach (string[] f in S12Vectors.Lines("track.tsv"))
            {
                if (f[0] != scenario)
                {
                    scenario = f[0];
                    t = new S12FloodTrack(true);
                    t.SetFill(S12Vectors.D(f[1]));
                    t.SetDebounce(S12Vectors.D(f[15]), S12Vectors.D(f[16]));
                }
                var o = new S12RoomObs { Processing = f[3] == "1", Known = f[4] == "1", Airtight = f[5] == "1", Oxygen = S12Vectors.D(f[6]), EnvOxygen = S12Vectors.D(f[7]),
                                         UnderSea = f[8] == "1", VentOut = f[9] == "1" };
                t.Step(S12Vectors.D(f[2]), o);
                string what = string.Join(" ", f);
                Assert.AreEqual(S12Vectors.I(f[10]), t.State, "state " + what);
                Assert.AreEqual(S12Vectors.I(f[11]), t.Source, "source " + what);
                Assert.AreEqual(S12Vectors.D(f[12]), t.Level, 1e-9, "level " + what);
                Assert.AreEqual((uint)S12Vectors.I(f[13]), t.StartSeq, "startSeq " + what);
                Assert.AreEqual(f[14] == "1", t.Ended, "ended " + what);
                steps++;
            }
            Assert.IsTrue(steps > 50, "steps " + steps);
        }

        [TestMethod]
        public unsafe void Wire_Vector_BytesAndRoundTrip()
        {
            List<string[]> l = S12Vectors.Lines("wire.tsv");
            string hex = l[0][0].Trim();
            var h = new S12Header { Version = 1, Flags = 7, WorldEpoch = 3, FloodSeq = 2, VentSeq = 5, Tick = 123456789012UL };
            var rows = new S12Row[2];
            rows[0] = new S12Row { GridId = 142823253881049946L, TrackId = 1, Kind = 1, State = 1, Source = 1, BoxCount = 1, Level = 0.375f, RatePerS = 0.125f, StartSeq = 1, GridSize = 2, UpAxis = 3,
                                   Px = 5f, Py = 2.5f, Pz = -7.5f, Dx = 0f, Dy = 0f, Dz = -1f };
            rows[0].SetBox(0, new CellBox(1, 1, 1, 3, 3, 3));
            rows[1] = new S12Row { GridId = -77, TrackId = 9, Kind = 2, State = 1, Source = 0, BoxCount = 6, Level = 0f, RatePerS = 0f, StartSeq = 4, GridSize = 1, UpAxis = 6, Flags = 1,
                                   Px = 0.25f, Py = -0.5f, Pz = 1f, Dx = 1f, Dy = 0f, Dz = 0f };
            rows[1].SetBox(0, new CellBox(-1, 0, 0, 2, 2, 2));
            rows[1].SetBox(1, new CellBox(3, 0, 0, 3, 1, 4));
            rows[1].SetBox(2, new CellBox(40000, -40000, 0, 1, 1, 1)); // clamped to i16
            rows[1].SetBox(3, new CellBox(0, 0, 0, 0, 0, 0));
            rows[1].SetBox(4, new CellBox(5, 5, 5, 6, 6, 6));
            rows[1].SetBox(5, new CellBox(7, 7, 7, 7, 7, 7));
            byte[] buf = new byte[S12Conventions.C6.Size];
            fixed (byte* p = buf) S12Wire.Encode(p, h, rows, 2);
            var sb = new StringBuilder();
            foreach (byte b in buf) sb.Append(b.ToString("x2"));
            Assert.AreEqual(hex, sb.ToString(), "the C# encoding equals the twin's bytes");
            var h2 = new S12Header();
            var back = new S12Row[S12Conventions.C6.MaxRows];
            int n;
            fixed (byte* p = buf) n = S12Wire.Decode(p, h2, back);
            Assert.AreEqual(2, n);
            Assert.AreEqual(rows[0].GridId, back[0].GridId);
            Assert.AreEqual(32767, back[1].Box(2).MinX);
            Assert.AreEqual(-32768, back[1].Box(2).MinY);
            Assert.AreEqual(123456789012UL, h2.Tick);
            buf[0x04] = 0; // version 0 = absent
            fixed (byte* p = buf) n = S12Wire.Decode(p, h2, back);
            Assert.AreEqual(0, n, "version 0 = absent");
        }

        [TestMethod]
        public void Hold_OneFlipPerCrossing_TheRawCellFlickers()
        {
            S12CellRoom room = S12Vectors.Room("1,1,1,3,3,3:1");
            const double g = 2.5;
            double h = S12AirVolume.Hysteresis(g);
            var hold = new S12AirHold();
            hold.Step(7, true, S12AirVolume.Depth(2 * g, 2 * g, 2 * g, g, room).DepthM, h);
            Assert.IsTrue(hold.Was(7), "inside the room");
            int flips = 0;
            bool was = true;
            for (int i = 0; i < 400; i++)
            {
                double x = 3 * g + 0.5 + i * 0.01 + 0.04 * Math.Sin(i * 1.7);
                bool now = hold.Step(7, true, S12AirVolume.Depth(x, 2 * g, 2 * g, g, room).DepthM, h);
                if (now != was) flips++;
                was = now;
            }
            Assert.AreEqual(1, flips, "walking out with +-4 cm jitter flips once");
            Assert.IsFalse(hold.Step(7, false, 1.0, h), "no room cells known (processing without a snapshot): out");
            int raw = 0;
            bool? prev = null;
            for (int i = 0; i < 400; i++)
            {
                double x = 3 * g + 1.25 + 0.03 * Math.Sin(i * 2.3);
                bool now = room(S12AirVolume.Cell(x, g), 2, 2) > 0;
                if (prev.HasValue && prev.Value != now) raw++;
                prev = now;
            }
            Assert.IsTrue(raw > 10, "the raw cell (the S7 rule, S12.M1) flickers on the same jitter: " + raw);
        }

        [TestMethod]
        public void Vent_OneStartPerEdge()
        {
            var v = new S12VentEdge();
            bool[] starts = { v.Step(false, false), v.Step(true, false), v.Step(true, false), v.Step(false, false), v.Step(true, true) };
            CollectionAssert.AreEqual(new[] { false, true, false, false, true }, starts);
            Assert.AreEqual(2u, v.StartSeq);
            Assert.AreEqual(S12Conventions.VentState.Depressurizing, v.State);
        }

        [TestMethod]
        public void Boxes_RowAndAxes()
        {
            var many = new List<CellBox>();
            for (int i = 0; i < 9; i++) many.Add(new CellBox(i, 0, 0, i, 1, 1));
            List<CellBox> r = S12Boxes.ForRow(many);
            Assert.AreEqual(S12Conventions.C6.MaxBoxes, r.Count, "more than MaxBoxes: the largest, never their bounding box");
            for (int i = 0; i < r.Count; i++) Assert.AreEqual(new CellBox(i, 0, 0, i, 1, 1), r[i], "ties by the lowest corner");
            many.Add(new CellBox(20, 0, 0, 22, 1, 1));
            Assert.AreEqual(new CellBox(20, 0, 0, 22, 1, 1), S12Boxes.ForRow(many)[0], "the largest first");
            // format flood.plane's decomposition: a 3 x 2 x 1 block and an L
            var cells = new List<CellBox>();
            for (int x = 0; x < 3; x++) for (int y = 0; y < 2; y++) cells.Add(CellBox.Cell(x, y, 0));
            cells.Add(CellBox.Cell(0, 0, 1));
            List<CellBox> d = S12Boxes.Decompose(cells);
            Assert.AreEqual(2, d.Count);
            Assert.AreEqual(new CellBox(0, 0, 0, 2, 1, 0), d[0]);
            Assert.AreEqual(new CellBox(0, 0, 1, 0, 0, 1), d[1]);
            long n = 0;
            foreach (CellBox b in d) n += b.CellCount;
            Assert.AreEqual(7L, n, "every cell once");
            double fl, top;
            S12Boxes.FloorTop(new CellBox(1, 1, 1, 3, 3, 3), 2.5, S12Conventions.UpAxis.PosY, out fl, out top);
            Assert.AreEqual(1.25, fl, 1e-12);
            Assert.AreEqual(8.75, top, 1e-12);
            Assert.AreEqual(S12Conventions.UpAxis.NegZ, S12Boxes.UpAxisOf(0.1, 0.2, -0.9));
        }
    }
}
