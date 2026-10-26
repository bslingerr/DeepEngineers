using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.WorldMod.Tests
{
    /// <summary>
 /// S17 (docs/S17-BRIEF.md): the fish items and their cooking blueprints load through SE's own definition serializer
 /// (MyObjectBuilderSerializerKeen.DeserializeXML) with the values the Wire table pins, and no element or attribute SE would
 /// silently ignore (the S16 helper's per-item check).
 /// </summary>
    [TestClass]
    public class S17FoodTests
    {
        /// <summary>The kinds of Wire S17Conventions.Kinds (this project does not reference Wire; the twin and S17ConventionsTests pin the table).</summary>
        private static readonly string[] Kinds = { "Peeper", "Bladderfish", "Boomerang", "Hoopfish", "Spadefish", "Reginald", "Eyeye", "HoleFish", "Spinefish", "GarryFish" };

        private static string Items => File.ReadAllText(Path.Combine(Rig.ModDir, "Data", "PhysicalItems_DE_Food.sbc"), Encoding.UTF8);
        private static string Blueprints => File.ReadAllText(Path.Combine(Rig.ModDir, "Data", "Blueprints_DE_Food.sbc"), Encoding.UTF8);

        [TestMethod]
        public void Items_LoadThroughSesSerializer_OneFoodStatEach()
        {
            var defs = Se.LoadDefinitions(Items);
            Assert.AreEqual(2 * Kinds.Length, defs.PhysicalItems.Length, "raw + cooked per kind");
            foreach (var p in defs.PhysicalItems)
            {
                var c = p as VRage.Game.MyObjectBuilder_ConsumableItemDefinition;
                Assert.IsNotNull(c, p.Id.SubtypeId + " is a consumable");
                Assert.AreEqual("MyObjectBuilder_ConsumableItem", c.Id.TypeIdString);
                Assert.AreEqual(1, c.Stats.Length, c.Id.SubtypeId + " one stat");
                Assert.AreEqual("Food", c.Stats[0].Name);
                bool raw = c.Id.SubtypeId.EndsWith("_Raw", StringComparison.Ordinal);
                Assert.AreEqual(raw ? 0.00267 : 0.00534, c.Stats[0].Value, 1e-7, c.Id.SubtypeId);
                Assert.AreEqual(30, c.Stats[0].Time, 1e-6);
            }
            Assert.AreEqual(0, S16Se.UnknownNames(Items).Count, string.Join("; ", S16Se.UnknownNames(Items)));
        }

        [TestMethod]
        public void Blueprints_LoadThroughSesSerializer_InTheFoodProcessorClass()
        {
            var defs = Se.LoadDefinitions(Blueprints);
            Assert.AreEqual(Kinds.Length, defs.Blueprints.Length);
            Assert.AreEqual(defs.Blueprints.Length, defs.BlueprintClassEntries.Length);
            Assert.IsTrue(defs.BlueprintClassEntries.All(e => e.Class == "FoodProcessorRations"));
            foreach (string k in Kinds)
                Assert.IsTrue(defs.Blueprints.Any(b => b.Id.SubtypeId == "DE_CookFish_" + k && b.Prerequisites.Length == 1 && b.Prerequisites[0].SubtypeId == "DE_Fish_" + k + "_Raw" &&
                                                     b.Results.Length == 1 && b.Results[0].SubtypeId == "DE_Fish_" + k + "_Cooked"), k);
            Assert.AreEqual(0, S16Se.UnknownNames(Blueprints).Count, string.Join("; ", S16Se.UnknownNames(Blueprints)));
        }
    }
}
