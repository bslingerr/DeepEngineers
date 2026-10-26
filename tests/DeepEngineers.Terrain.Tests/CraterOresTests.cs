using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Terrain.Tests
{
    /// <summary>S8a Stage E: the tiles' material function with the Subnautica map's ores (MaterialBands.Ores).</summary>
    [TestClass]
    public class CraterOresTests
    {
        [TestMethod]
        public void MaterialFunction_OrePocketElseBand()
        {
            var bands = new MaterialBands(new byte[] { 1, 2, 3, 4, 5, 6, 7 });
            double px = 0, py = 0, pz = 0, r;
            int row = -1;
            for (long cx = 0; cx < 400 && row < 0; cx++) row = S8aCraterOres.Pocket(cx, -3, 5, out px, out py, out pz, out r);
            Assert.IsTrue(row >= 0);
            var p = new Vec3(px, py, pz);
            Assert.AreEqual(bands.For(py), bands.For(p), "without the rule: the band");
            bands.Ores = new S8aCraterOreRule(new byte[] { 41, 42, 43, 44, 45, 46, 47, 48, 49 });
            Assert.AreEqual((byte)(41 + row), bands.For(p), "inside a pocket: its ore");
            Assert.AreEqual(bands.For(-20.0), bands.For(new Vec3(px + 4000.0, -20.0, pz)), "outside the map: the band");
        }
    }
}
