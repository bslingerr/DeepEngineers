using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Microsoft.VisualStudio.TestTools.UnitTesting.DoNotParallelize]

namespace DeepEngineers.WorldMod.Tests
{
    /// <summary>Resolves the SE assemblies from the install before any SE type is touched.</summary>
    [TestClass]
    public static class Rig
    {
        public static string SeBin64 { get; private set; }
        public static string ModDir { get; private set; }

        [AssemblyInitialize]
        public static void Init(TestContext _)
        {
            var meta = typeof(Rig).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToList();
            SeBin64 = meta.First(m => m.Key == "DeepEngineers.SEBin64").Value;
            ModDir = Path.GetFullPath(meta.First(m => m.Key == "DeepEngineers.WorldModDir").Value);
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string p = Path.Combine(SeBin64, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(p) ? Assembly.LoadFrom(p) : null;
            };
        }

        public static string SbcPath => Path.Combine(ModDir, "Data", "PlanetGeneratorDefinitions.sbc");
        public static string WaterConfigPath => Path.Combine(ModDir, "Data", "WaterConfig.xml");
    }

    /// <summary>All SE types live here, so the test classes load without the SE assemblies (discovery runs before
 /// the assembly resolver is installed).</summary>
    internal static class Se
    {
        private static bool s_registered;

        private static void Register()
        {
            if (s_registered) return;
            // what MyGlobalTypeMetadata.Init does for the object-build pass assembly
            VRage.MyXmlSerializerManager.RegisterSerializableBaseType(typeof(VRage.ObjectBuilders.MyObjectBuilder_Base));
            VRage.Game.ObjectBuilder.MyGlobalTypeMetadata.Static.RegisterAssembly(
                typeof(VRage.Game.MyObjectBuilder_PlanetGeneratorDefinition).Assembly);
            s_registered = true;
        }

