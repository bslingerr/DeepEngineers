using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S7p-character (ROADMAP S7p P9), SE side, game-independent parts of the character export
 /// (<see cref="S7pCharacterExportRules"/>): the fallback of the player's own gender (Characters.sbc: Default_Astronaut
 /// Male, Default_Astronaut_Female Female), the held tool's skin decision (send, wait for the variant, keep, remove) and

 /// </summary>
    [TestClass]
    public class S7pCharacterExportTests
    {
        [TestMethod]
        public void FallbackFollowsTheGender()
        {
            Assert.AreEqual("Default_Astronaut_Female", S7pCharacterExportRules.FallbackSubtype("Female"));
            Assert.AreEqual("Default_Astronaut_Female", S7pCharacterExportRules.FallbackSubtype(" female "));
            Assert.AreEqual("Default_Astronaut", S7pCharacterExportRules.FallbackSubtype("Male"));
            Assert.AreEqual("Default_Astronaut", S7pCharacterExportRules.FallbackSubtype(null), "unknown gender: the male Engineer (S5's default)");
            Assert.AreEqual("Default_Astronaut", S7pCharacterExportRules.FallbackSubtype("Robot"));
            Assert.AreEqual(S5Defaults.DefaultCharacter, S7pCharacterExportRules.DefaultMale, "the S5 default stays the male Engineer");
        }

        [TestMethod]
        public void ToolSkin_FirstNonEmpty()
        {
            Assert.IsNull(S7pCharacterExportRules.ToolSkin(null));
            Assert.IsNull(S7pCharacterExportRules.ToolSkin(new List<string>()));
            Assert.AreEqual("Rainbow_HandDrill", S7pCharacterExportRules.ToolSkin(new List<string> { "", null, "Rainbow_HandDrill", "Veteran_Welder" }));
        }

        [TestMethod]
        public void ToolStep_SendWaitKeepRemove()
        {
            const long A = 101, B = 202;
            var Keep = S7pCharacterExportRules.ToolStep.Keep;
            var Send = S7pCharacterExportRules.ToolStep.Send;
            var Wait = S7pCharacterExportRules.ToolStep.Wait;
            var Remove = S7pCharacterExportRules.ToolStep.Remove;
            Assert.AreEqual(Keep, S7pCharacterExportRules.DecideTool(0, 0, false, null, null, true, 0), "no tool, nothing sent");
            Assert.AreEqual(Remove, S7pCharacterExportRules.DecideTool(0, A, true, null, null, true, 0), "tool put away");
            Assert.AreEqual(Send, S7pCharacterExportRules.DecideTool(A, 0, false, null, null, true, 0), "a plain tool goes at once");
            Assert.AreEqual(Keep, S7pCharacterExportRules.DecideTool(A, A, true, null, null, true, 0));
            Assert.AreEqual(Wait, S7pCharacterExportRules.DecideTool(A, 0, false, "Rainbow_HandDrill", null, false, 100), "skin variant not decided yet");
            Assert.AreEqual(Send, S7pCharacterExportRules.DecideTool(A, 0, false, "Rainbow_HandDrill", null, false, S7pCharacterExportRules.ToolSkinWaitMs), "the wait ran out: plain");
            Assert.AreEqual(Send, S7pCharacterExportRules.DecideTool(A, 0, false, "Rainbow_HandDrill", null, true, 0), "decided: the variant");
            Assert.AreEqual(Send, S7pCharacterExportRules.DecideTool(A, A, true, "Rainbow_HandDrill", null, true, 0), "the skin arrived after the equip: re-send");
            Assert.AreEqual(Keep, S7pCharacterExportRules.DecideTool(A, A, true, "Rainbow_HandDrill", "Rainbow_HandDrill", true, 0));
            Assert.AreEqual(Send, S7pCharacterExportRules.DecideTool(B, A, true, null, "Rainbow_HandDrill", true, 0), "another tool");
            Assert.AreEqual(Wait, S7pCharacterExportRules.DecideTool(B, A, true, "Veteran_Welder", "Rainbow_HandDrill", false, 0), "the old tool stays until the new variant is decided");
        }

        [TestMethod]
        public void IdentityText_NamesEveryField()
        {
            string t = S7pCharacterExportRules.IdentityText(103753901892655110L, "Default_Astronaut_Female", "Female", @"Models\Characters\Astronaut\Female\SE_astronaut.mwm",
                0.575f, 0.15f, 0.2f, new List<string> { "Rainbow_HandDrill", "Veteran_Welder" }, "Rainbow_HandDrill");
            StringAssert.Contains(t, "character 103753901892655110");
            StringAssert.Contains(t, "definition Default_Astronaut_Female gender Female");
            StringAssert.Contains(t, @"model Models\Characters\Astronaut\Female\SE_astronaut.mwm");
            StringAssert.Contains(t, "colorMaskHsv (0.575, 0.15, 0.2)");
            StringAssert.Contains(t, "skins [Rainbow_HandDrill,Veteran_Welder]");
            StringAssert.Contains(t, "toolSkin Rainbow_HandDrill");
            string u = S7pCharacterExportRules.IdentityText(1, null, null, null, 0, -1, 0, null, null);
            StringAssert.Contains(u, "definition ? gender ? model ?");
            StringAssert.Contains(u, "skins []");
            Assert.IsFalse(u.Contains("toolSkin"));
        }
    }
}
