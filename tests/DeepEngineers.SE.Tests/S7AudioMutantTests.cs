using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S7, SE side, game-independent parts of the astronaut/audio/tools build pass: the A1 muffle decision of
 /// <see cref="AudioPolishRules"/> (CONTRACT §5.4 "S7 conventions" <c>format audio.muffle</c>: SE's
 /// <c>LowPassNoHelmetNoOxy</c> while <c>seMixActive</c> and not in an airtight room, modifiable sounds only; S7.M19
 /// leaves SE's answer), the <c>de.s7audio {effect}</c> grammar, the row cadence, and the SE S7 mutant flags behind
 /// <see cref="Mutants.Set"/> (S7.M8–M10, M12, M14–M17, M19, M20; S7.M9 bones truncated to 64).
 /// </summary>
    [TestClass]
    public class S7AudioMutantTests
    {
        [TestCleanup]
        public void Cleanup()
        {
            S7Mutants.ClearAll();
        }

        [TestMethod]
        public void Muffle_UnderwaterMixOnly_NotInARoom_ModifiableOnly_M19Off()
        {
            Assert.IsTrue(AudioPolishRules.ShouldMuffle(true, true, false, true, false), "under water, out of a room");
            Assert.IsFalse(AudioPolishRules.ShouldMuffle(true, false, false, true, false), "above water: SE's own air sound");
            Assert.IsFalse(AudioPolishRules.ShouldMuffle(true, true, true, true, false), "A3: the dry room under water is not muffled");
            Assert.IsFalse(AudioPolishRules.ShouldMuffle(true, true, false, false, false), "ModifiableByHelmetFilters false");
            Assert.IsFalse(AudioPolishRules.ShouldMuffle(false, true, false, true, false), "de.s7audio {effect: off}");
            Assert.IsFalse(AudioPolishRules.ShouldMuffle(true, true, false, true, true), "S7.M19: the postfix leaves SE's answer");
        }

        [TestMethod]
        public void Muffle_ReviewF2_NeverInAnArcadeSoundWorld()
        {
            Assert.IsTrue(AudioPolishRules.ShouldMuffle(true, true, false, true, false, true), "realistic sound: as before");
            Assert.IsFalse(AudioPolishRules.ShouldMuffle(true, true, false, true, false, false), "Arcade: the postfix forces nothing");
            Assert.AreNotEqual(AudioPolishRules.StateKey(true, true, false, false, "x", true), AudioPolishRules.StateKey(true, true, false, false, "x", false),
                               "a sound-mode change writes a row");
            StringAssert.Contains(AudioPolishRules.ArcadeLine, "Arcade sound mode");
        }

        [TestMethod]
        public void S7M8_ReviewF19_RefusedOutsideAThrowawayCopy()
        {
            Func<string> keep = S7Mutants.M8Guard;
            try
            {
                S7Mutants.M8Guard = () => "not a throwaway copy";
                string line = S7Mutants.Set("S7.M8", true);
                StringAssert.StartsWith(line, "S7.M8 REFUSED");
                Assert.IsFalse(S7Mutants.M8, "a leftover DEEPENGINEERS_MUTANTS never puts hosts into a real save");
                Assert.IsNotNull(S7Mutants.Set("S7.M8", false), "switching it off is never refused");
                S7Mutants.M8Guard = () => null;
                StringAssert.StartsWith(S7Mutants.Set("S7.M8", true), "S7.M8 ON");
                Assert.IsTrue(S7Mutants.M8, "a throwaway copy (or no session yet: checked again in the world)");
            }
            finally { S7Mutants.M8Guard = keep; S7Mutants.ClearAll(); }
        }

        [TestMethod]
        public void RoomFixture_ReviewF1_ShellVentDoorLightsWindowBattery()
        {
            System.Collections.Generic.List<RoomBlock> l = S3Placement.RoomLayout();
            Assert.AreEqual(5 * 5 * 5 - 3 * 3 * 3 + 6, l.Count, "98 shell blocks and 6 inside (vent, door, light, three spots)");
            Func<string, int> n = role => l.FindAll(b => b.Role == role).Count;
            Assert.AreEqual(1, n("window")); Assert.AreEqual(1, n("battery")); Assert.AreEqual(1, n("vent")); Assert.AreEqual(1, n("door"));
            Assert.AreEqual(1, n("light")); Assert.AreEqual(3, n("spot"));
            RoomBlock w = l.Find(b => b.Role == "window");
            CollectionAssert.AreEqual(new[] { 2, 2, 4 }, w.Sn, "the window in the north wall's centre");
            CollectionAssert.AreEqual(new[] { 2, 2, -4 }, w.Cell, "SN north = grid-local -Z (the grid's Forward is the heading)");
            Assert.IsTrue(l.TrueForAll(b => b.DefinitionId.IndexOf('/') > 0), "every block a Type/Subtype id");
            Assert.AreEqual("MyObjectBuilder_AirVent/", l.Find(b => b.Role == "vent").DefinitionId, "the Air Vent (empty subtype): SE pressurizes only with a gas block");
            var cells = new System.Collections.Generic.HashSet<string>();
            foreach (RoomBlock b in l) Assert.IsTrue(cells.Add(b.Cell[0] + "," + b.Cell[1] + "," + b.Cell[2]), "one block per cell");
            Assert.IsFalse(cells.Contains("2,2,-2"), "the interior centre (the launcher's camera) stays free");
            Assert.IsFalse(cells.Contains("2,2,-3"), "the window camera's cell stays free");
            var req = new S3PlaceRequest { Plate = "room" };
            Assert.AreEqual(l.Count, req.Cells().Count);
            S3PlaceRequest parsed;
            string why;
            Assert.IsTrue(S3Args.TryParsePlace(new System.Collections.Generic.Dictionary<string, object>
            {
                { "place", new System.Collections.Generic.Dictionary<string, object> { { "sn", new System.Collections.Generic.List<object> { 60.0, -12.0, 60.0 } },
                    { "plate", new System.Collections.Generic.Dictionary<string, object> { { "orientation", "room" } } } } },
            }, out parsed, out why), why);
            Assert.AreEqual("room", parsed.Plate);
        }

        [TestMethod]
        public void EffectGrammar_SeFourEffects_DefaultOff()
        {
            string e;
            bool off;
            Assert.AreEqual("LowPassNoHelmetNoOxy", AudioPolishRules.DefaultEffect);
            Assert.AreEqual(4, AudioPolishRules.Effects.Length);
            Assert.IsTrue(AudioPolishRules.TryParseEffect("lowpasshelmet", out e, out off));
            Assert.AreEqual("LowPassHelmet", e);
            Assert.IsFalse(off);
            Assert.IsTrue(AudioPolishRules.TryParseEffect("off", out e, out off));
            Assert.IsTrue(off);
            Assert.IsNull(e);
            Assert.IsTrue(AudioPolishRules.TryParseEffect("default", out e, out off));
            Assert.AreEqual(AudioPolishRules.DefaultEffect, e);
            Assert.IsFalse(AudioPolishRules.TryParseEffect("Reverb", out e, out off));
            Assert.IsFalse(AudioPolishRules.TryParseEffect("", out e, out off));
        }

        [TestMethod]
        public void RowCadence_ChangeAtOnce_OncePerSecondUnderwater()
        {
            int a = AudioPolishRules.StateKey(true, true, false, false, "LowPassNoHelmetNoOxy");
            int b = AudioPolishRules.StateKey(true, true, true, false, "LowPassNoHelmetNoOxy");
            int c = AudioPolishRules.StateKey(true, true, false, false, "LowPassHelmet");
            Assert.AreNotEqual(a, b);
            Assert.AreNotEqual(a, c);
            Assert.IsTrue(AudioPolishRules.RowDue(true, false, 10, 0));
            Assert.IsFalse(AudioPolishRules.RowDue(false, false, 10000, 0), "above water: rows on changes only");
            Assert.IsFalse(AudioPolishRules.RowDue(false, true, 999, 0));
            Assert.IsTrue(AudioPolishRules.RowDue(false, true, 1000, 0));
            StringAssert.Contains(AudioPolishRules.Line(true, true, null, true, false, 10, 4, null), "effect=LowPassNoHelmetNoOxy");
            StringAssert.Contains(AudioPolishRules.Line(false, true, "x", false, false, 0, 0, "not found"), "NOT patched (not found)");
        }

        [TestMethod]
        public void S7Mutants_SetThroughTheDispatcher_ActiveAndClear()
        {
            Assert.AreEqual("", S7Mutants.Active);
            foreach (string id in S7Mutants.All)
            {
                string r = Mutants.Set(id, true);
                StringAssert.StartsWith(r, id + " ON", id);
            }
            Assert.IsTrue(S7Mutants.M9 && S7Mutants.M19 && S7Mutants.M8 && S7Mutants.M20);
            Assert.AreEqual(string.Join(",", S7Mutants.All), S7Mutants.Active);
            StringAssert.Contains(Mutants.Active, "S7.M9");
            StringAssert.StartsWith(Mutants.Set("s7.m9", false), "S7.M9 OFF");
            Assert.IsFalse(S7Mutants.M9);
            Mutants.ClearAll();
            Assert.AreEqual("", S7Mutants.Active);
            StringAssert.Contains(Mutants.Set("S7.M4", true), "UNKNOWN", "S7.M4 is Subnautica's");
            StringAssert.Contains(Mutants.Set("S7.M99", true), "S7.M19");
        }
    }
}