        /// <summary>SE's definition-file path: MyObjectBuilderSerializerKeen.DeserializeXML into MyObjectBuilder_Definitions.</summary>
        public static VRage.Game.MyObjectBuilder_Definitions LoadDefinitions(string xml)
        {
            Register();
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml)))
            {
                bool ok = VRage.ObjectBuilders.Private.MyObjectBuilderSerializerKeen.DeserializeXML(
                    ms, out VRage.Game.MyObjectBuilder_Definitions defs);
                Assert.IsTrue(ok, "SE's DeserializeXML refused the file");
                return defs;
            }
        }

        /// <summary>The pregenerated serializer of MyObjectBuilder_PlanetGeneratorDefinition on the &lt;Definition&gt;
 /// element, with events that report every element or attribute SE would ignore.</summary>
        public static List<string> UnknownNames(string xml)
        {
            Register();
            var ser = VRage.MyXmlSerializerManager.GetSerializer(typeof(VRage.Game.MyObjectBuilder_PlanetGeneratorDefinition));
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var defs = doc.DocumentElement.GetElementsByTagName("Definition");
            Assert.AreEqual(1, defs.Count, "one <Definition>");
            var def = (XmlElement)defs[0];
            var root = doc.CreateElement("PlanetGeneratorDefinition");   // the XmlType name the serializer expects
            foreach (XmlAttribute a in def.Attributes)
                if (a.Name != "xsi:type") root.SetAttribute(a.LocalName, a.NamespaceURI, a.Value);
            while (def.HasChildNodes) root.AppendChild(def.FirstChild);
            var unknown = new List<string>();
            var events = new XmlDeserializationEvents
            {
                OnUnknownElement = (s, e) => unknown.Add("element " + e.Element.Name),
                OnUnknownAttribute = (s, e) => unknown.Add("attribute " + e.Attr.Name),
            };
            using (var r = new XmlNodeReader(root))
                Assert.IsNotNull(ser.Deserialize(r, events));
            return unknown;
        }

        public static int BestOctreeSize(float size) => VRage.Voxels.MyVoxelCoordSystems.FindBestOctreeSize(size).X;
    }

    [TestClass]
    public class PlanetDefinitionTests
    {
        private static string Sbc => File.ReadAllText(Rig.SbcPath);

        [TestMethod]
        public void Sbc_LoadsThroughSesDefinitionSerializer_WithTheSiteValues()
        {
            var defs = Se.LoadDefinitions(Sbc);
            Assert.AreEqual(1, defs.Definitions.Length, "one definition");
            Assert.IsNull(defs.PlanetGeneratorDefinitions, "no legacy <PlanetGeneratorDefinitions> block");
            var d = defs.Definitions[0] as VRage.Game.MyObjectBuilder_PlanetGeneratorDefinition;
            Assert.IsNotNull(d, "xsi:type PlanetGeneratorDefinition resolves to MyObjectBuilder_PlanetGeneratorDefinition");
            Assert.AreEqual("DE_4546B", d.Id.SubtypeId);
            Assert.AreEqual("MyObjectBuilder_PlanetGeneratorDefinition", d.Id.TypeIdString);
            Assert.IsTrue(d.Public && d.Enabled);

            // CONTRACT section 3 / site_v1.PLANET (float32 like SE)
            Assert.AreEqual(-0.05f, d.HillParams.Value.Min);
            Assert.AreEqual(0.12f, d.HillParams.Value.Max);
            Assert.AreEqual(1.0f, d.SurfaceGravity.Value);
            Assert.AreEqual(7f, d.GravityFalloffPower.Value);
            Assert.AreEqual(true, d.HasAtmosphere.Value);
            Assert.IsTrue(d.Atmosphere.Breathable);
            Assert.AreEqual(0.9f, d.Atmosphere.OxygenDensity);
            Assert.AreEqual(1.0f, d.Atmosphere.Density);
            Assert.AreEqual(2.0f, d.Atmosphere.LimitAltitude);
            Assert.AreEqual(80f, d.Atmosphere.MaxWindSpeed);

            // ROADMAP S1: no SurfaceDetail, EnvironmentItems or weather generators; S8a Stage C: the designed planet's
 // ore depth-rarity table (tools/planet/layout_4546b.json -> OreMappings, the _mat B channel), as SE's serializer reads it
            Assert.IsNull(d.SurfaceDetail);
            Assert.IsNull(d.EnvironmentItems);
            // py --verify checks it against the
 // layout): nine ores, each with its row and, for the deeper ones, an outcrop row (Start 0); the repository keeps the
 // previous nine-row table until the orchestrator rewrites the files, so the rules here hold for both
            var types = new HashSet<string> { "Iron_02", "Nickel_01", "Silicon_01", "Cobalt_01", "Magnesium_01", "Silver_01", "Gold_01", "Platinum_01", "Uraninite_01" };
            Assert.IsNotNull(d.OreMappings);
            Assert.IsTrue(d.OreMappings.Length >= types.Count && d.OreMappings.Length <= 2 * types.Count, "9..18 ore rows, got " + d.OreMappings.Length);
            var seenValues = new HashSet<byte>();
            var seenTypes = new HashSet<string>();
            for (int i = 0; i < d.OreMappings.Length; i++)
            {
                var o = d.OreMappings[i];
                Assert.IsTrue(o.Value > 0 && seenValues.Add(o.Value), "ore row " + i + ": a distinct non-zero B value (" + o.Value + ")");
                Assert.IsTrue(types.Contains(o.Type), "ore row " + i + ": a vanilla ore voxel (" + o.Type + ")");
                seenTypes.Add(o.Type);
                Assert.IsTrue(o.Start >= 0f && o.Depth > 0f, "ore row " + i + ": Start >= 0, Depth > 0");
                Assert.IsTrue(o.ColorShift.HasValue, "TargetColor parsed, ore row " + i);
            }
            Assert.AreEqual(types.Count, seenTypes.Count, "every ore of the table has a row");
            Assert.IsTrue(d.WeatherGenerators == null || d.WeatherGenerators.Count == 0);
            Assert.IsNull(d.MapProvider, "default texture map provider, Path = FolderName");
            Assert.IsNull(d.FolderName, "FolderName defaults to the SubtypeId: Data/PlanetDataFiles/DE_4546B");
            Assert.IsNull(d.InheritFrom);
            Assert.IsNotNull(d.CloudLayers, "ProcessPlanetGeneratorDefition iterates it without a null check");

            Assert.AreEqual(4, d.CloudLayers.Count, "EarthLike's cloud layers (S7t T5)");
            Assert.IsNotNull(d.AtmosphereSettings, "the S7t atmosphere (EarthLike's block, retuned)");

            // materials (S8a Stage C, CONTRACT 3.3 "Sculpt"): R = 1..7 in the _mat files -> Sand_02, Stone_01..Stone_05, Grass (the
 // island), MaxDepth 5 each; G the biome map, B the ore map (PlanetMaps Ores and Biome on); default surface and subsurface Stone
            Assert.IsTrue(d.PlanetMaps.Value.Material && d.PlanetMaps.Value.Ores && d.PlanetMaps.Value.Biome && !d.PlanetMaps.Value.Occlusion);
            var table = new[] { "Sand_02", "Stone_01", "Stone_02", "Stone_03", "Stone_04", "Stone_05", "Grass" };
            Assert.AreEqual(table.Length, d.CustomMaterialTable.Length);
            for (int i = 0; i < table.Length; i++)
            {
                Assert.AreEqual(table[i], d.CustomMaterialTable[i].Material, "row " + i);
                Assert.AreEqual((byte)(i + 1), d.CustomMaterialTable[i].Value, "row " + i);
                Assert.AreEqual(5f, d.CustomMaterialTable[i].MaxDepth, "row " + i);
            }
            Assert.AreEqual("Stone", d.DefaultSurfaceMaterial.Material);
            Assert.AreEqual("Stone", d.DefaultSubSurfaceMaterial.Material);
        }

        [TestMethod]
        public void Sbc_HasNoNameThatSeWouldIgnore()
        {
            var unknown = Se.UnknownNames(Sbc);
            Assert.AreEqual(0, unknown.Count, string.Join("; ", unknown));
        }

        /// <summary>Seeded failure: a misspelt element loads without error in SE and silently keeps the default;
 /// the strict check reports it.</summary>
        [TestMethod]
        public void Mutant_MisspeltElement_IsSilentInSe_ButReportedByTheStrictCheck()
        {
            string bad = Sbc.Replace("<GravityFalloffPower>7</GravityFalloffPower>", "<GravityFallOffPower>3</GravityFallOffPower>")
                            .Replace("Min=\"-0.05\"", "Min=\"-0.05\" Mn=\"1\"");
            Assert.AreNotEqual(Sbc, bad, "mutation applied");
            var d = (VRage.Game.MyObjectBuilder_PlanetGeneratorDefinition)Se.LoadDefinitions(bad).Definitions[0];
            Assert.IsFalse(d.GravityFalloffPower.HasValue, "SE ignores the misspelt element (the default 7 applies)");
            var unknown = Se.UnknownNames(bad);
            CollectionAssert.AreEquivalent(new[] { "element GravityFallOffPower", "attribute Mn" }, unknown);
        }

        [TestMethod]
        public void Derived_StorageSizeAndRadii_AsSeComputesThem()
        {
            float R = 120000f / 2f;                        // spawn size -> MyPlanetStorageProvider radius
            float maxHill = 0.12f * R, minHill = -0.05f * R;
            Assert.AreEqual(57000f, R + minHill, "MinimumRadius");
            Assert.AreEqual(67200f, R + maxHill, "MaximumRadius");
            Assert.AreEqual(262144, Se.BestOctreeSize(2f * (R + maxHill)), "MyVoxelCoordSystems.FindBestOctreeSize");
            Assert.AreEqual(14400f, maxHill * 2.0f, "AtmosphereAltitude = MaxHillHeight x LimitAltitude");
        }
    }

    [TestClass]
    public class WaterConfigTests
    {
        /// <summary>The Water Mod reads WaterConfig.xml with XmlSerializer on.NET Framework (SE's runtime): floats go
 /// through XmlConvert.ToSingle..NET Framework's decimal parsing is not always correctly rounded (S0 D14), so
 /// the contract bits are checked on this runtime.</summary>
        [TestMethod]
        public void Radius_ParsesToTheContractFloat_OnNetFramework()
        {
            var doc = new XmlDocument();
            doc.Load(Rig.WaterConfigPath);
            var radius = doc.SelectSingleNode("/WaterConfig/PlanetConfigs/PlanetConfig[@SubtypeId='DE_4546B']/WaterSettings/Radius");
            Assert.IsNotNull(radius);
            float r = XmlConvert.ToSingle(radius.InnerText);
            Assert.AreEqual(0x3F86BCA2u, BitConverter.ToUInt32(BitConverter.GetBytes(r), 0), "float32 bits");
            Assert.AreEqual(r, float.Parse(radius.InnerText, CultureInfo.InvariantCulture));
            float rs = 57000f * r;                         // WaterComponent: MinimumRadius * Settings.Radius (floats)
            Assert.AreEqual(60000.00390625f, rs, "Rs");
            Assert.AreEqual(60000.00390625, (double)rs, "Rs exactly");
        }

        [TestMethod]
        public void ContractValues_AreAllExplicit()
        {
            var doc = new XmlDocument();
            doc.Load(Rig.WaterConfigPath);
            var configs = doc.SelectNodes("/WaterConfig/PlanetConfigs/PlanetConfig");
            Assert.AreEqual(1, configs.Count);
            var pc = (XmlElement)configs[0];
            Assert.AreEqual("MyObjectBuilder_PlanetGeneratorDefinition", pc.GetAttribute("TypeId"));
            Assert.AreEqual("DE_4546B", pc.GetAttribute("SubtypeId"));
            var ws = pc.SelectSingleNode("WaterSettings");
            Assert.IsNotNull(ws);
            Assert.AreEqual(0f, XmlConvert.ToSingle(ws.SelectSingleNode("WaveHeight").InnerText));
            Assert.AreEqual(0f, XmlConvert.ToSingle(ws.SelectSingleNode("TideHeight").InnerText));
            Assert.AreEqual(0f, XmlConvert.ToSingle(ws.SelectSingleNode("CrushDamage").InnerText));
            Assert.AreEqual(0f, XmlConvert.ToSingle(ws.SelectSingleNode("CurrentSpeed").InnerText));
            Assert.IsFalse(XmlConvert.ToBoolean(ws.SelectSingleNode("EnableFish").InnerText));
            Assert.IsFalse(XmlConvert.ToBoolean(ws.SelectSingleNode("Volumetric").InnerText));
        }
    }
}
