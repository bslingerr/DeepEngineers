using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.WorldMod.Tests
{
    /// <summary>
 /// S14 point 3: the world mod's Water Mod block configs load as the Water Mod 3.23 reads them (XmlSerializer over
 /// <c>WaterConfigAPI</c>, Water Mod, <c>BlockConfig</c> attributes
 /// BaseConfig TypeId/SubtypeId), on.NET Framework (SE's runtime): 20 weapon entries, every one functional under
 /// water, no attribute the serializer would ignore.
 /// </summary>
    [TestClass]
    public class S14WeaponConfigTests
    {
        // the Water Mod's shapes (names only; the mod's own classes are compiled in SE at run time)
        [XmlRoot("WaterConfig")]
        public class WaterConfigApi
        {
            [XmlArrayItem("BlockConfig")] public BlockConfigM[] BlockConfigs;
            [XmlArrayItem("PlanetConfig")] public PlanetConfigM[] PlanetConfigs;
        }

        public class BlockConfigM
        {
            [XmlAttribute("TypeId")] public string TypeId = "(null)";
            [XmlAttribute("SubtypeId")] public string SubtypeId = "(null)";
            [XmlAttribute("Volume")] public float Volume = -1;
            [XmlAttribute("FunctionalUnderwater")] public bool FunctionalUnderWater = true;
            [XmlAttribute("FunctionalAboveWater")] public bool FunctionalAboveWater = true;
            [XmlAttribute("MaximumPressure")] public float MaximumPressure = -1;
            [XmlAttribute("MaxFunctionalPressure")] public float MaxFunctionalPressure = -1;
            [XmlAttribute("PlayDamageEffect")] public bool PlayDamageEffect;
            [XmlAttribute("IsPressurized")] public bool IsPressurized;
        }

        public class PlanetConfigM
        {
            [XmlAttribute("TypeId")] public string TypeId;
            [XmlAttribute("SubtypeId")] public string SubtypeId;
        }

        [TestMethod]
        public void WeaponConfigs_LoadAsTheWaterModReadsThem()
        {
            var ser = new XmlSerializer(typeof(WaterConfigApi));
            var unknown = new List<string>();
            ser.UnknownAttribute += (s, e) => { if (e.ObjectBeingDeserialized is BlockConfigM) unknown.Add(e.Attr.Name); };
            WaterConfigApi cfg;
            using (var r = File.OpenText(Rig.WaterConfigPath)) cfg = (WaterConfigApi)ser.Deserialize(r);
            Assert.IsNotNull(cfg.BlockConfigs, "<BlockConfigs> present");
            Assert.AreEqual(20, cfg.BlockConfigs.Length, "the Water Mod's 20 weapon entries");
            var ids = new HashSet<string>();
            foreach (BlockConfigM b in cfg.BlockConfigs)
            {
                Assert.IsTrue(ids.Add(b.TypeId + "/" + b.SubtypeId), "duplicate " + b.TypeId + "/" + b.SubtypeId + " (the Water Mod keeps the last)");
                Assert.IsTrue(b.FunctionalUnderWater, b.TypeId + "/" + b.SubtypeId + " functional under water");
                Assert.IsTrue(b.FunctionalAboveWater, b.TypeId + "/" + b.SubtypeId + " functional above water");
                Assert.IsTrue(b.Volume > 0, b.TypeId + "/" + b.SubtypeId + " keeps the Water Mod's volume");
                Assert.AreEqual(-1f, b.MaxFunctionalPressure, "no pressure limit added");
            }
            Assert.AreEqual(0, unknown.Count, "attributes the Water Mod ignores: " + string.Join(", ", unknown));
            Assert.AreEqual(1, cfg.PlanetConfigs.Length, "the planet config is still there");
            Assert.AreEqual("DE_4546B", cfg.PlanetConfigs[0].SubtypeId);
        }
    }
}
