using System;
using System.Collections.Generic;
using DeepEngineers.SE;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>S13 "Character and animation v2", the SE side's pure rules.</summary>
    [TestClass]
    public class S13SeTests
    {
        [TestMethod]
        public void Point5_ThirdPersonSwimsAlongTheBody()
        {
            Assert.AreEqual(S13Basis.Body, S13SeRules.Choose(true, false, true, false), "third person: the body's own look");
            Assert.AreEqual(S13Basis.Camera, S13SeRules.Choose(true, true, true, false), "first person: SE's camera (the 2026-10-17 fix)");
            Assert.AreEqual(S13Basis.Camera, S13SeRules.Choose(true, false, false, false), "de.s13 basis camera");
            Assert.AreEqual(S13Basis.Camera, S13SeRules.Choose(true, false, true, true), "S13.M5");
            Assert.AreEqual(S13Basis.Head, S13SeRules.Choose(false, false, true, false), "another camera controller: the head (S2b)");
            Assert.AreEqual("body", S13SeRules.Name(S13Basis.Body));
            Assert.AreEqual(180.0, S13SeRules.AngleDeg(0, 0, 1, 0, 0, -1), 1e-9);
            Assert.AreEqual(90.0, S13SeRules.AngleDeg(1, 0, 0, 0, 0, 3), 1e-9);
            Assert.AreEqual(0.0, S13SeRules.AngleDeg(0, 0, 0, 1, 0, 0), 1e-9);
        }

        [TestMethod]
        public void Point3_ToolColourIsTheEntitysNotZero()
        {
            float h, s, v;
            S13SeRules.ToolColour(0f, -1f, 0f, false, out h, out s, out v);
            Assert.AreEqual(-1f, s, "SE's OldGray kept: the shader key's saturation -0.2 → grey, not red");
            S13SeRules.ToolColour(0.3f, 0.2f, -0.1f, false, out h, out s, out v);
            Assert.AreEqual(0.3f, h); Assert.AreEqual(0.2f, s); Assert.AreEqual(-0.1f, v);
            S13SeRules.ToolColour(float.NaN, 0f, 0f, false, out h, out s, out v);
            Assert.AreEqual(S13SeRules.OldGrayS, s, "non-finite: OldGray");
            S13SeRules.ToolColour(0f, -1f, 0f, true, out h, out s, out v);
            Assert.AreEqual(0f, s, "S13.M3: (0, 0, 0)");
        }

        [TestMethod]
        public void Point3_ModTexturesResolveInTheModRoot()
        {
            Assert.IsNull(S13SeRules.MwmContentPath(@"Models\Characters\Astronaut\SE_astronaut.mwm"), "vanilla: Content-relative");
            Assert.AreEqual(@"C:\Steam\workshop\content\244850\123\", S13SeRules.MwmContentPath(@"C:\Steam\workshop\content\244850\123\Models\Characters\Suit.mwm"));
            Assert.IsNull(S13SeRules.MwmContentPath(@"C:\x\y.mwm"), "no models part");
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                @"C:\Steam\workshop\content\244850\123\Textures\Suit_cm.dds",
                @"C:\Game\Content\Textures\Models\Cubes\armor_cm.dds",
            };
            Func<string, bool> exists = files.Contains;
            bool via;
            Assert.AreEqual(@"C:\Steam\workshop\content\244850\123\Textures\Suit_cm.dds",
                S13SeRules.TextureFullPath(@"Textures\Suit_cm.dds", @"C:\Steam\workshop\content\244850\123\", @"C:\Game\Content", exists, out via));
            Assert.IsTrue(via, "found in the mod's root");
            Assert.AreEqual(@"C:\Game\Content\Textures\Models\Cubes\armor_cm.dds",
                S13SeRules.TextureFullPath(@"Textures\Models\Cubes\armor_cm.dds", @"C:\Steam\workshop\content\244850\123\", @"C:\Game\Content", exists, out via));
            Assert.IsFalse(via, "a vanilla texture of a mod model: Content");
            Assert.IsNull(S13SeRules.TextureFullPath(@"Textures\missing.dds", @"C:\Steam\workshop\content\244850\123\", @"C:\Game\Content", exists, out via), "missing anywhere");
            Assert.AreEqual(@"C:\Game\Content\Textures\Models\Cubes\armor_cm.dds",
                S13SeRules.TextureFullPath(@"Textures\Models\Cubes\armor_cm.dds", null, @"C:\Game\Content", exists, out via), "vanilla model: Content");
            Assert.AreEqual("", S13SeRules.TextureFullPath("", null, @"C:\Game\Content", exists, out via));
        }

        [TestMethod]
        public void Mutants_SetActiveClear()
        {
            try
            {
                StringAssert.StartsWith(S13SeMutants.Set("S13.M3", true), "S13.M3 ON");
                StringAssert.StartsWith(S13SeMutants.Set("s13.m5", true), "S13.M5 ON");
                Assert.AreEqual("S13.M3,S13.M5", S13SeMutants.Active);
                StringAssert.StartsWith(S13SeMutants.Set("S13.M1", true), "REFUSE");
                Assert.IsNull(S13SeMutants.Set("S16.M1", true));
                StringAssert.Contains(Mutants.Active, "S13.M3");
            }
            finally { S13SeMutants.ClearAll(); }
            Assert.AreEqual("", S13SeMutants.Active);
        }
    }
}
