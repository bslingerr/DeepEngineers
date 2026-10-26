using DeepEngineers.SN;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S12, Subnautica side, the Unity-free part (src/DeepEngineers.SN/S12/S12SnRules.cs): one splash per flood start (a fresh
 /// track's first sight counts, an old one is the baseline, a new epoch rebaselines), the eased level, the vent stream's
 /// rate, the water plane's corners across the up axes, and the mutant names known to DevMutants (S12 hook).
 /// </summary>
    [TestClass]
    public class S12SnTests
    {
        [TestMethod]
        public void Args_ChatWords_ReachTheLookSwitch()
        {
            // ToDict
            var raw = new System.Collections.Generic.Dictionary<string, object> { { "argv", new System.Collections.Generic.List<object> { "look", "off" } } };
            var a = S3Args.ToDict(raw);
            Assert.IsFalse(a.ContainsKey("look"), "S3Args.ToDict alone drops the value");
            S12SnArgs.Words(raw, a);
            Assert.AreEqual("off", a["look"]);
            var b = new System.Collections.Generic.Dictionary<string, object>();
            S12SnArgs.Words(new System.Collections.Generic.Dictionary<string, object> { { "argv", new System.Collections.Generic.List<object> { "water", "Fallback", "look", "on" } } }, b);
            Assert.AreEqual("fallback", b["water"]);
            Assert.AreEqual("on", b["look"]);
        }

        [TestMethod]
        public void StartReader_OnePerStart()
        {
            var r = new S12StartReader();
            Assert.AreEqual(1, r.Step(2, 1, true), "a fresh rising track seen first: its start");
            Assert.AreEqual(0, r.Step(2, 1, false), "unchanged");
            Assert.AreEqual(1, r.Step(2, 2, false), "a re-breach");
            Assert.AreEqual(1, r.Step(2, 4, false), "two at once: one splash");
            Assert.AreEqual(1, r.Coalesced);
            var old = new S12StartReader();
            Assert.AreEqual(0, old.Step(2, 3, false), "a track met half full: the baseline, no splash");
            Assert.AreEqual(0, old.Step(3, 4, false), "a new epoch rebaselines");
            Assert.AreEqual(0, old.Step(3, 2, false), "backwards (a new SE session): no splash");
        }

        [TestMethod]
        public void Ease_TowardTheRow()
        {
            Assert.AreEqual(0.4, S12Ease.Toward(double.NaN, 0.4, 0, 0, 0.016), 1e-12, "first: the row's level");
            double s = S12Ease.Toward(0.0, 1.0, 0.1, 0, 0.1);
            Assert.AreEqual(0.03, s, 1e-12, "at least MinSpeedPerS");
            Assert.AreEqual(0.5, S12Ease.Toward(0.49, 0.5, 0, 0, 0.1), 1e-12, "snaps when within a step");
            Assert.AreEqual(0.55, S12Ease.Toward(0.55, 0.5, 0.1, 0.5, 0.0), 1e-12, "dt 0: unchanged");
            Assert.IsTrue(S12Ease.Toward(0.5, 0.5, 0.2, 0.5, 0.05) > 0.5, "extrapolated by the rate between records");
        }

        [TestMethod]
        public void Stream_RateCarried()
        {
            var st = new S12Stream();
            int total = 0;
            for (int i = 0; i < 60; i++) total += st.Take(S12Conventions.Vent.BubblesPerS, 1.0 / 60.0);
            Assert.IsTrue(total >= S12Conventions.Vent.BubblesPerS - 1 && total <= S12Conventions.Vent.BubblesPerS, "BubblesPerS per second: " + total);
            Assert.AreEqual(0, st.Take(40, 0), "no time, no bubbles");
        }

        [TestMethod]
        public void Plane_CornersAcrossAxes()
        {
            var b = new CellBox(1, 1, 1, 3, 3, 3);
            var c = new double[12];
            Assert.IsTrue(S12PlaneRules.Corners(b, 2.5, S12Conventions.UpAxis.PosY, 5.0, c));
            for (int k = 0; k < 4; k++) Assert.AreEqual(5.0, c[k * 3 + 1], 1e-12, "the plane at y 5");
            Assert.AreEqual(1.25, c[0], 1e-12);
            Assert.AreEqual(8.75, c[3], 1e-12);
            Assert.IsFalse(S12PlaneRules.Corners(b, 2.5, S12Conventions.UpAxis.PosY, 9.0, c), "above the box: no plane");
            Assert.IsFalse(S12PlaneRules.Corners(b, 2.5, S12Conventions.UpAxis.PosY, 1.0, c), "below the box: no plane");
            Assert.IsTrue(S12PlaneRules.Corners(b, 2.5, S12Conventions.UpAxis.NegZ, -5.0, c), "the -Z axis is up: heights are signed");
            for (int k = 0; k < 4; k++) Assert.AreEqual(5.0, c[k * 3 + 2], 1e-12, "z = 5 for the signed height -5 along -Z");
            Assert.AreEqual(1.25 + 0.5 * 7.5, S12PlaneRules.Height(b, 2.5, S12Conventions.UpAxis.PosY, 0.5), 1e-12, "half full");
            Assert.AreEqual(2.5, S12PlaneRules.GridSize(2));
            Assert.AreEqual(0.5, S12PlaneRules.GridSize(1));
        }

        [TestMethod]
        public void Mutants_KnownToDevMutants()
        {
            Assert.IsTrue(DevMutants.IsKnown("S12.M6"), "DevMutants knows S12.M6 (S12 hook)");
            Assert.IsTrue(DevMutants.IsKnown("S12.M7"), "DevMutants knows S12.M7 (S12 hook)");
            Assert.IsFalse(DevMutants.IsKnown("S12.M1"), "S12.M1 is SE's");
        }
    }
}
