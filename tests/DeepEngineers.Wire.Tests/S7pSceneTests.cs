using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using EventFlags = DeepEngineers.Wire.S7Conventions.EventFlags;
using EventType = DeepEngineers.Wire.S7Conventions.EventType;
using ThrusterKind = DeepEngineers.Wire.S7Conventions.ThrusterKind;
using WeaponKind = DeepEngineers.Wire.S7Conventions.WeaponKind;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S7p-scene-above (ROADMAP "### S7p Polish pass" P1, P6b, P7; CONTRACT §5.4 "S7p scene-above"): the GridAdd bits and
 /// tool subpart keys stay outside every earlier bit and key; the route toggle; the above-water recipes of route B; the
 /// emission accumulator and budget; the spinner table, integrator and rotation convention; the projection admission and
 /// the hidden-block rule; the missile selection. Pure, replay-exact (no game, no Unity).
 /// </summary>
    [TestClass]
    public class S7pSceneTests
    {
        // ============================================================================================ bits and keys

        [TestMethod]
        public void GridBits_AreFreeOfEveryEarlierBit()
        {
            Assert.AreEqual(16u, S7pScene.GridFlagProjection);
            Assert.AreEqual(32u, S7pScene.GridFlagMissile);
            uint taken = MirrorGridFlags.Static | MirrorGridFlags.Subgrid | MirrorGridFlags.FloatingObject | S7aConventions.GridFlagsExt.Ghost | MirrorPartFlags.VersionMask;
            Assert.AreEqual(0u, S7pScene.GridFlagProjection & taken, "Projection bit free");
            Assert.AreEqual(0u, S7pScene.GridFlagMissile & taken, "Missile bit free");
            Assert.AreEqual(0u, S7pScene.GridFlagProjection & S7pScene.GridFlagMissile);
        }

        [TestMethod]
        public void GridBits_ReadOnlyOnVersion1()
        {
            uint p = S7pScene.GridFlags(MirrorConventions.GridFlags(false, false), true, false);
            Assert.IsTrue(S7pScene.IsProjection(p));
            Assert.IsFalse(S7pScene.IsMissile(p));
            Assert.IsFalse(S7pScene.IsProjection(S7pScene.GridFlagProjection), "legacy nibble 0");
            Assert.IsFalse(S7pScene.IsProjection((2u << 28) | S7pScene.GridFlagProjection), "rejected nibble");
            Assert.IsFalse(S7aGhost.IsGhostGrid(42UL, p), "a projection is never the placement ghost");

            uint m = S7pScene.GridFlags(MirrorConventions.GridFlags(false, false, true), false, true);
            Assert.IsTrue(S7pScene.IsMissile(m));
            Assert.IsFalse(S7pScene.IsProjection(m));
            Assert.AreEqual(MirrorGridFlags.Version1 | MirrorGridFlags.FloatingObject | S7pScene.GridFlagMissile, m);
            Assert.IsFalse(S7pScene.IsMissile(MirrorConventions.GridFlags(false, false) | S7pScene.GridFlagMissile), "a missile is a floating object");
            Assert.AreEqual(MirrorConventions.GridFlags(true, true), S7pScene.GridFlags(MirrorConventions.GridFlags(true, true), false, false), "no bit added");
        }

        [TestMethod]
        public void ToolSubpartKeys_AreCharacterPartKeysOfTheirOwn()
        {
            var keys = new HashSet<ulong>();
            for (int i = 0; i < S7pScene.MaxToolSubparts; i++)
            {
                ulong k = S7pScene.ToolSubpartKey(i);
                Assert.IsTrue(keys.Add(k));
                Assert.IsTrue(S7pScene.IsToolSubpartKey(k));
                Assert.AreNotEqual(MirrorCharacterPart.Body, k);
                Assert.AreNotEqual(MirrorCharacterPart.Head1st, k);
                Assert.AreNotEqual(MirrorCharacterPart.Tool, k);
            }
            Assert.AreEqual(0x7470000000000000UL, S7pScene.ToolSubpartKey(0));
            Assert.IsFalse(S7pScene.IsToolSubpartKey(MirrorCharacterPart.Tool));
            Assert.IsFalse(S7pScene.IsToolSubpartKey(S7pScene.ToolSubpartBase | (ulong)S7pScene.MaxToolSubparts));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => S7pScene.ToolSubpartKey(-1));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => S7pScene.ToolSubpartKey(S7pScene.MaxToolSubparts));
        }

        // ============================================================================================ toggles

        [TestMethod]
        public void AboveMode_ParsesNamesAndEnv()
        {
            S7pAboveMode m;
            Assert.IsTrue(S7pScene.TryParseAbove("events", out m)); Assert.AreEqual(S7pAboveMode.Events, m);
            Assert.IsTrue(S7pScene.TryParseAbove(" SE ", out m)); Assert.AreEqual(S7pAboveMode.Se, m);
            Assert.IsTrue(S7pScene.TryParseAbove("off", out m)); Assert.AreEqual(S7pAboveMode.Off, m);
            Assert.IsTrue(S7pScene.TryParseAbove("on", out m)); Assert.AreEqual(S7pAboveMode.Events, m);
            Assert.IsFalse(S7pScene.TryParseAbove("maybe", out m));
            foreach (S7pAboveMode x in new[] { S7pAboveMode.Events, S7pAboveMode.Se, S7pAboveMode.Off })
            {
                Assert.IsTrue(S7pScene.TryParseAbove(S7pScene.Name(x), out m));
                Assert.AreEqual(x, m, "name round trip");
            }
            bool fromEnv;
            Assert.AreEqual(S7pAboveMode.Events, S7pScene.AboveFromEnv(null, out fromEnv)); Assert.IsFalse(fromEnv);
            Assert.AreEqual(S7pAboveMode.Events, S7pScene.AboveFromEnv("bogus", out fromEnv)); Assert.IsFalse(fromEnv);
            Assert.AreEqual(S7pAboveMode.Se, S7pScene.AboveFromEnv("se", out fromEnv)); Assert.IsTrue(fromEnv);
            Assert.IsTrue(S7pScene.OnOffFromEnv(null, true));
            Assert.IsFalse(S7pScene.OnOffFromEnv("off", true));
            Assert.IsTrue(S7pScene.OnOffFromEnv("1", false));
            Assert.IsFalse(S7pScene.OnOffFromEnv("perhaps", false));
        }

        [TestMethod]
        public void AboveMode_DecidesVariantsMissilesAndTrails()
        {
            Assert.IsTrue(S7pScene.PlaysAboveVariants(S7pAboveMode.Events));
            Assert.IsFalse(S7pScene.PlaysAboveVariants(S7pAboveMode.Se));
            Assert.IsFalse(S7pScene.PlaysAboveVariants(S7pAboveMode.Off));
            Assert.IsTrue(S7pScene.MirrorsMissiles(S7pAboveMode.Events));
            Assert.IsTrue(S7pScene.MirrorsMissiles(S7pAboveMode.Se));
            Assert.IsFalse(S7pScene.MirrorsMissiles(S7pAboveMode.Off));
            Assert.IsTrue(S7pScene.MissileTrail(S7pAboveMode.Events, false));
            Assert.IsTrue(S7pScene.MissileTrail(S7pAboveMode.Events, true));
            Assert.IsFalse(S7pScene.MissileTrail(S7pAboveMode.Se, false), "se: SE's own renderer above water");
            Assert.IsTrue(S7pScene.MissileTrail(S7pAboveMode.Se, true));
            Assert.IsFalse(S7pScene.MissileTrail(S7pAboveMode.Off, true));
        }

        [TestMethod]
        public void ParseArgv_ReadsChatWords()
        {
            string bad;
            Dictionary<string, string> d = S7pScene.ParseArgv(new[] { "above", "se", "projections=off", "subparts", "on" }, out bad);
            Assert.IsNull(bad);
            Assert.AreEqual("se", d["above"]);
            Assert.AreEqual("off", d["projections"]);
            Assert.AreEqual("on", d["subparts"]);
            d = S7pScene.ParseArgv(new[] { "status" }, out bad);
            Assert.IsNull(bad);
            Assert.AreEqual("status", d["action"]);
            S7pScene.ParseArgv(new[] { "above" }, out bad);
            Assert.IsNotNull(bad, "a key needs its value");
            S7pScene.ParseArgv(new[] { "colour", "red" }, out bad);
            Assert.IsNotNull(bad, "unknown word");
            Assert.AreEqual(0, S7pScene.ParseArgv(null, out bad).Count);
            d = S7pScene.ParseArgv(new[] { "scene" }, out bad);
            Assert.IsNull(bad);
            Assert.AreEqual("scene", d["action"]);
        }

        [TestMethod]
        public void SharedVerb_SceneAboveAnswersOnlyItsOwnKeys()
        {
            Assert.IsTrue(S7pScene.IsMine(new[] { "above" }, null, null));
            Assert.IsTrue(S7pScene.IsMine(new[] { "projections", "x" }, null, null));
            Assert.IsTrue(S7pScene.IsMine(new string[0], "scene", null));
            Assert.IsTrue(S7pScene.IsMine(new string[0], "Status", null));
            Assert.IsTrue(S7pScene.IsMine(new string[0], null, null), "no argument: the scene-above status");
            Assert.IsTrue(S7pScene.IsMine(null, null, new[] { "above", "se" }), "/de sn s7p above se");
            Assert.IsTrue(S7pScene.IsMine(null, null, new[] { "subparts=off" }));
            Assert.IsFalse(S7pScene.IsMine(new[] { "hold" }, null, null), "gameplay's key");
            Assert.IsFalse(S7pScene.IsMine(new[] { "damage" }, null, null));
            Assert.IsFalse(S7pScene.IsMine(new string[0], "gameplay", null));
            Assert.IsFalse(S7pScene.IsMine(new string[0], "fx", null), "effects-under's");
            Assert.IsFalse(S7pScene.IsMine(null, null, new[] { "fx", "thruster", "2" }), "/de sn s7p fx thruster 2");
            Assert.IsTrue(S7pScene.IsMine(null, null, new string[0]), "an empty argv (/de sn s7p): the scene-above status");
        }

        [TestMethod]
        public void ChatArgs_ReachEveryFeaturesKeys()
        {
            Dictionary<string, object> d = S7pScene.ChatArgs(new[] { "above", "se" });
            Assert.AreEqual("se", d["above"]);
            d = S7pScene.ChatArgs(new[] { "hold=0.5", "yawfix", "off" });
            Assert.AreEqual("0.5", d["hold"]);
            Assert.AreEqual("off", d["yawfix"]);
            d = S7pScene.ChatArgs(new[] { "gameplay" });
            Assert.AreEqual("gameplay", d["action"]);
            d = S7pScene.ChatArgs(new[] { "projections", "off", "scene" });
            Assert.AreEqual("off", d["projections"]);
            Assert.AreEqual("scene", d["action"]);
            Assert.AreEqual(0, S7pScene.ChatArgs(null).Count);
        }

        // ============================================================================================ route B rows

        [TestMethod]
        public void Plays_OnlyRowsAboveTheWaterline()
        {
            const S7pAboveMode ev = S7pAboveMode.Events;
            Assert.IsTrue(S7pAboveRules.Plays(ev, EventType.WeaponMuzzle, 0));
            Assert.IsTrue(S7pAboveRules.Plays(ev, EventType.Thruster, EventFlags.Attached | EventFlags.Sustain));
            Assert.IsTrue(S7pAboveRules.Plays(ev, EventType.Explosion, EventFlags.Surface), "a surface row above y 0 plays");
            Assert.IsFalse(S7pAboveRules.Plays(ev, EventType.WeaponMuzzle, EventFlags.Underwater), "the S7 player's");
            Assert.IsFalse(S7pAboveRules.Plays(ev, EventType.Splash, EventFlags.Surface), "the S7 player keeps the splash");
            Assert.IsFalse(S7pAboveRules.Plays(ev, 0, 0));
            Assert.IsFalse(S7pAboveRules.Plays(ev, 14, 0));
            Assert.IsTrue(S7pAboveRules.Plays(ev, EventType.Grinder, EventFlags.Stop | EventFlags.Underwater), "a Stop always ends a hold");
            Assert.IsFalse(S7pAboveRules.Plays(S7pAboveMode.Se, EventType.WeaponMuzzle, 0));
            Assert.IsFalse(S7pAboveRules.Plays(S7pAboveMode.Off, EventType.Explosion, 0));
        }

        [TestMethod]
        public void SustainTypes_AreThrustersToolsAndDrills()
        {
            foreach (byte t in new[] { EventType.Thruster, EventType.Welder, EventType.Grinder, EventType.Drill, EventType.Repair, EventType.PrawnDrill })
                Assert.IsTrue(S7pAboveRules.IsSustainType(t), EventType.Name(t));
            foreach (byte t in new[] { EventType.WeaponMuzzle, EventType.BulletStreak, EventType.Impact, EventType.Explosion, EventType.GridImpact, EventType.HullBreak, EventType.Splash })
                Assert.IsFalse(S7pAboveRules.IsSustainType(t), EventType.Name(t));
        }

        private static S7pPuff[] Puffs() { return new S7pPuff[S7pAboveRules.MaxPuffs]; }

        [TestMethod]
        public void OneShot_MuzzleFlashesAndMissileBackblast()
        {
            S7pPuff[] p = Puffs();
            int n = S7pAboveRules.OneShot(EventType.WeaponMuzzle, WeaponKind.Projectile, 1f, 1f, 50, p);
            Assert.IsTrue(n >= 2);
            Assert.AreEqual(S7pLook.Flash, p[0].Look);
            float gunFlash = p[0].Size;
            Assert.IsTrue(p[0].Life <= 0.1f, "a muzzle flash is short");
            n = S7pAboveRules.OneShot(EventType.WeaponMuzzle, WeaponKind.Missile, 1f, 1f, 50, p);
            Assert.AreEqual(S7pLook.Flash, p[0].Look);
            Assert.IsTrue(p[0].Size > gunFlash, "a missile launch flashes bigger");
            bool smoke = false;
            for (int i = 0; i < n; i++) smoke |= p[i].Look == S7pLook.Smoke && p[i].Count >= 8;
            Assert.IsTrue(smoke, "backblast smoke");
        }

        [TestMethod]
        public void OneShot_TracerFliesTheStreakInItsLifetime()
        {
            S7pPuff[] p = Puffs();
            Assert.AreEqual(1, S7pAboveRules.OneShot(EventType.BulletStreak, WeaponKind.Projectile, 1f, 40f, 100, p));
            // R4: with the Hit flag and a hit at 10 m the tracer flies 10 m, not the 40 m streak
            Assert.AreEqual(1, S7pAboveRules.OneShot(EventType.BulletStreak, WeaponKind.Projectile, 1f, 40f, 100, 1000, EventFlags.Hit, p));
            Assert.IsTrue(p[0].Speed * p[0].Life <= 10.01f, "tracer travel " + (p[0].Speed * p[0].Life) + " m > the 10 m hit");
            Assert.AreEqual(1, S7pAboveRules.OneShot(EventType.BulletStreak, WeaponKind.Projectile, 1f, 40f, 100, 1000, 0, p));
            Assert.IsTrue(p[0].Speed * p[0].Life > 10.01f, "without the Hit flag the whole streak");
            Assert.AreEqual(S7pLook.Tracer, p[0].Look);
            Assert.AreEqual(1, p[0].Count);
            Assert.AreEqual(0.1f, p[0].Life, 1e-6f);
            Assert.AreEqual(400f, p[0].Speed, 1e-3f, "40 m in 0.1 s");
            Assert.AreEqual(0f, p[0].Spread, "along the shot");
            S7pAboveRules.OneShot(EventType.BulletStreak, WeaponKind.Projectile, 1f, 500f, 100, p);
            Assert.AreEqual(600f, p[0].Speed, 1e-3f, "length clamped to StreakMaxM 60");
            S7pAboveRules.OneShot(EventType.BulletStreak, WeaponKind.Projectile, 1f, 40f, 0, p);
            Assert.AreEqual(0.05f, p[0].Life, 1e-6f, "lifetime clamped");
            Assert.AreEqual(800f, p[0].Speed, 1e-3f);
            S7pAboveRules.OneShot(EventType.BulletStreak, WeaponKind.Projectile, 1f, 1f, 600, p);
            Assert.AreEqual(60f, p[0].Speed, 1e-3f, "speed floor");
        }

        [TestMethod]
        public void OneShot_ExplosionScalesWithRadiusAndIsLayered()
        {
            S7pPuff[] p = Puffs();
            int n = S7pAboveRules.OneShot(EventType.Explosion, 0, 1f, 5f, 400, p);
            Assert.AreEqual(5, n, "flash, fireball, smoke, debris sparks, shock ring");
            Assert.AreEqual(S7pLook.Flash, p[0].Look);
            Assert.AreEqual(S7pLook.Fire, p[1].Look);
            Assert.AreEqual(S7pLook.Smoke, p[2].Look);
            Assert.AreEqual(S7pLook.Spark, p[3].Look);
            Assert.AreEqual(S7pLook.Vapor, p[4].Look);
            Assert.IsTrue(p[4].Ring, "the shock ring flies in a plane");
            float fire5 = p[1].Size, smoke5 = p[2].Size;
            int fireN5 = p[1].Count;
            S7pAboveRules.OneShot(EventType.Explosion, 0, 1f, 20f, 400, p);
            Assert.IsTrue(p[1].Size > fire5 && p[2].Size > smoke5 && p[1].Count >= fireN5, "bigger radius, bigger blast");
            S7pAboveRules.OneShot(EventType.Explosion, 0, 1f, 1e6f, 400, p);
            float maxFlash = p[0].Size;
            S7pAboveRules.OneShot(EventType.Explosion, 0, 1f, S7pAboveRules.ExplosionMaxRadius, 400, p);
            Assert.AreEqual(p[0].Size, maxFlash, "radius clamped at 60 m");
            Assert.IsTrue(p[1].Count <= 90 && p[2].Count <= 70 && p[3].Count <= 120, "particle counts capped");
        }

        [TestMethod]
        public void OneShot_ImpactsByMaterialAndStrength()
        {
            S7pPuff[] p = Puffs();
            int n = S7pAboveRules.OneShot(EventType.Impact, 2, 1f, 0.5f, 100, p);
            Assert.AreEqual(S7pLook.Dust, p[0].Look, "voxel: dust first");
            n = S7pAboveRules.OneShot(EventType.Impact, 1, 1f, 0.5f, 100, p);
            Assert.AreEqual(S7pLook.Spark, p[0].Look, "grid: sparks first");
            Assert.IsTrue(n >= 2);
            S7pAboveRules.OneShot(EventType.GridImpact, 0, 0f, 1f, 100, p);
            int soft = p[0].Count;
            S7pAboveRules.OneShot(EventType.GridImpact, 0, 1f, 1f, 100, p);
            Assert.IsTrue(p[0].Count > soft, "a harder collision sparks more");
            Assert.AreEqual(1, S7pAboveRules.OneShot(EventType.HullBreak, 0, 1f, 2.5f, 3000, p));
            Assert.AreEqual(S7pLook.Vapor, p[0].Look);
            Assert.AreEqual(0, S7pAboveRules.OneShot(EventType.Thruster, 0, 1f, 1f, 500, p), "sustains have no one-shot");
            Assert.AreEqual(0, S7pAboveRules.OneShot(EventType.Splash, 0, 1f, 1f, 500, p));
            Assert.ThrowsException<ArgumentException>(() => S7pAboveRules.OneShot(EventType.Explosion, 0, 1f, 1f, 1, new S7pPuff[2]));
        }

        private static S7pRate[] Rates() { return new S7pRate[S7pAboveRules.MaxRates]; }

        [TestMethod]
        public void Sustain_ThrusterFlameByKindAndStrength()
        {
            S7pRate[] r = Rates();
            Assert.AreEqual(0, S7pAboveRules.Sustain(EventType.Thruster, ThrusterKind.Ion, 0f, 1f, false, r), "no strength, no flame");
            Assert.AreEqual(1, S7pAboveRules.Sustain(EventType.Thruster, ThrusterKind.Ion, 0.3f, 1f, false, r));
            float low = r[0].PerSecond, lowSpeed = r[0].Puff.Speed;
            Assert.AreEqual(S7pLook.Fire, r[0].Puff.Look);
            Assert.IsTrue(r[0].Puff.B > r[0].Puff.R, "ion is blue");
            S7pAboveRules.Sustain(EventType.Thruster, ThrusterKind.Ion, 1f, 1f, false, r);
            Assert.IsTrue(r[0].PerSecond > low && r[0].Puff.Speed > lowSpeed, "stronger, denser and longer");
            float ion = r[0].PerSecond;
            S7pAboveRules.Sustain(EventType.Thruster, ThrusterKind.Atmospheric, 1f, 1f, false, r);
            Assert.IsTrue(r[0].PerSecond < ion, "atmospheric: a faint flame");
            int n = S7pAboveRules.Sustain(EventType.Thruster, ThrusterKind.Hydrogen, 1f, 1f, false, r);
            Assert.AreEqual(2, n, "hydrogen at full strength adds a vapour trail");
            Assert.IsTrue(r[0].Puff.R > r[0].Puff.B, "hydrogen is orange");
            Assert.IsTrue(S7pAboveRules.Sustain(EventType.Thruster, ThrusterKind.Ion, 1f, 100f, false, r) == 1 && r[0].PerSecond <= 600f, "rate capped");
        }

        [TestMethod]
        public void Sustain_ThrusterTakesSesOwnFlameColourWhenTheRowCarriesIt()
        {
            S7pRate[] r = Rates();
            ushort extra = S7pFxCodec.EncodeFlame(1f, 0.2f, 0f, 2.0f);
            float er, eg, eb, ls;
            Assert.IsTrue(S7pFxCodec.TryDecodeFlame(extra, out er, out eg, out eb, out ls));
            Assert.AreEqual(1, S7pAboveRules.Sustain(EventType.Thruster, ThrusterKind.Ion, 1f, 1f, false, r, extra));
            Assert.AreEqual(er, r[0].Puff.R, 1e-6f, "SE's colour above water as below");
            Assert.AreEqual(eg, r[0].Puff.G, 1e-6f);
            Assert.AreEqual(eb, r[0].Puff.B, 1e-6f);
            float longer = r[0].Puff.Speed;
            S7pAboveRules.Sustain(EventType.Thruster, ThrusterKind.Ion, 1f, 1f, false, r);
            Assert.IsTrue(r[0].Puff.B > r[0].Puff.R, "no extra: the kind's colour");
            Assert.IsTrue(longer > r[0].Puff.Speed, "a longer flame class flies further");
            S7pAboveRules.Sustain(EventType.Thruster, (ushort)(ThrusterKind.Hydrogen | (3 << 8)), 1f, 1f, false, r);
            Assert.IsTrue(r[0].Puff.R > r[0].Puff.B, "the low byte of subtype is the kind");
            S7pPuff[] p = Puffs();
            S7pAboveRules.OneShot(EventType.Impact, (ushort)(2 | (5 << 8)), 1f, 0.5f, 100, p);
            Assert.AreEqual(S7pLook.Dust, p[0].Look, "high bits ignored");
        }

        [TestMethod]
        public void Sustain_ToolsSparkOnlyWhenWorking()
        {
            S7pRate[] r = Rates();
            Assert.AreEqual(0, S7pAboveRules.Sustain(EventType.Grinder, 1, 0.25f, 1f, false, r), "a grinder in the air: nothing");
            Assert.AreEqual(2, S7pAboveRules.Sustain(EventType.Grinder, 1, 1f, 1f, true, r));
            Assert.AreEqual(S7pLook.Spark, r[0].Puff.Look);
            Assert.IsTrue(r[0].Puff.R > r[0].Puff.B, "grinder sparks are orange");
            Assert.AreEqual(0, S7pAboveRules.Sustain(EventType.Drill, 1, 0.25f, 1f, false, r));
            Assert.AreEqual(2, S7pAboveRules.Sustain(EventType.PrawnDrill, 2, 1f, 1f, true, r));
            Assert.AreEqual(S7pLook.Dust, r[0].Puff.Look);
            S7pAboveRules.Sustain(EventType.Welder, 2, 1f, 1f, false, r);
            float idle = r[0].PerSecond;
            S7pAboveRules.Sustain(EventType.Welder, 2, 1f, 1f, true, r);
            Assert.IsTrue(r[0].PerSecond > idle);
            Assert.IsTrue(r[0].Puff.B >= r[0].Puff.R, "welder sparks are blue-white");
            Assert.AreEqual(2, S7pAboveRules.Sustain(EventType.Repair, 1, 1f, 1f, true, r));
            Assert.AreEqual(0, S7pAboveRules.Sustain(EventType.Explosion, 0, 1f, 1f, true, r));
        }

        [TestMethod]
        public void MissileTrail_FlameAndSmokeAboveBubblesBelow()
        {
            S7pRate[] r = Rates();
            Assert.AreEqual(2, S7pAboveRules.MissileTrail(false, r));
            Assert.AreEqual(S7pLook.Fire, r[0].Puff.Look);
            Assert.AreEqual(S7pLook.Smoke, r[1].Puff.Look);
            Assert.AreEqual(2, S7pAboveRules.MissileTrail(true, r));
            Assert.AreEqual(S7pLook.Vapor, r[0].Puff.Look);
            Assert.IsTrue(r[0].Puff.Up > 0f, "bubbles rise");
        }

        [TestMethod]
        public void Accumulate_KeepsTheFractionExactly()
        {
            float carry = 0f;
            int total = 0;
            for (int f = 0; f < 60; f++) total += S7pAboveRules.Accumulate(30f, 1f / 60f, ref carry, 64);
            Assert.AreEqual(30, total, "30 per second over 60 frames of 1/60 s");
            carry = 0.9f;
            Assert.AreEqual(0, S7pAboveRules.Accumulate(0f, 1f / 60f, ref carry, 64));
            Assert.AreEqual(0f, carry, "a stopped source drops its fraction");
            carry = 0f;
            Assert.AreEqual(10, S7pAboveRules.Accumulate(10000f, 1f, ref carry, 10), "capped");
            carry = 0f;
            Assert.AreEqual(10, S7pAboveRules.Accumulate(100f, 5f, ref carry, 64), "dt clamped to 0.1 s");
        }

        [TestMethod]
        public void Budget_SharesThePerFrameEmission()
        {
            int left = 10;
            long capped = 0;
            Assert.AreEqual(6, S7pAboveRules.Budget(6, ref left, ref capped));
            Assert.AreEqual(4, S7pAboveRules.Budget(6, ref left, ref capped));
            Assert.AreEqual(2L, capped);
            Assert.AreEqual(0, S7pAboveRules.Budget(5, ref left, ref capped));
            Assert.AreEqual(7L, capped);
            Assert.AreEqual(0, S7pAboveRules.Budget(0, ref left, ref capped));
            Assert.AreEqual(7L, capped);
        }

        // ============================================================================================ P6b spinners

        [TestMethod]
        public void SpinTable_FindsTheDecompiledRenderTypes()
        {
            S7pSpinner s;
            Assert.IsTrue(S7pSpinRules.TryFind("Sandbox.Game.EntityComponents.Renders.MyShipDrillRenderComponent+MyDrillHeadRenderComponent", out s));
            Assert.AreEqual(S7pSpinAxis.Z, s.Axis);
            Assert.AreEqual("m_speed", s.SpeedField);
            Assert.IsFalse(s.OnBlockRender);
            Assert.IsTrue(S7pSpinRules.TryFind("Sandbox.Game.EntityComponents.Renders.MyRenderComponentShipGrinder+MyRenderComponentShipGrinderBlade", out s));
            Assert.AreEqual(S7pSpinAxis.X, s.Axis);
            Assert.AreEqual(41.8879, s.Up, 1e-9);
            Assert.IsTrue(S7pSpinRules.TryFind("Sandbox.Game.Components.MyRenderComponentThrust+MyPropellerRenderComponent", out s));
            Assert.IsTrue(s.OnBlockRender, "the propeller's speed is the thruster render's");
            Assert.AreEqual("m_propellerSpeed", s.SpeedField);
            Assert.IsTrue(S7pSpinRules.TryFind("SpaceEngineers.Game.EntityComponents.Renders.MyRenderComponentWindTurbine+TurbineRenderComponent", out s));
            Assert.AreEqual(S7pSpinAxis.Y, s.Axis);
            Assert.IsTrue(S7pSpinRules.TryFind("SpaceEngineers.Game.EntityComponents.Renders.MyRenderComponentAirVent+MyWallFanRenderComponent", out s));
            Assert.IsTrue(S7pSpinRules.TryFind("SpaceEngineers.Game.EntityComponents.Renders.MyRenderComponentHydrogenEngine+MyRotatingSubpartRenderComponent", out s));
            Assert.IsFalse(S7pSpinRules.TryFind("SpaceEngineers.Game.EntityComponents.Renders.MyRenderComponentMovingSubparts+MyRotatingSubpartRenderComponent`1[[X]]", out s),
                           "the generic moving-subparts rotor keeps no speed: not a spinner");
            Assert.IsFalse(S7pSpinRules.TryFind("Other.XMyShipDrillRenderComponent+MyDrillHeadRenderComponent", out s), "a type boundary");
            Assert.IsFalse(S7pSpinRules.TryFind(null, out s));
            Assert.IsFalse(S7pSpinRules.TryFind("Sandbox.Game.Components.MyRenderComponentCubeBlock", out s));
        }

        [TestMethod]
        public void Step_RelativeSpinUpReachesTheTargetInHalfASecond()
        {
            double a = 0, w = 0, nom = 0, target = 4 * Math.PI;
            for (int i = 0; i < 30; i++) S7pSpinRules.Step(ref a, ref w, ref nom, target, 0, 0, 1.0 / 60.0);
            Assert.AreEqual(target, w, 1e-9, "|target| / 0.5 s");
            double before = a;
            S7pSpinRules.Step(ref a, ref w, ref nom, target, 0, 0, 1.0 / 60.0);
            double d = a - before;
            if (d < 0) d += 2 * Math.PI;
            Assert.AreEqual(target / 60.0, d, 1e-9, "the angle advances by omega dt");
            for (int i = 0; i < 30; i++) S7pSpinRules.Step(ref a, ref w, ref nom, 0, 0, 0, 1.0 / 60.0);
            Assert.AreEqual(0, w, 1e-9, "and down again in half a second (the nominal speed's rate)");
            Assert.AreEqual(target, nom, 1e-12, "the nominal is the largest target");
        }

        [TestMethod]
        public void Step_AbsoluteRatesSignWrapAndClamps()
        {
            double a = 0, w = 0, nom = 0;
            S7pSpinRules.Step(ref a, ref w, ref nom, 5 * Math.PI, 41.8879, 20.94395, 0.1);
            Assert.AreEqual(4.18879, w, 1e-9, "the grinder's spin-up rate");
            S7pSpinRules.Step(ref a, ref w, ref nom, 0, 41.8879, 20.94395, 0.1);
            Assert.AreEqual(4.18879 - 2.094395, w, 1e-9, "its spin-down rate");
            w = 3; a = 0; nom = 0;
            S7pSpinRules.Step(ref a, ref w, ref nom, -3, 10, 1, 0.1);
            Assert.AreEqual(2, w, 1e-9, "a sign change uses the spin-up rate");
            a = 6.2; w = 10; nom = 0;
            S7pSpinRules.Step(ref a, ref w, ref nom, 10, 0, 0, 0.1);
            Assert.IsTrue(a >= 0 && a < 2 * Math.PI, "wrapped");
            Assert.AreEqual((6.2 + 1.0) % (2 * Math.PI), a, 1e-9);
            a = 0; w = 0; nom = 0;
            S7pSpinRules.Step(ref a, ref w, ref nom, 1e9, 1e12, 0, 0.1);
            Assert.AreEqual(S7pSpinRules.MaxOmega, w, 1e-6, "at most 0.9 pi per 60 Hz tick");
            Assert.IsTrue(S7pSpinRules.MaxOmega / 60.0 < Math.PI);
            a = 1; w = 2; nom = 2;
            S7pSpinRules.Step(ref a, ref w, ref nom, double.NaN, 0, 0, 0.1);
            Assert.AreEqual(1.6, w, 1e-9, "NaN target = stop, at the nominal's rate (2 / 0.5 per s)");
            a = 1; w = 2; nom = 2;
            S7pSpinRules.Step(ref a, ref w, ref nom, 2, 0, 0, 0);
            Assert.AreEqual(1, a, "no time, no motion");
            S7pSpinRules.Step(ref a, ref w, ref nom, 2, 0, 0, 10);
            Assert.AreEqual(1.2, a, 1e-9, "dt clamped to 0.1 s");
            a = 0; w = 0; nom = 0;
            S7pSpinRules.Step(ref a, ref w, ref nom, 0, 0, 0, 0.1);
            Assert.AreEqual(0, w);
            Assert.AreEqual(0, a, "a spinner that never ran stays put");
        }

        [TestMethod]
        public void Rotation_IsVRagesRowConvention()
        {
            double t = 0.3;
            SeMatrix z = S7pSpinRules.Rotation(S7pSpinAxis.Z, t);
            // VRage Matrix.CreateRotationZ: M11 = cos, M12 = sin, M21 = -sin, M22 = cos
            Assert.AreEqual(Math.Cos(t), z.Right.X, 1e-12);
            Assert.AreEqual(Math.Sin(t), z.Right.Y, 1e-12);
            Assert.AreEqual(-Math.Sin(t), z.Up.X, 1e-12);
            Assert.AreEqual(1, z.Backward.Z, 1e-12);
            SeMatrix x = S7pSpinRules.Rotation(S7pSpinAxis.X, t);
            Assert.AreEqual(Math.Sin(t), x.Up.Z, 1e-12, "CreateRotationX M23 = sin");
            SeMatrix y = S7pSpinRules.Rotation(S7pSpinAxis.Y, t);
            Assert.AreEqual(-Math.Sin(t), y.Right.Z, 1e-12, "CreateRotationY M13 = -sin");
            foreach (SeMatrix m in new[] { x, y, z })
            {
                Assert.AreEqual(1, m.Right.Length, 1e-12);
                Assert.AreEqual(1, m.Up.Length, 1e-12);
                Assert.AreEqual(0, Vec3.Dot(m.Right, m.Up), 1e-12);
                Assert.AreEqual(1, Vec3.Dot(Vec3.Cross(m.Right, m.Up), m.Backward), 1e-12, "right-handed");
            }
        }

        [TestMethod]
        public void Spun_TurnsAboutTheSubpartsOwnAxisAndKeepsItsPlace()
        {
            // a subpart at (1, 2, 3) in its block, its own Z along the grid's X
            var composed = new SeMatrix(new Vec3(0, 1, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 0), new Vec3(1, 2, 3));
            SeMatrix s = S7pSpinRules.Spun(composed, S7pSpinAxis.Z, 1.0);
            Assert.AreEqual(1, s.Translation.X, 1e-12);
            Assert.AreEqual(2, s.Translation.Y, 1e-12);
            Assert.AreEqual(3, s.Translation.Z, 1e-12);
            Assert.AreEqual(1, s.Backward.X, 1e-12, "the spin axis is unchanged");
            Vec3 p = s.Transform(new Vec3(0, 0, 0.5));
            Assert.AreEqual(1.5, p.X, 1e-12, "a point on the axis stays on it");
            Assert.AreEqual(2, p.Y, 1e-12);
            Vec3 q = s.Transform(new Vec3(1, 0, 0)) - s.Translation;
            Assert.AreEqual(0, q.X, 1e-12, "an off-axis point turns in the plane perpendicular to the axis");
            Assert.AreEqual(1, q.Length, 1e-12);
        }

        // ============================================================================================ P7 projections

        [TestMethod]
        public void Hidden_IsSesShowHideRule()
        {
            Assert.IsTrue(S7pProjectionRules.Hidden(-1f), "HideCube");
            Assert.IsFalse(S7pProjectionRules.Hidden(-0.25f), "ShowCube buildable");
            Assert.IsFalse(S7pProjectionRules.Hidden(-0.5f));
            Assert.IsFalse(S7pProjectionRules.Hidden(0f), "a plain block");
            Assert.IsTrue(S7pProjectionRules.Hidden(1f));
            Assert.IsTrue(S7pProjectionRules.Hidden(float.NaN));
        }

        [TestMethod]
        public void Admit_ChecksInOrderAndKeepsMirroredOnes()
        {
            Assert.AreEqual(S7pProjectionVerdict.Admit, S7pProjectionRules.Admit(true, true, true, true, true, 1f, 500, 0, false));
            Assert.AreEqual(S7pProjectionVerdict.Off, S7pProjectionRules.Admit(false, true, true, true, true, 1f, 500, 0, false));
            Assert.AreEqual(S7pProjectionVerdict.NotPreview, S7pProjectionRules.Admit(true, false, true, true, true, 1f, 500, 0, false));
            //: the clipboard's paste preview (no projector) is admitted while pasting, under the same caps
            Assert.AreEqual(S7pProjectionVerdict.Admit, S7pProjectionRules.Admit(true, true, false, false, false, 1f, 500, 0, false, true));
            Assert.AreEqual(S7pProjectionVerdict.NoProjector, S7pProjectionRules.Admit(true, true, false, false, false, 1f, 500, 0, false, false), "not pasting: the build preview stays out");
            Assert.AreEqual(S7pProjectionVerdict.TooManyBlocks, S7pProjectionRules.Admit(true, true, false, false, false, 1f, S7pScene.MaxProjectedBlocks + 1, 0, false, true));
            Assert.AreEqual(S7pProjectionVerdict.TooManyProjections, S7pProjectionRules.Admit(true, true, false, false, false, 1f, 500, S7pScene.MaxProjections, false, true));
            Assert.AreEqual(S7pProjectionVerdict.NoProjector, S7pProjectionRules.Admit(true, true, false, false, false, 1f, 500, 0, false), "the build preview");
            Assert.AreEqual(S7pProjectionVerdict.NotProjecting, S7pProjectionRules.Admit(true, true, true, false, true, 1f, 500, 0, false));
            Assert.AreEqual(S7pProjectionVerdict.NotProjectedGrid, S7pProjectionRules.Admit(true, true, true, true, false, 1f, 500, 0, false), "one grid per projector");
            Assert.AreEqual(S7pProjectionVerdict.Scaled, S7pProjectionRules.Admit(true, true, true, true, true, 0.02f, 500, 0, false), "a console miniature");
            Assert.AreEqual(S7pProjectionVerdict.TooManyBlocks, S7pProjectionRules.Admit(true, true, true, true, true, 1f, S7pScene.MaxProjectedBlocks + 1, 0, false));
            Assert.AreEqual(S7pProjectionVerdict.TooManyProjections, S7pProjectionRules.Admit(true, true, true, true, true, 1f, 500, S7pScene.MaxProjections, false));
            Assert.AreEqual(S7pProjectionVerdict.Admit, S7pProjectionRules.Admit(true, true, true, true, true, 1f, 500, S7pScene.MaxProjections, true), "a mirrored one keeps its place");
            foreach (S7pProjectionVerdict v in Enum.GetValues(typeof(S7pProjectionVerdict)))
                Assert.IsFalse(S7pProjectionRules.Name(v).StartsWith("unknown", StringComparison.Ordinal), v.ToString());
        }

        [TestMethod]
        public void HologramColour_IsTranslucentBlue()
        {
            Assert.IsTrue(S7pProjectionRules.B > S7pProjectionRules.R && S7pProjectionRules.B >= S7pProjectionRules.G);
            Assert.IsTrue(S7pProjectionRules.A > 0f && S7pProjectionRules.A < 0.6f);
        }

        // ============================================================================================ P1 missiles

        [TestMethod]
        public void Select_NearestFirstCappedTiesById()
        {
            long[] ids = { 50, 10, 30, 20, 40, 60 };
            double[] d = { 300, 100, 2000, 100, 50, 1499 };
            int[] pick = new int[3];
            int dropped;
            int n = S7pMissileRules.Select(ids, d, ids.Length, 3, pick, out dropped);
            Assert.AreEqual(3, n);
            Assert.AreEqual(40L, ids[pick[0]], "50 m");
            Assert.AreEqual(10L, ids[pick[1]], "100 m, smaller id first");
            Assert.AreEqual(20L, ids[pick[2]]);
            Assert.AreEqual(2, dropped, "300 m and 1,499 m over the cap; 2,000 m outside the radius is not a drop");
            n = S7pMissileRules.Select(ids, d, 0, 3, pick, out dropped);
            Assert.AreEqual(0, n);
            Assert.AreEqual(0, dropped);
            double[] nan = { double.NaN };
            Assert.AreEqual(0, S7pMissileRules.Select(new long[] { 1 }, nan, 1, 3, pick, out dropped), "NaN distance refused");
        }

        [TestMethod]
        public void Select_IsOrderIndependent()
        {
            var rng = new Random(7);
            for (int round = 0; round < 50; round++)
            {
                int count = rng.Next(0, 40);
                var ids = new long[count];
                var d = new double[count];
                for (int i = 0; i < count; i++) { ids[i] = rng.Next(1, 1000); d[i] = Math.Floor(rng.NextDouble() * 2000); }
                int[] a = new int[S7pScene.MaxMissiles], b = new int[S7pScene.MaxMissiles];
                int da, db;
                int na = S7pMissileRules.Select(ids, d, count, S7pScene.MaxMissiles, a, out da);
                // the same inputs reversed
                var ids2 = (long[])ids.Clone();
                var d2 = (double[])d.Clone();
                Array.Reverse(ids2);
                Array.Reverse(d2);
                int nb = S7pMissileRules.Select(ids2, d2, count, S7pScene.MaxMissiles, b, out db);
                Assert.AreEqual(na, nb);
                Assert.AreEqual(da, db);
                for (int i = 0; i < na; i++)
                {
                    Assert.AreEqual(d[a[i]], d2[b[i]]);
                    Assert.AreEqual(ids[a[i]], ids2[b[i]]);
                    if (i > 0) Assert.IsTrue(d[a[i - 1]] <= d[a[i]]);
                }
            }
        }
    }
}
