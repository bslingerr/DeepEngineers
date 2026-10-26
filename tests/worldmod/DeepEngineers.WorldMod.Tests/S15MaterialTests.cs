using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.WorldMod.Tests
{
    /// <summary>
 /// S15 part B (CONTRACT §5.4 "S15 conventions", format water.material): the world mod's water material loads through SE's own
 /// definition serializer (MyObjectBuilderSerializerKeen.DeserializeXML) as one TransparentMaterialDefinition named
 /// DE_4546B_Water with our generated texture (in the mod, the path SE resolves against the mod folder), with no element SE
 /// would silently ignore; the Water Mod config names it for DE_4546B.
 /// </summary>
    [TestClass]
    public class S15MaterialTests
    {
        private static string SbcPath => Path.Combine(Rig.ModDir, "Data", "TransparentMaterials.sbc");

        [TestMethod]
        public void Sbc_LoadsThroughSesSerializer()
        {
            var defs = Se.LoadDefinitions(File.ReadAllText(SbcPath, Encoding.UTF8));
            Assert.IsNotNull(defs.TransparentMaterials);
            Assert.AreEqual(2, defs.TransparentMaterials.Length, "the near and the far material");
            VRage.Game.MyObjectBuilder_TransparentMaterialDefinition far = defs.TransparentMaterials[1];
            Assert.AreEqual("DE_4546B_WaterFar", far.Id.SubtypeId);
            Assert.AreEqual("Textures\\DE_4546B_WaterFar.dds", far.Texture);
            Assert.IsTrue(File.Exists(Path.Combine(Rig.ModDir, "Textures", "DE_4546B_WaterFar.dds")), "the far texture ships in the mod");
            Assert.AreEqual(0f, far.Reflectivity);
            VRage.Game.MyObjectBuilder_TransparentMaterialDefinition m = defs.TransparentMaterials[0];
            Assert.AreEqual("MyObjectBuilder_TransparentMaterialDefinition", m.Id.TypeIdString);
            Assert.AreEqual("DE_4546B_Water", m.Id.SubtypeId);
            Assert.AreEqual("Textures\\DE_4546B_Water.dds", m.Texture);
            Assert.IsTrue(File.Exists(Path.Combine(Rig.ModDir, "Textures", "DE_4546B_Water.dds")), "the generated texture ships in the mod");
            Assert.IsFalse(m.UseAtlas);
            Assert.IsFalse(m.CanBeAffectedByOtherLights);
            Assert.IsFalse(m.AlphaMistingEnable);
            Assert.AreEqual(1f, m.AlphaSaturation);
            Assert.AreEqual(5f, m.SoftParticleDistanceScale);
            Assert.AreEqual(0f, m.Reflectivity, "the Water Mod's JWater2 values, only the texture changes");
            Assert.IsTrue(m.IsFlareOccluder);
        }

        [TestMethod]
        public void Sbc_HasNoNameSeIgnores()
        {
            Se.LoadDefinitions("<Definitions xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" />"); // registers the build pass types
            var doc = new XmlDocument();
            doc.LoadXml(File.ReadAllText(SbcPath, Encoding.UTF8));
            Type t = typeof(VRage.Game.MyObjectBuilder_TransparentMaterialDefinition);
            XmlSerializer ser;
            try { ser = VRage.MyXmlSerializerManager.GetSerializer(t); }
            catch (KeyNotFoundException) { ser = new XmlSerializer(t); }
            var xt = t.GetCustomAttribute<XmlTypeAttribute>(false);
            string rootName = xt != null && !string.IsNullOrEmpty(xt.TypeName) ? xt.TypeName : t.Name;
            var unknown = new List<string>();
            int items = 0;
            foreach (XmlNode arr in doc.DocumentElement.ChildNodes)
            {
                var ae = arr as XmlElement;
                if (ae == null) continue;
                if (ae.Name != "TransparentMaterials") { unknown.Add("root element " + ae.Name); continue; }
                foreach (XmlNode n in ae.ChildNodes)
                {
                    var el = n as XmlElement;
                    if (el == null) continue;
                    Assert.AreEqual("TransparentMaterial", el.Name, "MyObjectBuilder_Definitions.TransparentMaterials item name");
                    var root = doc.CreateElement(rootName);
                    foreach (XmlNode c in el.ChildNodes) root.AppendChild(c.CloneNode(true));
                    var events = new XmlDeserializationEvents
                    {
                        OnUnknownElement = (s, e) => unknown.Add("element " + e.Element.Name),
                        OnUnknownAttribute = (s, e) => { if (e.Attr.Name != "xmlns:xsi") unknown.Add("attribute " + e.Attr.Name); },
                    };
                    using (var r = new XmlNodeReader(root))
                        Assert.IsNotNull(ser.Deserialize(r, events));
                    items++;
                }
            }
            Assert.AreEqual(2, items, "the near and the far material");
            Assert.AreEqual(0, unknown.Count, "names SE would ignore: " + string.Join(", ", unknown));
        }

        [TestMethod]
        public void WaterConfig_NamesTheMaterial()
        {
            var doc = new XmlDocument();
            doc.Load(Path.Combine(Rig.ModDir, "Data", "WaterConfig.xml"));
            XmlNode tex = doc.SelectSingleNode("/WaterConfig/PlanetConfigs/PlanetConfig[@SubtypeId='DE_4546B']/WaterSettings/Texture/String");
            Assert.IsNotNull(tex, "WaterSettings/Texture/String (the Water Mod's SerializableStringId)");
            Assert.AreEqual("DE_4546B_Water", tex.InnerText.Trim());
            XmlNode tr = doc.SelectSingleNode("/WaterConfig/PlanetConfigs/PlanetConfig[@SubtypeId='DE_4546B']/WaterSettings/Transparent");
            Assert.IsNotNull(tr, "WaterSettings/Transparent (the owner's opaque sea)");
            Assert.AreEqual("false", tr.InnerText.Trim());
            XmlNodeList list = doc.SelectNodes("/WaterConfig/WaterTextures/Texture");
            bool listed = false;
            foreach (XmlNode n in list) listed |= n.InnerText.Trim() == "DE_4546B_Water";
            Assert.IsTrue(listed, "registered in the Water Mod's texture list (its UI offers it)");
        }
    }
}
