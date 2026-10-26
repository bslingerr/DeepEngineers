using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// Planet v2, ores as voxels (ext s8a terrain 6, format convert.orelook): the nine ores take this mod's own block types in the
 /// installed palette's null slots; the canonical ore table the converter's census reads; S8a.M30.
 /// </summary>
    [TestClass]
    public class S8aOreLookTests
    {
        private static readonly int[] Slots = { 95, 96, 107, 108, 117, 118, 119, 121, 122 };

        [TestMethod]
        public void OreRows_TakeThePaletteSlots_InOreOrder()
        {
            var seen = new HashSet<int>();
            for (int i = 0; i < Slots.Length; i++)
            {
                string ore = S8aMaterialMap.Subtypes[S8aMaterialMap.SurfaceMaterials + i];
                Assert.IsTrue(S8aMaterialMap.IsOre(ore), ore);
                Assert.AreEqual(Slots[i], S8aMaterialMap.SnType(ore), ore);
                Assert.IsTrue(seen.Add(Slots[i]), "slot " + Slots[i] + " twice");
            }
            for (int i = 0; i < S8aMaterialMap.SurfaceMaterials; i++)
                Assert.IsFalse(seen.Contains(S8aMaterialMap.SnTypes[i]), "a surface type in an ore slot");
            Assert.IsFalse(seen.Contains(S8aMaterialMap.Fallback));
            string text = S8aConventions.CanonicalText();
            StringAssert.Contains(text, "format convert.orelook Subnautica's palette slots 95, 96, 107, 108, 117, 118, 119, 121 and 122");
            Assert.AreEqual("ext s8a terrain 6", S8aConventions.Header);
        }

        [TestMethod]
        public void BuildTable_SetsTheCanonicalOreTable()
        {
            string[] names = { null, "Sand_02", "Iron_02", "Uraninite_01", "Stone_05", "SomethingElse" };
            byte[] t = S8aMaterialMap.BuildTable(i => i < names.Length ? names[i] : null);
            byte[] oc = S8aMaterialMap.OreCanon;
            Assert.IsNotNull(oc);
            Assert.AreEqual(95, t[2]);
            Assert.AreEqual(95, oc[2]);
            Assert.AreEqual(122, oc[3]);
            Assert.AreEqual(0, oc[1], "sand is not an ore");
            Assert.AreEqual(0, oc[4], "stone is not an ore");
            Assert.AreEqual(0, oc[5]);
            Assert.AreEqual(0, oc[0]);
        }

        [TestMethod]
        public void M30_SetAndCleared()
        {
            try
            {
                Assert.IsTrue(S8aWireMutants.Set("S8a.M30", true));
                Assert.IsTrue(S8aWireMutants.M30OresAsRock);
                StringAssert.Contains(S8aWireMutants.Active, "S8a.M30");
                S8aWireMutants.ClearAll();
                Assert.IsFalse(S8aWireMutants.M30OresAsRock);
            }
            finally { S8aWireMutants.ClearAll(); }
        }
    }
}
