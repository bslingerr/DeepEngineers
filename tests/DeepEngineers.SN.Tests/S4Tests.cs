using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S4 Unity-free parts of the Subnautica raycast service (ROADMAP S4 "SN plugin"; the S4 contract result §3 "SN
 /// raycast service"): the de.s4 request grammar and its limits, the camera hold ladder (first rung, ray origin, floor,
 /// contiguous ±16 m boxes), the per-frame hold decision, the vertical ray/triangle test and the render pick of the
 /// native-budget pass, the batch replies (start, status with paging and the C11d size bound, idle, cancel), the
 /// DE.S4 log lines (key=value grammar, invariant culture) and the s4.probe / s4.batch JSONL rows.
 /// </summary>
    [TestClass]
    public class S4Tests
    {
        private static readonly Regex Kv = new Regex(@"^DE\.S4 (PROBE|BATCH \S+ (start|done|cancelled|failed)|DESCEND)(?: [A-Za-z_][A-Za-z0-9_]*=\S+)*(?: \(.*\))?$");

        private static Dictionary<string, object> Args(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private static S4Request Parse(string json)
        {
            S4Request r;
            string error;
            Assert.IsTrue(S4Rules.TryParse(Args(json), out r, out error), json + " -> " + error);
            return r;
        }

        private static string ParseError(string json)
        {
            S4Request r;
            string error;
            Assert.IsFalse(S4Rules.TryParse(Args(json), out r, out error), json + " was accepted");
            Assert.IsNull(r);
            Assert.IsFalse(string.IsNullOrEmpty(error));
            return error;
        }

        // ------------------------------------------------------------------------------------- request grammar

        [TestMethod]
        public void S4_Parse_ProbeBound_WidensToTheS8aWindowUniverse()
        {
            // S8a Stage C fix: while the window is
 // served the probe reaches the window universe, bx, bz in [-35, 60] -> SN x, z in [-7,648, 7,712]
            Assert.AreEqual(-7648.0, S4Rules.WindowMinXZ);
            Assert.AreEqual(7712.0, S4Rules.WindowMaxXZ);
            try
            {
                S4Rules.WindowUniverse = false;
                StringAssert.Contains(ParseError("{\"probe\": [[3000, 0]]}"), "window is not served");
                S4Rules.WindowUniverse = true;
                Assert.AreEqual(4600.0, Parse("{\"probe\": [[4600, -7600]]}").Columns[0].X);
                StringAssert.Contains(ParseError("{\"probe\": [[7713, 0]]}"), "window universe");
                StringAssert.Contains(ParseError("{\"probe\": [[0, -7649]]}"), "window universe");
            }
            finally { S4Rules.WindowUniverse = false; }
        }

        [TestMethod]
        public void S4_Parse_ProbeForms()
        {
            S4Request r = Parse("{\"probe\": [[0, 0], [-1029, -1116, -235.138], {\"x\": 5, \"z\": 6, \"y\": -17}, {\"x\": 1, \"z\": 2}]}");
            Assert.AreEqual(S4Kind.Probe, r.Kind);
            Assert.AreEqual(4, r.Columns.Count);
            Assert.AreEqual(0, r.Columns[0].I);
            Assert.IsFalse(r.Columns[0].HasHint);
            Assert.AreEqual(-1029, r.Columns[1].X);
            Assert.AreEqual(-1116, r.Columns[1].Z);
            Assert.AreEqual(-235.138, r.Columns[1].YHint, 1e-12);
            Assert.AreEqual(3, r.Columns[3].I);
            Assert.AreEqual(-17, r.Columns[2].YHint);
            Assert.IsFalse(r.Columns[3].HasHint);

            S4Request single = Parse("{\"probe\": [12.5, -3]}");
            Assert.AreEqual(1, single.Columns.Count, "a flat [x, z] is one column");
            Assert.AreEqual(12.5, single.Columns[0].X);
            S4Request single3 = Parse("{\"columns\": [12.5, -3, -40]}");
            Assert.AreEqual(1, single3.Columns.Count, "columns is an alias of probe");
            Assert.AreEqual(-40, single3.Columns[0].YHint);

            S4Request text = Parse("{\"probe\": \"0,0; 10,5,-17 ;\"}");
            Assert.AreEqual(2, text.Columns.Count, "\"x,z[,y];...\" (the key=value form)");
            Assert.AreEqual(5, text.Columns[1].Z);
            Assert.AreEqual(-17, text.Columns[1].YHint);

            S4Request nullHint = Parse("{\"probe\": [[1, 2, null]]}");
            Assert.IsFalse(nullHint.Columns[0].HasHint, "a null yHint is no hint");

            S4Request action = Parse("{\"action\": \"probe\", \"probe\": [[0, 0]]}");
            Assert.AreEqual(S4Kind.Probe, action.Kind);
        }

        [TestMethod]
        public void S4_Parse_Defaults_And_Options()
        {
            S4Request r = Parse("{\"probe\": [[0, 0]]}");
            S4Options o = r.Options;
            Assert.IsTrue(o.Hold);
            Assert.IsTrue(o.Mesh);
            Assert.AreEqual(-5.0, o.FromY);
            Assert.AreEqual(8.0, o.HoldAbove);
            Assert.AreEqual(24.0, o.Descend);
            Assert.AreEqual(-1950.0, o.MinY);
            Assert.AreEqual(64.0, o.HintSpan);
            Assert.AreEqual(6000UL, o.TimeoutMs);
            Assert.IsNull(o.Id);

            S4Request q = Parse("{\"probe\": [[0, 0]], \"hold\": false, \"mesh\": false, \"fromY\": 20, \"holdAbove\": 4, \"descend\": 28, \"minY\": -500, " +
                                "\"hintSpan\": 10, \"timeoutMs\": 2500, \"id\": \"at2-1\"}");
            Assert.IsFalse(q.Options.Hold);
            Assert.IsFalse(q.Options.Mesh);
            Assert.AreEqual(20.0, q.Options.FromY);
            Assert.AreEqual(4.0, q.Options.HoldAbove);
            Assert.AreEqual(28.0, q.Options.Descend);
            Assert.AreEqual(-500.0, q.Options.MinY);
            Assert.AreEqual(10.0, q.Options.HintSpan);
            Assert.AreEqual(2500UL, q.Options.TimeoutMs);
            Assert.AreEqual("at2-1", q.Options.Id);
            StringAssert.Contains(q.Options.Describe(), "hold=false mesh=false fromY=20 holdAbove=4 descend=28 minY=-500 hintSpan=10 timeoutMs=2500");
        }

        [TestMethod]
        public void S4_Parse_ChatTokens()
        {
            // {"argv": [...]} (SE chat relay) and key=value tokens go through S3Args.ToDict
            var argv = new Dictionary<string, object>
            {
                { "argv", new List<object> { "probe=0,0;10,5,-17", "hold=true", "timeoutMs=3000", "id=x1" } },
            };
            S4Request r;
            string error;
            Assert.IsTrue(S4Rules.TryParse(argv, out r, out error), error);
            Assert.AreEqual(S4Kind.Probe, r.Kind);
            Assert.AreEqual(2, r.Columns.Count);
            Assert.AreEqual(3000UL, r.Options.TimeoutMs);
            Assert.AreEqual("x1", r.Options.Id);
        }

        [TestMethod]
        public void S4_Parse_Rejects()
        {
            StringAssert.Contains(ParseError("{\"probe\": []}"), "no column");
            StringAssert.Contains(ParseError("{\"probe\": 5}"), "list of columns");
            StringAssert.Contains(ParseError("{\"probe\": [[1]]}"), "probe[0]");
            StringAssert.Contains(ParseError("{\"probe\": [[1, 2, 3, 4]]}"), "probe[0]");
            StringAssert.Contains(ParseError("{\"probe\": [[0, 0], [1, \"a\"]]}"), "probe[1]");
            StringAssert.Contains(ParseError("{\"probe\": [[3000, 0]]}"), "octree world");
            StringAssert.Contains(ParseError("{\"probe\": [[0, -2049]]}"), "octree world");
            StringAssert.Contains(ParseError("{\"probe\": [[0, 0, -3100]]}"), "yHint");
            StringAssert.Contains(ParseError("{\"probe\": [[0, 0]], \"holdabove\": 4}"), "unknown key 'holdabove'");
            StringAssert.Contains(ParseError("{\"probe\": [[0, 0]], \"holdAbove\": 20}"), "holdAbove");
            StringAssert.Contains(ParseError("{\"probe\": [[0, 0]], \"descend\": 40}"), "descend");
            StringAssert.Contains(ParseError("{\"probe\": [[0, 0]], \"descend\": 0}"), "descend");
            StringAssert.Contains(ParseError("{\"probe\": [[0, 0]], \"fromY\": -100, \"minY\": -50}"), "minY");
            StringAssert.Contains(ParseError("{\"probe\": [[0, 0]], \"timeoutMs\": 0}"), "timeoutMs");
            StringAssert.Contains(ParseError("{\"probe\": [[0, 0]], \"hold\": \"maybe\"}"), "hold");
            StringAssert.Contains(ParseError("{\"probe\": [[0, 0]], \"id\": \"a b\"}"), "id");
            StringAssert.Contains(ParseError("{\"probe\": [[0, 0]], \"cancel\": true}"), "one of");
            StringAssert.Contains(ParseError("{\"probe\": [[0, 0]], \"columns\": [[0, 0]]}"), "give one");
            StringAssert.Contains(ParseError("{\"action\": \"probe\"}"), "needs probe");
            StringAssert.Contains(ParseError("{\"action\": \"fly\"}"), "action");
            StringAssert.Contains(ParseError("{\"status\": true, \"max\": 0}"), "max");
            StringAssert.Contains(ParseError("{\"status\": true, \"from\": -1}"), "from");
            StringAssert.Contains(ParseError("{\"status\": true, \"from\": 1.5}"), "from");

            var many = new List<object>();
            for (int i = 0; i <= S4Rules.MaxColumns; i++) many.Add(new List<object> { 0.0, 0.0 });
            S4Request r;
            string error;
            Assert.IsFalse(S4Rules.TryParse(new Dictionary<string, object> { { "probe", many } }, out r, out error));
            StringAssert.Contains(error, "at most " + S4Rules.MaxColumns);
            many.RemoveAt(0);
            Assert.IsTrue(S4Rules.TryParse(new Dictionary<string, object> { { "probe", many } }, out r, out error), "the A-T1-sized 64x64 grid fits: " + error);
        }

        [TestMethod]
        public void S4_Parse_StatusCancelTeardown()
        {
            S4Request empty = Parse("{}");
            Assert.AreEqual(S4Kind.Status, empty.Kind, "no arguments: status");
            Assert.AreEqual(0, empty.From);
            Assert.AreEqual(S4Rules.DefaultStatusMax, empty.Max);
            S4Request st = Parse("{\"status\": true, \"from\": 3, \"max\": 10}");
            Assert.AreEqual(S4Kind.Status, st.Kind);
            Assert.AreEqual(3, st.From);
            Assert.AreEqual(10, st.Max);
            Assert.AreEqual(S4Kind.Cancel, Parse("{\"cancel\": true}").Kind);
            Assert.AreEqual(S4Kind.Teardown, Parse("{\"teardown\": true}").Kind);
            Assert.AreEqual(S4Kind.Teardown, Parse("{\"action\": \"teardown\"}").Kind);
            Assert.AreEqual(S4Kind.Status, Parse("{\"action\": \"status\", \"from\": 2}").Kind);
            Assert.AreEqual(S4Kind.Cancel, Parse("{\"action\": \"cancel\"}").Kind);
            Assert.IsFalse(st.All);
            Assert.IsTrue(Parse("{\"status\": true, \"all\": true}").All);
            StringAssert.Contains(ParseError("{\"status\": true, \"all\": \"x\"}"), "all");
        }

        [TestMethod]
        public void S4_Id_Rule()
        {
            string id, error;
            Assert.IsTrue(S4Rules.TryId("A-T2_run.1", out id, out error));
            Assert.AreEqual("A-T2_run.1", id);
            Assert.IsTrue(S4Rules.TryId(12.0, out id, out error));
            Assert.AreEqual("12", id);
            Assert.IsFalse(S4Rules.TryId("a b", out id, out error));
            Assert.IsFalse(S4Rules.TryId("x=y", out id, out error));
            Assert.IsFalse(S4Rules.TryId(new string('a', 65), out id, out error));
            Assert.IsFalse(S4Rules.TryId("", out id, out error));
        }

        // ------------------------------------------------------------------------------------- hold ladder

        [TestMethod]
        public void S4_Ladder_WithHint()
        {
            var o = new S4Options();
            var c = new S4Column { X = 0, Z = 0, YHint = -17.212 };
            Assert.AreEqual(-9.212, S4Rules.FirstCamY(c, o), 1e-9, "yHint + holdAbove");
            Assert.AreEqual(6.788, S4Rules.OriginY(c, o), 1e-9, "the top of the first box");
            Assert.AreEqual(-81.212, S4Rules.FloorY(c, o), 1e-9, "yHint - hintSpan");
            var rungs = new List<double> { S4Rules.FirstCamY(c, o) };
            double y = rungs[0], next;
            while (S4Rules.TryNextCamY(c, o, y, out next)) { rungs.Add(next); y = next; }
            CollectionAssert.AreEqual(new[] { -9.212, -33.212, -57.212, -81.212 }, Round(rungs));
            Assert.AreEqual(4, S4Rules.RungCount(c, o));
            Assert.IsTrue(-17.212 >= rungs[0] - S4Rules.BoxHalf && -17.212 <= rungs[0] + S4Rules.BoxHalf, "the hinted seabed lies in the first box");

            // the floor never goes below minY
            var deep = new S4Column { X = 0, Z = 0, YHint = -1940 };
            Assert.AreEqual(-1950.0, S4Rules.FloorY(deep, o));
        }

        [TestMethod]
        public void S4_Ladder_Descend_CoversContiguously()
        {
            var o = new S4Options();
            var c = new S4Column { X = -1029, Z = -1116 };
            Assert.AreEqual(-5.0, S4Rules.FirstCamY(c, o));
            Assert.AreEqual(11.0, S4Rules.OriginY(c, o));
            int n = S4Rules.RungCount(c, o);
            Assert.AreEqual(82, n, "(-5 - -1950) / 24 + 1");
            double top = S4Rules.OriginY(c, o), y = S4Rules.FirstCamY(c, o), next;
            double covered = y - S4Rules.BoxHalf;
            Assert.AreEqual(top, y + S4Rules.BoxHalf, 1e-9, "the ray origin is the first box's top");
            while (S4Rules.TryNextCamY(c, o, y, out next))
            {
                Assert.IsTrue(next + S4Rules.BoxHalf >= covered, "successive ±16 m boxes overlap (descend ≤ 32 m)");
                covered = next - S4Rules.BoxHalf;
                y = next;
            }
            Assert.IsTrue(covered <= o.MinY, "the ladder reaches minY: " + covered);
            Assert.IsTrue(y >= o.MinY);

            o.Descend = 2 * S4Rules.BoxHalf;
            y = S4Rules.FirstCamY(c, o);
            Assert.IsTrue(S4Rules.TryNextCamY(c, o, y, out next));
            Assert.AreEqual(y - S4Rules.BoxHalf, next + S4Rules.BoxHalf, 1e-9, "the largest step still joins the boxes");
        }

        [TestMethod]
        public void S4_Ladder_HoldFalse_IsOneRung()
        {
            var o = new S4Options { Hold = false };
            var c = new S4Column { X = 0, Z = 0, YHint = -17 };
            double next;
            Assert.IsFalse(S4Rules.TryNextCamY(c, o, S4Rules.FirstCamY(c, o), out next));
            Assert.AreEqual(1, S4Rules.RungCount(c, o));
        }

        // ------------------------------------------------------------------------------------- hold decision

        [TestMethod]
        public void S4_Decide_Table()
        {
            const double cam = -10; // box [-26, 6]
            const ulong T = 6000;
            // a hit in the box
            Assert.AreEqual(S4Step.Accept, S4Rules.Decide(true, -17, cam, true, true, 2, 10, T, false));
            Assert.AreEqual(S4Step.Wait, S4Rules.Decide(true, -17, cam, true, true, 1, 10, T, false), "built for one frame only");
            Assert.AreEqual(S4Step.Wait, S4Rules.Decide(true, -17, cam, false, false, 0, 10, T, false));
            Assert.AreEqual(S4Step.AcceptUnverified, S4Rules.Decide(true, -17, cam, false, false, 0, T, T, false), "the box never read built");
            Assert.AreEqual(S4Step.Wait, S4Rules.Decide(true, -26.4, cam, false, false, 0, 10, T, false), "within the slack below the box: in it");
            Assert.AreEqual(S4Step.AcceptUnverified, S4Rules.Decide(true, 10, cam, true, false, 5, T, T, false), "above the box (an earlier rung's cell loaded late)");
            Assert.AreEqual(S4Step.Accept, S4Rules.Decide(true, 10, cam, true, true, 2, 10, T, true));
            // a hit below the box: trusted only when the span to it is built
            Assert.AreEqual(S4Step.Accept, S4Rules.Decide(true, -60, cam, true, true, 2, 10, T, false));
            Assert.AreEqual(S4Step.NextRung, S4Rules.Decide(true, -60, cam, true, false, 2, 10, T, false), "a lingering collider: descend to it");
            Assert.AreEqual(S4Step.AcceptUnverified, S4Rules.Decide(true, -60, cam, true, false, 2, 10, T, true), "last rung: kept, unverified");
            Assert.AreEqual(S4Step.Wait, S4Rules.Decide(true, -60, cam, false, false, 0, 10, T, false));
            Assert.AreEqual(S4Step.NextRung, S4Rules.Decide(true, -60, cam, false, false, 0, T, T, false));
            // no hit
            Assert.AreEqual(S4Step.NextRung, S4Rules.Decide(false, double.NaN, cam, true, false, 2, 10, T, false), "the box is built and empty");
            Assert.AreEqual(S4Step.Fail, S4Rules.Decide(false, double.NaN, cam, true, false, 2, 10, T, true));
            Assert.AreEqual(S4Step.Wait, S4Rules.Decide(false, double.NaN, cam, true, false, 1, 10, T, false));
            Assert.AreEqual(S4Step.Wait, S4Rules.Decide(false, double.NaN, cam, false, false, 0, T - 1, T, false));
            Assert.AreEqual(S4Step.NextRung, S4Rules.Decide(false, double.NaN, cam, false, false, 0, T, T, false));
            Assert.AreEqual(S4Step.Fail, S4Rules.Decide(false, double.NaN, cam, false, false, 0, T, T, true));
            Assert.AreEqual(S4Step.Wait, S4Rules.Decide(true, double.NaN, cam, false, false, 0, 10, T, false), "a NaN hit height is no hit");
        }

        [TestMethod]
        public void S4_SpanY_JoinsBoxAndHit()
        {
            double lo, hi;
            S4Rules.SpanY(-17, -10, out lo, out hi);
            Assert.AreEqual(-26.0, lo);
            Assert.AreEqual(6.0, hi);
            S4Rules.SpanY(-60, -10, out lo, out hi);
            Assert.AreEqual(-61.0, lo);
            Assert.AreEqual(6.0, hi);
            S4Rules.SpanY(10, -10, out lo, out hi);
            Assert.AreEqual(-26.0, lo);
            Assert.AreEqual(11.0, hi);
        }

        // ------------------------------------------------------------------------------------- render pass

        [TestMethod]
        public void S4_VerticalHit_Triangle()
        {
            // plane y = 1 + 0.5x + 0.25z through a(0,1,0) b(0,1.25,1) c(1,1.5,0); a -> b -> c is clockwise seen from above
            double y;
            bool up;
            Assert.IsTrue(S4Rules.VerticalHit(0.25, 0.25, 0, 1, 0, 0, 1.25, 1, 1, 1.5, 0, out y, out up));
            Assert.AreEqual(1.1875, y, 1e-12);
            Assert.IsTrue(up, "clockwise from above = front face up (Unity winding)");
            Assert.IsTrue(S4Rules.VerticalHit(0.25, 0.25, 0, 1, 0, 1, 1.5, 0, 0, 1.25, 1, out y, out up));
            Assert.AreEqual(1.1875, y, 1e-12);
            Assert.IsFalse(up, "the reversed winding faces down");
            Assert.IsFalse(S4Rules.VerticalHit(0.8, 0.8, 0, 1, 0, 0, 1.25, 1, 1, 1.5, 0, out y, out up), "outside");
            Assert.IsTrue(S4Rules.VerticalHit(0.5, 0, 0, 1, 0, 0, 1.25, 1, 1, 1.5, 0, out y, out up), "on an edge");
            Assert.AreEqual(1.25, y, 1e-12);
            Assert.IsTrue(S4Rules.VerticalHit(0, 0, 0, 1, 0, 0, 1.25, 1, 1, 1.5, 0, out y, out up), "on a vertex");
            Assert.AreEqual(1.0, y, 1e-12);
            Assert.IsFalse(S4Rules.VerticalHit(0, 0.5, 0, 0, 0, 0, 1, 0, 0, 2, 1, out y, out up), "a wall seen edge-on from above");

            // world-sized coordinates (the grandReef spot) keep their precision
            double x0 = -1029.37, z0 = -1116.81;
            Assert.IsTrue(S4Rules.VerticalHit(x0 + 0.1, z0 + 0.1, x0, -235.0, z0, x0, -235.5, z0 + 1, x0 + 1, -234.0, z0, out y, out up));
            Assert.AreEqual(-235.0 + 0.1 * 1.0 + 0.1 * -0.5, y, 1e-9);
        }

        [TestMethod]
        public void S4_RenderPick_NearestToCollider_And_FirstBelowOrigin()
        {
            var p = new S4RenderPick();
            p.Reset(6.788, -17.212);
            p.Add(-17.25, true);
            p.Add(-17.1, false);
            p.Add(10, true);          // above the ray origin: the physics ray never sees it
            p.Add(-40, true);
            p.Add(double.NaN, true);
            Assert.AreEqual(3, p.Hits);
            Assert.AreEqual(2, p.Up);
            Assert.AreEqual(1, p.Down);
            Assert.AreEqual(-17.1, p.FirstY, 1e-12);
            Assert.AreEqual(-17.25, p.NearestY, 1e-12);
            Assert.IsTrue(p.NearestUp);
            p.Reset(0, -5);
            Assert.AreEqual(0, p.Hits);
            Assert.IsTrue(double.IsNaN(p.NearestY));
            Assert.IsTrue(double.IsNaN(p.FirstY));
            p.AddTriangle(0.25, 0.25, 0, -5, 0, 0, -5, 1, 1, -5, 0);
            Assert.AreEqual(1, p.Hits);
            Assert.AreEqual(-5.0, p.NearestY, 1e-12);
        }

        // ------------------------------------------------------------------------------------- replies and rows

        private static S4Batch Batch(int n, string id = "s4-7")
        {
            var cols = new List<S4Column>();
            for (int i = 0; i < n; i++) cols.Add(new S4Column { I = i, X = i * 2.5, Z = -i, YHint = i == 0 ? -17.212 : double.NaN });
            return new S4Batch(id, new S4Options(), cols, 1000);
        }

        private static void Ok(S4Batch b, int k, double y, double renderY)
        {
            S4ColumnResult r = b.Results[k];
            r.State = "ok";
            r.Hit = true;
            r.Verified = true;
            r.Y = y;
            r.Collider = "ChunkCollider";
            r.Layer = 30;
            r.CamY = y + 8;
            r.OriginY = y + 24;
            r.Rungs = 1;
            r.WaitMs = 812;
            r.RenderY = renderY;
            r.RenderFirstY = renderY;
            r.RenderHits = 2;
            r.RenderVia = "scan";
            r.RenderVisible = true;
            b.Complete(r);
        }

        private static void Miss(S4Batch b, int k, string error)
        {
            S4ColumnResult r = b.Results[k];
            r.State = "failed";
            r.Error = error;
            r.CamY = -1949;
            r.Rungs = 82;
            r.WaitMs = 30000;
            b.Complete(r);
        }

        private static Dictionary<string, object> Obj(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        [TestMethod]
        public void S4_StatusReply_Shape()
        {
            S4Batch b = Batch(3);
            Ok(b, 0, -17.212, -17.25);
            Miss(b, 1, "no TerrainCollider under (2.5, -1)");
            var w = new JsonObjectWriter().Scalar("id", 4.0);
            b.WriteStatus(w, 0, 64, 5000, S4Rules.CurrentJson(b.Columns[2], 1, -33.2, 6.8, 250, 1));
            Dictionary<string, object> o = Obj(w.ToString());
            Assert.AreEqual(true, o["ok"]);
            Assert.AreEqual("de.s4", o["cmd"]);
            Assert.AreEqual("s4-7", o["batch"]);
            Assert.AreEqual("running", o["state"]);
            Assert.AreEqual(2.0, o["done"]);
            Assert.AreEqual(3.0, o["columns"]);
            Assert.AreEqual(0.0, o["from"]);
            Assert.AreEqual(1.0, o["hits"]);
            Assert.AreEqual(1.0, o["failed"]);
            Assert.AreEqual(1.0, o["renderMeasured"]);
            Assert.AreEqual(4000.0, o["elapsedMs"]);
            Assert.AreEqual(2.0, o["next"]);
            Assert.AreEqual(true, o["more"], "still running");
            Assert.AreEqual(false, o["truncated"]);
            var results = (List<object>)o["results"];
            Assert.AreEqual(2, results.Count, "the finished columns only");
            var r0 = (Dictionary<string, object>)results[0];
            foreach (string k in new[] { "i", "x", "z", "hit", "y", "collider", "layer", "camY", "waitMs", "renderY", "renderDelta", "renderNote", "error",
                                         "state", "yHint", "verified", "rungs", "originY", "renderFirstY", "renderHits", "renderVia", "renderVisible" })
                Assert.IsTrue(r0.ContainsKey(k), "result key " + k);
            Assert.AreEqual(0.0, r0["i"]);
            Assert.AreEqual(true, r0["hit"]);
            Assert.AreEqual(-17.212, (double)r0["y"], 1e-12);
            Assert.AreEqual("ChunkCollider", r0["collider"]);
            Assert.AreEqual(30.0, r0["layer"]);
            Assert.AreEqual(-17.25 - -17.212, (double)r0["renderDelta"], 1e-12);
            Assert.AreEqual(-17.212, (double)r0["yHint"], 1e-12);
            Assert.IsNull(r0["error"]);
            Assert.IsNull(r0["renderNote"]);
            Assert.AreEqual("ok", r0["state"]);
            var r1 = (Dictionary<string, object>)results[1];
            Assert.AreEqual(false, r1["hit"]);
            Assert.IsNull(r1["y"], "NaN is null");
            Assert.IsNull(r1["renderY"]);
            Assert.IsNull(r1["renderDelta"]);
            Assert.IsNull(r1["layer"]);
            Assert.IsNull(r1["yHint"]);
            Assert.AreEqual("failed", r1["state"]);
            StringAssert.Contains((string)r1["error"], "no TerrainCollider");
            var cur = (Dictionary<string, object>)o["current"];
            Assert.AreEqual(2.0, cur["i"]);
            Assert.AreEqual("holding", cur["state"]);
            Assert.AreEqual(1.0, cur["rung"]);
        }

        [TestMethod]
        public void S4_StatusReply_Paging_And_SizeBound()
        {
            S4Batch b = Batch(3);
            Ok(b, 0, -17.2, -17.3);
            Ok(b, 1, -18.2, -18.3);
            Ok(b, 2, -19.2, -19.3);
            b.End("done", null, 9000);
            var w = new JsonObjectWriter();
            b.WriteStatus(w, 1, 1, 9999, null);
            Dictionary<string, object> o = Obj(w.ToString());
            Assert.AreEqual("done", o["state"]);
            Assert.AreEqual(8000.0, o["elapsedMs"], "frozen at the end");
            var results = (List<object>)o["results"];
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(1.0, ((Dictionary<string, object>)results[0])["i"]);
            Assert.AreEqual(2.0, o["next"]);
            Assert.AreEqual(true, o["more"], "one more finished column after next");
            Assert.IsNull(o["current"]);
            var w2 = new JsonObjectWriter();
            b.WriteStatus(w2, 7, 64, 9999, null);
            Dictionary<string, object> o2 = Obj(w2.ToString());
            Assert.AreEqual(0, ((List<object>)o2["results"]).Count, "from past the end");
            Assert.AreEqual(false, o2["more"]);

            // the C11d bound: long notes, many columns
            S4Batch big = Batch(400);
            for (int k = 0; k < 400; k++)
            {
                big.Results[k].RenderNote = new string('n', 400);
                Ok(big, k, -20 - k, -20.01 - k);
            }
            var w3 = new JsonObjectWriter();
            big.WriteStatus(w3, 0, S4Rules.MaxStatusMax, 1, null);
            string text = w3.ToString();
            Dictionary<string, object> o3 = Obj(text);
            int returned = (int)(double)o3["returned"];
            Assert.AreEqual(true, o3["truncated"]);
            Assert.IsTrue(returned > 10 && returned < 400, "returned " + returned);
            Assert.AreEqual((double)returned, o3["next"]);
            Assert.IsTrue(text.Length < S4Rules.MaxReplyChars + 2000, "reply " + text.Length + " chars fits C11d (128 KiB)");
        }

        [TestMethod]
        public void S4_StatusReply_All_ListsEveryColumnState()
        {
            S4Batch b = Batch(3);
            Ok(b, 0, -17.2, -17.3);
            b.Results[1].State = "holding";
            var w = new JsonObjectWriter();
            b.WriteStatus(w, 0, 64, 2000, null, true);
            Dictionary<string, object> o = Obj(w.ToString());
            Assert.AreEqual(true, o["all"]);
            Assert.AreEqual(2.0, o["pending"]);
            var results = (List<object>)o["results"];
            Assert.AreEqual(3, results.Count);
            Assert.AreEqual("ok", ((Dictionary<string, object>)results[0])["state"]);
            Assert.AreEqual("holding", ((Dictionary<string, object>)results[1])["state"]);
            Assert.AreEqual("pending", ((Dictionary<string, object>)results[2])["state"]);
            Assert.AreEqual(3.0, o["next"]);
            Assert.AreEqual(false, o["more"]);
            var w2 = new JsonObjectWriter();
            b.WriteStatus(w2, 0, 64, 2000, null);
            Dictionary<string, object> o2 = Obj(w2.ToString());
            Assert.AreEqual(1, ((List<object>)o2["results"]).Count, "by default the finished columns only");
            Assert.AreEqual(false, o2["all"]);
        }

        [TestMethod]
        public void S4_Batch_End_Cancels_The_Rest()
        {
            S4Batch b = Batch(3);
            Ok(b, 0, -17.2, double.NaN);
            b.Results[1].State = "holding";
            b.End("cancelled", "de.s4 cancel", 4000);
            Assert.IsFalse(b.Running);
            Assert.AreEqual(1, b.Done);
            Assert.AreEqual("cancelled", b.Results[1].State);
            Assert.AreEqual("cancelled", b.Results[2].State);
            StringAssert.Contains(b.Results[2].Error, "cancelled");
            Assert.AreEqual("ok", b.Results[0].State, "finished columns keep their result");
            Assert.AreEqual(0, b.RenderMeasured, "a NaN renderY is not a measurement");
            Assert.AreEqual(0, b.Pending, "cancelled columns are not pending");
            string line = b.EndLine();
            StringAssert.StartsWith(line, "DE.S4 BATCH s4-7 cancelled n=3 hits=1 ");
            Assert.IsTrue(Kv.IsMatch(line), line);
        }

        [TestMethod]
        public void S4_StartReply_IdleReply_BatchLines()
        {
            S4Batch b = Batch(5, "at2-1");
            b.OverrideWasActive = true;
            var w = new JsonObjectWriter().Scalar("id", "r1");
            b.WriteStart(w);
            Dictionary<string, object> o = Obj(w.ToString());
            Assert.AreEqual(true, o["ok"]);
            Assert.AreEqual("de.s4", o["cmd"]);
            Assert.AreEqual(true, o["pending"]);
            Assert.AreEqual("at2-1", o["batch"]);
            Assert.AreEqual(5.0, o["columns"]);
            Assert.AreEqual(true, o["overrideWasActive"]);
            Assert.IsTrue(o.ContainsKey("note"));

            var idle = new JsonObjectWriter();
            S4Rules.WriteIdle(idle);
            Dictionary<string, object> i = Obj(idle.ToString());
            Assert.AreEqual("idle", i["state"]);
            Assert.IsNull(i["batch"]);
            Assert.AreEqual(0, ((List<object>)i["results"]).Count);

            string start = b.StartLine();
            StringAssert.StartsWith(start, "DE.S4 BATCH at2-1 start n=5 hold=true mesh=true fromY=-5 holdAbove=8 descend=24 minY=-1950 hintSpan=64 timeoutMs=6000 overrideWasActive=true");
            Assert.IsTrue(Kv.IsMatch(start), start);
            Ok(b, 0, -17.2, -17.3);
            Ok(b, 1, -17.4, -17.5);
            for (int k = 2; k < 5; k++) Miss(b, k, "x");
            b.End("done", null, 6000);
            StringAssert.StartsWith(b.EndLine(), "DE.S4 BATCH at2-1 done n=5 hits=2 verified=2 renderMeasured=2 failed=3 done=5 ms=5000");
            Assert.IsTrue(Kv.IsMatch(b.EndLine()), b.EndLine());
        }

        [TestMethod]
        public void S4_ProbeLine_Grammar_InvariantCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");
                var r = new S4ColumnResult
                {
                    I = 7, X = -1029.5, Z = -1116.25, YHint = -235, State = "ok", Hit = true, Verified = true, Y = -235.138, Collider = "override collider",
                    Layer = 30, CamY = -227, OriginY = -211, Rungs = 2, WaitMs = 1234, RenderY = -235.1, RenderFirstY = -235.1, RenderHits = 3,
                    RenderVia = "parent", RenderVisible = true, RenderNote = "1 unreadable filters, 0 non-triangle submeshes skipped",
                };
                string line = S4Rules.ProbeLine("s4-1", r);
                Assert.IsTrue(Kv.IsMatch(line), line);
                StringAssert.StartsWith(line, "DE.S4 PROBE i=7 x=-1029.5 z=-1116.25 hit=true y=-235.138 collider=override_collider layer=30 camY=-227 waitMs=1234 renderY=-235.1 delta=0.038 ");
                StringAssert.Contains(line, " batch=s4-1 yHint=-235 rungs=2 verified=true originY=-211 renderFirstY=-235.1 renderHits=3 renderVia=parent renderVisible=true state=ok note=1_unreadable_filters,_0_non-triangle_submeshes_skipped error=none");

                var miss = new S4ColumnResult { I = 0, X = 0, Z = 0, State = "failed", Error = "no TerrainCollider under (0, 0)", Rungs = 82, WaitMs = 30000 };
                string ml = S4Rules.ProbeLine("s4-1", miss);
                Assert.IsTrue(Kv.IsMatch(ml), ml);
                StringAssert.Contains(ml, " hit=false y=nan collider=none layer=-1 camY=nan waitMs=30000 renderY=nan delta=nan ");
                StringAssert.Contains(ml, " error=no_TerrainCollider_under_(0,_0)");

                string d = S4Rules.DescendLine("s4-1", new S4Column { I = 3, X = 1.5, Z = 2 }, 0, -5, -29, "box built and empty");
                Assert.AreEqual("DE.S4 DESCEND batch=s4-1 i=3 x=1.5 z=2 rung=0 camY=-5 next=-29 why=box_built_and_empty", d);
                Assert.IsTrue(Kv.IsMatch(d), d);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [TestMethod]
        public void S4_JsonlRows()
        {
            S4Batch b = Batch(2, "wm6");
            Ok(b, 0, -17.212, -17.25);
            string row = S4Rules.ProbeRow("wm6", b.Results[0], "2026-10-16T21:00:00.000Z", 123456, 789);
            Assert.IsFalse(row.Contains("\n"), "one line");
            Dictionary<string, object> o = Obj(row);
            Assert.AreEqual("s4.probe", o["ev"]);
            Assert.AreEqual("s4.probe", o["event"]);
            Assert.AreEqual("info", o["level"]);
            Assert.AreEqual(123456.0, o["tickMs"]);
            Assert.AreEqual(789.0, o["frame"]);
            var data = (Dictionary<string, object>)o["data"];
            Assert.AreEqual("wm6", data["batch"]);
            Assert.AreEqual(0.0, data["i"]);
            Assert.AreEqual(-17.212, (double)data["y"], 1e-12);
            Assert.AreEqual(-0.038, (double)data["renderDelta"], 1e-9);
            Assert.AreEqual(true, data["verified"]);

            string br = b.BatchRow("start", "2026-10-16T21:00:00.000Z", 1, 2);
            Dictionary<string, object> bo = Obj(br);
            Assert.AreEqual("s4.batch", bo["ev"]);
            var bd = (Dictionary<string, object>)bo["data"];
            Assert.AreEqual("wm6", bd["batch"]);
            Assert.AreEqual("start", bd["phase"]);
            Assert.AreEqual("running", bd["state"]);
            Assert.AreEqual(2.0, bd["columns"]);
            Assert.AreEqual(6000.0, bd["timeoutMs"]);
            Assert.IsNull(bd["error"]);
        }

        [TestMethod]
        public void S4_JsonObjectWriter_Raw()
        {
            var w = new JsonObjectWriter().Raw("a", "[1,{\"b\":2}]").Raw("c", null).Raw("d", "");
            Dictionary<string, object> o = Obj(w.ToString());
            Assert.AreEqual(2, ((List<object>)o["a"]).Count);
            Assert.IsNull(o["c"]);
            Assert.IsNull(o["d"]);
        }

        private static double[] Round(List<double> v)
        {
            var r = new double[v.Count];
            for (int i = 0; i < v.Count; i++) r[i] = Math.Round(v[i], 9);
            return r;
        }
    }
}
