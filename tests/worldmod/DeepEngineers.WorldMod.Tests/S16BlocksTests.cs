using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.WorldMod.Tests
{
    /// <summary>The S16 file and SE's serializer for it (all SE types here, as <see cref="Se"/>).</summary>
    internal static class S16Se
    {
        public static string Path => System.IO.Path.Combine(Rig.ModDir, "Data", "CubeBlocks_S16.sbc");

        /// <summary>
 /// Every element or attribute of the file that SE's pregenerated serializers would ignore: each array item is
 /// deserialized with the serializer of its build pass type (the type an xsi:type or the array's item type names), under
 /// the root name that serializer expects, with events that report unknown names.
 /// </summary>
        public static List<string> UnknownNames(string xml)
        {
            Se.LoadDefinitions("<Definitions xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" />"); // registers the build pass types
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var unknown = new List<string>();
            var map = new Dictionary<string, Type>
            {
                { "Components", typeof(VRage.Game.MyObjectBuilder_ComponentDefinition) },
                { "Blueprints", typeof(VRage.Game.MyObjectBuilder_BlueprintDefinition) },
                { "BlueprintClassEntries", typeof(VRage.Game.BlueprintClassEntry) },
                { "CubeBlocks", typeof(VRage.Game.MyObjectBuilder_CubeBlockDefinition) },
                { "CategoryClasses", typeof(VRage.Game.MyObjectBuilder_GuiBlockCategoryDefinition) },
                { "BlockVariantGroups", typeof(VRage.Game.ObjectBuilders.Definitions.MyObjectBuilder_BlockVariantGroup) },
                { "PhysicalItems", typeof(VRage.Game.MyObjectBuilder_ConsumableItemDefinition) },   // S17 hook: the fish items (every item of Data/PhysicalItems_DE_Food.sbc is a consumable)
            };
            foreach (XmlNode arr in doc.DocumentElement.ChildNodes)
            {
                var ae = arr as XmlElement;
                if (ae == null) continue;
                Type t;
                if (!map.TryGetValue(ae.Name, out t)) { unknown.Add("root element " + ae.Name); continue; }
                XmlSerializer ser;
                // the object builders: SE's registered pregenerated serializer; BlueprintClassEntry is no object build pass
 // (MyObjectBuilder_Definitions reads it through its own array member): the framework one over the same type
                try { ser = VRage.MyXmlSerializerManager.GetSerializer(t); }
                catch (KeyNotFoundException) { ser = new XmlSerializer(t); }
                var xt = t.GetCustomAttribute<XmlTypeAttribute>(false); // XmlSerializer reads the type's own attribute only (a base class's XmlType is not the root name)
                string rootName = xt != null && !string.IsNullOrEmpty(xt.TypeName) ? xt.TypeName : t.Name;
                foreach (XmlNode n in ae.ChildNodes)
                {
                    var el = n as XmlElement;
                    if (el == null) continue;
                    var root = doc.CreateElement(rootName);
                    foreach (XmlAttribute a in el.Attributes)
                        if (a.Name != "xsi:type") root.SetAttribute(a.LocalName, a.NamespaceURI, a.Value);
                    foreach (XmlNode c in el.ChildNodes) root.AppendChild(c.CloneNode(true));
                    var events = new XmlDeserializationEvents
                    {
                        OnUnknownElement = (s, e) => unknown.Add(ae.Name + ": element " + e.Element.Name),
                        OnUnknownAttribute = (s, e) => { if (e.Attr.Name != "xmlns:xsi") unknown.Add(ae.Name + ": attribute " + e.Attr.Name); },
                    };
                    using (var r = new XmlNodeReader(root))
                        Assert.IsNotNull(ser.Deserialize(r, events), ae.Name + " item did not deserialize");
                }
            }
            return unknown;
        }
    }

    /// <summary>
 /// S16 (ROADMAP §14 "### S16" point 2): the pressure hull blocks, their alloys and blueprints load through SE's own
 /// definition serializer (MyObjectBuilderSerializerKeen.DeserializeXML) with the values the Wire table and the oracle read,
 /// and no element or attribute SE would silently ignore.
 /// </summary>
    [TestClass]
    public class S16BlocksTests
    {
        private static string Xml => File.ReadAllText(S16Se.Path, Encoding.UTF8);

        [TestMethod]
        public void Sbc_LoadsThroughSesSerializer_WithTheTiers()
        {
            var defs = Se.LoadDefinitions(Xml);
            Assert.AreEqual(4, defs.CubeBlocks.Length, "four blocks (two tiers since depth 11)");
            var subs = new HashSet<string>();
            foreach (var b in defs.CubeBlocks)
            {
                Assert.AreEqual(typeof(VRage.Game.MyObjectBuilder_CubeBlockDefinition), b.GetType(), "a plain CubeBlock definition");
                Assert.AreEqual("MyObjectBuilder_CubeBlock", b.Id.TypeIdString);
                string sub = b.Id.SubtypeId;
                subs.Add(sub);
                int tier = sub[sub.IndexOf("Mk", StringComparison.Ordinal) + 2] - '0';
                bool large = sub.EndsWith("Large", StringComparison.Ordinal);
                Assert.AreEqual(large ? VRage.Game.MyCubeSize.Large : VRage.Game.MyCubeSize.Small, b.CubeSize, sub);
                Assert.AreEqual(1, b.Size.X); Assert.AreEqual(1, b.Size.Y); Assert.AreEqual(1, b.Size.Z);
                Assert.AreEqual("DE_HullAlloyMk" + tier, b.CriticalComponent.Subtype, sub + " critical component");
                Assert.AreEqual(0, b.CriticalComponent.Index);
                Assert.IsTrue(b.Components.Any(c => c.Subtype == "DE_HullAlloyMk" + tier && c.Count > 0), sub + " alloy");
                Assert.IsFalse(b.Components.Any(c => c.Subtype == "Computer"), sub + ": no Computer on a non-terminal block");

                string[] models = { "VirtualMassBlock.mwm", "decoy.mwm" };
                StringAssert.EndsWith(b.Model, models[tier - 1], sub + " reuses the game's model by path");
                Assert.AreEqual("DE_PressureHullMk" + tier, b.BlockPairName);
            }
            CollectionAssert.AreEquivalent(new[] { "DE_PressureHullMk1Large", "DE_PressureHullMk1Small", "DE_PressureHullMk2Large", "DE_PressureHullMk2Small" }, subs.ToArray());

            Assert.AreEqual(2, defs.Components.Length);
            Assert.AreEqual(2, defs.Blueprints.Length);
            var want = new Dictionary<string, string[]>
            {
                { "DE_HullAlloyMk1", new[] { "Cobalt", "Magnesium", "Silver" } },
                { "DE_HullAlloyMk2", new[] { "Gold", "Platinum", "Uranium" } },
            };
            foreach (var c in defs.Components)
            {
                Assert.AreEqual("MyObjectBuilder_Component", c.Id.TypeIdString);
                Assert.IsTrue(want.ContainsKey(c.Id.SubtypeId), c.Id.SubtypeId);
                Assert.IsTrue(c.MaxIntegrity > 0 && c.Mass > 0, c.Id.SubtypeId);
            }
            foreach (var bp in defs.Blueprints)
            {
                string a = bp.Id.SubtypeId;
                Assert.IsTrue(want.ContainsKey(a), a);
                CollectionAssert.AreEquivalent(want[a], bp.Prerequisites.Select(p => p.Id.SubtypeId).ToArray(), a + " ingots");
                Assert.IsTrue(bp.Prerequisites.All(p => p.Id.TypeIdString == "MyObjectBuilder_Ingot"), a + " ingots");
                Assert.AreEqual(a, bp.Result.Id.SubtypeId);
                Assert.AreEqual("MyObjectBuilder_Component", bp.Result.Id.TypeIdString);
            }
            Assert.AreEqual(2, defs.BlueprintClassEntries.Length);
            Assert.IsTrue(defs.BlueprintClassEntries.All(e => e.Class == "Components" && want.ContainsKey(e.BlueprintSubtypeId) && e.Enabled));
            Assert.AreEqual(1, defs.CategoryClasses.Length);
            Assert.AreEqual("DeepEngineers", defs.CategoryClasses[0].Name);
            Assert.AreEqual(4, defs.CategoryClasses[0].ItemIds.Length);
            Assert.AreEqual(2, defs.BlockVariantGroups.Length);
            Assert.IsTrue(defs.BlockVariantGroups.All(g => g.Blocks.Length == 2));
        }

        [TestMethod]
        public void Sbc_HasNoNameThatSeWouldIgnore()
        {
            var unknown = S16Se.UnknownNames(Xml);
            Assert.AreEqual(0, unknown.Count, string.Join("; ", unknown));
        }

        /// <summary>Seeded failure: a misspelt element in a block loads silently in SE; the strict check reports it.</summary>
        [TestMethod]
        public void Mutant_MisspeltBlockElement_IsReported()
        {
            string bad = Xml.Replace("<IsAirTight>false</IsAirTight>", "<IsAirtight>false</IsAirtight>");
            Assert.AreNotEqual(Xml, bad, "mutation applied");
            Assert.AreEqual(4, Se.LoadDefinitions(bad).CubeBlocks.Length, "SE still loads the blocks");
            var unknown = S16Se.UnknownNames(bad);
            Assert.IsTrue(unknown.Count > 0 && unknown.All(u => u == "CubeBlocks: element IsAirtight"), string.Join("; ", unknown));
        }
    }
}
