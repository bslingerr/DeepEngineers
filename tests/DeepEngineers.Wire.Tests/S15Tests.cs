using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S15 "Gameplay and immersion polish" (CONTRACT §5.4 "S15 conventions"): the pinned canonical text, the voice gate over

 /// codecs, the sky math, the disc light and the palettes,
 /// against the Python twin's vectors (tools/s15/s15_ref.py --vectors -> tests/s15/vectors/*.tsv).
 /// </summary>
    [TestClass]
    public unsafe class S15Tests
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static List<string[]> Lines(string name)
        {
            string path = Path.Combine(S5Repo.Root, "tests", "s15", "vectors", name);
            Assert.IsTrue(File.Exists(path), "vector file missing: " + path + " (python -I tools/s15/s15_ref.py --vectors)");
            var rows = new List<string[]>();
            bool header = true;
            foreach (string raw in File.ReadAllLines(path, Encoding.ASCII))
            {
                if (raw.Length == 0) continue;
                if (header) { header = false; continue; }
                rows.Add(raw.Split('\t'));
            }
            Assert.IsTrue(rows.Count > 0, name + " has no rows");
            return rows;
        }

        private static double D(string s) { return s == "nan" ? double.NaN : double.Parse(s, NumberStyles.Float, Inv); }
        private static int I(string s) { return int.Parse(s, Inv); }

        [TestMethod]
        public void CanonicalText_IsPinned()
        {
            string t = S15Conventions.CanonicalText();
            Assert.AreEqual(S15Conventions.PinnedHash, S15Conventions.Hash(), "a changed rule changes the hash: re-pin CONTRACT, Wire and the twin together");
            Assert.AreEqual(S15Conventions.CanonicalLines, t.Split('\n').Length - 1);
            Assert.AreEqual(S15Conventions.CanonicalBytes, t.Length);
            Assert.IsTrue(t.StartsWith("ext s15 voice 5\nvoice 1 Welcome key grid rearmMs 5000 cooldownMs 120000 member SubRoot.welcomeNotification SubRoot.cs:64 prefab cyclops when boarding a large grid or a station\n", StringComparison.Ordinal));
            StringAssert.Contains(t, "voice 2 EnginePowerUp key grid rearmMs 2000 cooldownMs 10000 member SubRoot.enginePowerUpNotification SubRoot.cs:98 prefab cyclops when the aboard grid's power from none to some\n");
            StringAssert.Contains(t, "voice 3 EnginePowerDown key grid rearmMs 2000 cooldownMs 10000 member SubRoot.enginePowerDownNotification SubRoot.cs:100 prefab cyclops when the aboard grid's power switch off\n");
            StringAssert.Contains(t, "voice 4 SmallWelcome key grid rearmMs 5000 cooldownMs 120000 member Vehicle.welcomeNotification Vehicle.cs:105 prefab seamoth when boarding a small grid\n");
            Assert.IsFalse(t.Contains("Oxygen") || t.Contains("FirstDeep") || t.Contains("SilentRunning") || t.Contains("AbandonShip") || t.Contains("HullBreach"),
                           "owner 2026-10-24: only the four lines remain in the conventions");
            StringAssert.Contains(t, "const Events Offset 1792\n");
            StringAssert.Contains(t, "const C5 Version 2\n");
            StringAssert.Contains(t, "const Mix MusicAbovePermille 600\n");
            StringAssert.Contains(t, "const Mix CrossfadeMs 2000\n");
            StringAssert.Contains(t, "mix vca sfx 634d9305-7695-40df-999a-cc1d049e1ebe music 0d0dd1e2-9d46-4a5b-aae8-ae412d827831 voice 3c4e7775-5fbe-4b00-83e0-35bc9add106a SoundSystem.cs:57-59\n");
            StringAssert.Contains(t, "const Story AuroraNeverS 1000000000\n");
            StringAssert.Contains(t, "sonar tag sonar\n");
            foreach (char c in t) Assert.IsTrue(c < 128, "ASCII");
            StringAssert.Contains(t, "const C5 Offset 172\n");
            StringAssert.Contains(t, "const Voice VolumePermille 1600\n");
            StringAssert.Contains(t, "water DE_4546B_Water texture Textures\\DE_4546B_Water.dds size 256 mean 110,156,184 previous JWater2 transparent off softnessOffset 0.0001 specularIntensity 0\n");
            StringAssert.Contains(t, "air 30 km face 34,59,75\n");
            StringAssert.Contains(t, "ending sea 34,59,75 cloud 120,114,110 cover 127 limb 66,69,77 rim warm 164,135,121 cool 82,85,94\n");
            StringAssert.Contains(t, "const Colour SeaChromaPermille 30\n");
            Assert.IsFalse(S15Conventions.Water.Transparent, "the owner's opaque sea (2026-10-23 23:12)");
        }

        [TestMethod]
        public void TwinText_IsTheSame()
        {
            // the twin's --text output equals ours: the twin's file holds the same pinned hash and size
            string py = File.ReadAllText(Path.Combine(S5Repo.Root, "tools", "s15", "s15_ref.py"), Encoding.UTF8);
            StringAssert.Contains(py, "PINNED_HASH = 0x" + S15Conventions.PinnedHash.ToString("x16", Inv));
            StringAssert.Contains(py, "CANON_LINES, CANON_BYTES = " + S15Conventions.CanonicalLines + ", " + S15Conventions.CanonicalBytes);
        }

        [TestMethod]
        public void Layout_FreeSpaceOnly()
        {
            // the C5 block sits after S16's (0x80-0xA9) inside layout 1.0's pad_80 (0x80-0xFF)
            Assert.IsTrue(S15Conventions.C5.Offset >= S16Conventions.C5.Offset + S16Conventions.C5.Size);
            Assert.IsTrue(S15Conventions.C5.Offset + S15Conventions.C5.Size <= 0x100);
            Assert.AreEqual(256, sizeof(SeState));
            // the sky records sit in C6's 4 KiB region space past the 64-byte SeTime record
            Assert.AreEqual(64, sizeof(SeTime));
            Assert.IsTrue(S15Conventions.Sky.FrameOffset >= sizeof(SeTime));
            Assert.IsTrue(S15Conventions.Sky.FrameOffset + S15Conventions.Sky.FrameSize <= S15Conventions.Sky.ListOffset);
            Assert.IsTrue(S15Conventions.Sky.ListOffset + S15Conventions.Sky.ListHeader + S15Conventions.Sky.MaxBodies * S15Conventions.Sky.RowSize <= 0x1000);
            Assert.AreEqual(4, S15Conventions.Event.Count);
            Assert.AreEqual(4, S15Conventions.Event.C5Count);
            // the events block (the sonar) after the body list, inside the region
            Assert.IsTrue(S15Conventions.Events.Offset >= S15Conventions.Sky.ListOffset + S15Conventions.Sky.ListHeader + S15Conventions.Sky.MaxBodies * S15Conventions.Sky.RowSize);
            Assert.IsTrue(S15Conventions.Events.Offset + S15Conventions.Events.Size <= 0x1000);
            Assert.AreEqual(S15Conventions.Event.Count, S15Conventions.Voices.Length);
            for (int i = 0; i < S15Conventions.Voices.Length; i++) Assert.AreEqual(i + 1, S15Conventions.Voices[i].Id);
            Assert.AreEqual(S16Conventions.Voice.DuckMs, S15Conventions.Voice.QueueGapMs, "one voice at a time: S16's duck window");
        }

        [TestMethod]
        public unsafe void Events_Codec_MatchesTheTwin()
        {
            int n = 0;
            foreach (string[] r in Lines("wire.tsv"))
            {
                if (r[0] != "events") continue;
                byte[] want = Hex(r[1]);
                var e = new S15EventsState { Flags = 1, WorldEpoch = 9, SonarSeq = 17 };
                byte* buf = stackalloc byte[S15Conventions.Events.Size];
                S15EventsWire.Encode(buf, e);
                for (int i = 0; i < S15Conventions.Events.Size; i++) Assert.AreEqual(want[i], buf[i], "byte 0x" + i.ToString("x2", Inv));
                var back = new S15EventsState();
                S15EventsWire.Decode(buf, back);
                Assert.IsTrue(back.Valid);
                Assert.AreEqual(17u, back.SonarSeq);
                Assert.AreEqual(9u, back.WorldEpoch);
                Assert.AreEqual(0, back.Count, "version 2 carries no voice counters");
                n++;
            }
            Assert.AreEqual(1, n);
        }

        [TestMethod]
        public void PickRules_MatchTheTwin()
        {
            int n = 0;
            S15PowerSwitch p = null;
            string scenario = null;
            foreach (string[] r in Lines("picks.tsv"))
            {
                switch (r[0])
                {
                    case "night": Assert.AreEqual(D(r[4]), S15PickRules.NightFactor(D(r[1])), 1e-12); break;
                    case "scare": Assert.AreEqual(r[4] == "1", S15PickRules.Scares(D(r[1]))); break;
                    case "sonar": Assert.AreEqual(r[4] == "1", S15PickRules.IsSonarName(r[1]), "'" + r[1] + "'"); break;
                    case "group": Assert.AreEqual(D(r[4]), S15MixRules.GroupTarget(D(r[1]), D(r[2])), 1e-12, string.Join(" ", r)); break;
                    case "vca": Assert.AreEqual(D(r[4]), S15MixRules.VcaVolume(D(r[1]), D(r[2])), 1e-12, string.Join(" ", r)); break;
                    case "power":
                        {
                            string[] sc = r[1].Split(' ');
                            if (sc[0] != scenario) { scenario = sc[0]; p = new S15PowerSwitch(); }
                            string[] sp = r[3].Split('/');
                            long grid = long.Parse(r[2], Inv);
                            bool on = sp[0] == "1", pw = sp[1] == "1";
                            if (sc.Length > 1 && sc[1] == "B") p.Baseline(grid, on, pw);
                            else Assert.AreEqual(r[4] == "1", p.Step(grid, on, pw), string.Join(" ", r));
                            break;
                        }
                    default: Assert.Fail("unknown picks.tsv row " + r[0]); break;
                }
                n++;
            }
            Assert.AreEqual(51, n, "picks.tsv rows");
        }

        [TestMethod]
        public void Gate_MatchesTheTwin()
        {
            S15VoiceGate g = null;
            string scenario = null;
            int n = 0;
            foreach (string[] r in Lines("gate.tsv"))
            {
                if (r[0] != scenario) { scenario = r[0]; g = new S15VoiceGate(); }
                S15GateResult got;
                if (r[6] == "2") { g.Baseline(I(r[1]), long.Parse(r[2], Inv), r[3] == "1", long.Parse(r[4], Inv)); got = S15GateResult.None; }
                else got = g.Step(I(r[1]), long.Parse(r[2], Inv), r[3] == "1", long.Parse(r[4], Inv), r[5] == "1", r[6] == "1", r[6] == "3");
                Assert.AreEqual(I(r[7]), (int)got, scenario + " at " + r[4] + " ms");
                n++;
            }
            Assert.IsTrue(n >= 45);
        }

        [TestMethod]
        public void Gate_HandChecks()
        {
            var g = new S15VoiceGate();
            const int w = S15Conventions.Event.Welcome, e = S15Conventions.Event.EnginePowerUp;
            Assert.AreEqual(S15GateResult.Played, g.Step(w, 1, true, 0, true, false));
            Assert.AreEqual(S15GateResult.None, g.Step(w, 1, true, 100, true, false), "a held condition is no new event");
            Assert.AreEqual(S15GateResult.Played, g.Step(w, 2, true, 100, true, false), "another grid has its own key");
            g.Off(w, 1, 200);
            Assert.AreEqual(S15GateResult.Cooldown, g.Step(w, 1, true, 60000, true, false), "S16VoiceRule's 120 s cooldown");
            Assert.AreEqual(S15GateResult.SeAudio, g.Step(e, 1, true, 0, false, false), "SE's audio live: nothing sent");
            g.Off(e, 1, 10);
            Assert.AreEqual(S15GateResult.Played, g.Step(e, 1, true, 5000, true, false), "no cooldown started by an SE-audio event");
            g.Off(e, 1, 6000);
            Assert.AreEqual(S15GateResult.Cooldown, g.Step(e, 1, true, 9000, true, false), "the power lines' short cooldown (10 s)");
            g.Off(e, 1, 9500);
            Assert.AreEqual(S15GateResult.Played, g.Step(e, 1, true, 16000, true, false), "no once-per-session rule");
            Assert.IsTrue(S15VoiceGate.SnAudioLive(1f) && S15VoiceGate.SnAudioLive(0.5f) && !S15VoiceGate.SnAudioLive(0f));
        }

        [TestMethod]
        public void Gate_NeverHeld_And_Baseline()
        {

            var g = new S15VoiceGate();
            const int w = S15Conventions.Event.Welcome, s = S15Conventions.Event.SmallWelcome, e = S15Conventions.Event.EnginePowerUp;
            Assert.AreEqual(S15GateResult.SeAudio, g.Step(s, 1, true, 0, false, false));
            Assert.IsFalse(g.IsPending(s, 1));
            Assert.AreEqual(S15GateResult.None, g.Step(s, 1, true, 30000, true, false), "under water: still nothing");
            // S15.
            Assert.AreEqual(S15GateResult.SeAudio, g.Step(w, 2, true, 0, false, false, true));
            Assert.IsTrue(g.IsPending(w, 2));
            Assert.AreEqual(S15GateResult.Played, g.Step(w, 2, true, 31000, true, false, true), "S15.M17: sent under water");
            // a state that already holds when the grid becomes aboard is the baseline
            g.Baseline(e, 4, true, 0);
            Assert.AreEqual(S15GateResult.None, g.Step(e, 4, true, 167, true, false));
            Assert.AreEqual(S15GateResult.None, g.Step(e, 4, false, 1000, true, false));
            Assert.AreEqual(S15GateResult.Played, g.Step(e, 4, true, 4000, true, false), "the next real edge plays");
        }

        [TestMethod]
        public void PowerSwitch_HandChecks()
        {
            var p = new S15PowerSwitch();
            Assert.IsFalse(p.Step(5, true, true), "the first sample is the baseline");
            Assert.IsTrue(p.Step(5, false, false), "Y off from a powered grid (SE recomputed the distribution at once: unpowered on that scan)");
            Assert.IsTrue(p.Step(5, false, false), "held while off");
            Assert.IsFalse(p.Step(5, true, true), "Y on clears it");
            Assert.IsFalse(p.Step(7, false, false), "another grid: its baseline");
            p.Baseline(8, true, false);
            Assert.IsFalse(p.Step(8, false, false), "an unpowered grid switched off: nothing shuts down");
        }

        [TestMethod]
        public void MixRules()
        {
            Assert.AreEqual(0.6, S15MixRules.GroupTarget(0, S15MixRules.Above(S15Conventions.Mix.MusicAbovePermille)), 1e-12, "the music above water");
            Assert.AreEqual(1.0, S15MixRules.GroupTarget(1, 0.6), 1e-12, "under water");
            Assert.AreEqual(0.48, S15MixRules.VcaVolume(0.8, 0.6), 1e-12, "the player's slider x the level");
            Assert.AreEqual(0.0, S15MixRules.VcaVolume(double.NaN, 1), 1e-12);
        }

        [TestMethod]
        public void Aboard_MatchesTheTwin()
        {
            S15AboardHold h = null;
            string mode = null;
            foreach (string[] r in Lines("aboard.tsv"))
            {
                if (r[2] != mode) { mode = r[2]; h = new S15AboardHold(); }
                Assert.AreEqual(long.Parse(r[3], Inv), h.Step(long.Parse(r[0], Inv), long.Parse(r[1], Inv), r[2] == "1"), "at " + r[1] + " ms nohold " + r[2]);
            }
        }

        private static byte[] Hex(string h)
        {
            var b = new byte[h.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = byte.Parse(h.Substring(2 * i, 2), NumberStyles.HexNumber, Inv);
            return b;
        }

        private static string Vec(string kind)
        {
            foreach (string[] r in Lines("wire.tsv")) if (r[0] == kind) return r[1];
            Assert.Fail("no " + kind + " row");
            return null;
        }

        [TestMethod]
        public void C5_Codec_MatchesTheTwin()
        {
            var w = new S15WireState { Flags = 1 | 2 | 8 | 16, WorldEpoch = 7, WelcomeSeq = 1, PowerUpSeq = 2, PowerDownSeq = 3, SmallWelcomeSeq = 4, GridId = 0x0123456789ABCDEF };
            var st = new SeState();
            S15Wire.Write(ref st, ref w);
            byte* b = (byte*)&st + S15Conventions.C5.Offset;
            byte[] want = Hex(Vec("c5"));
            Assert.AreEqual(S15Conventions.C5.Size, want.Length);
            for (int i = 0; i < want.Length; i++) Assert.AreEqual(want[i], b[i], "byte " + i);
            S15WireState back;
            Assert.IsTrue(S15Wire.TryRead(ref st, out back));
            Assert.AreEqual(4u, back.SeqOf(S15Conventions.Event.SmallWelcome));
            Assert.AreEqual(3u, back.SeqOf(S15Conventions.Event.EnginePowerDown));
            Assert.AreEqual(2, back.Version);
            Assert.AreEqual(0x0123456789ABCDEF, back.GridId);
            // S16's block is untouched
            S16WireState s16;
            Assert.IsFalse(S16Wire.TryRead(ref st, out s16));
            S15Wire.Clear(ref st);
            Assert.IsFalse(S15Wire.TryRead(ref st, out back), "cleared: version 0 = absent");
        }

        [TestMethod]
        public void Sky_Codecs_MatchTheTwin()
        {
            byte* region = stackalloc byte[0x1000];
            for (int i = 0; i < 0x1000; i++) region[i] = 0;
            var f = new S15SkyFrame
            {
                Flags = 1, BodyCount = 3, ListSeq = 42, WorldEpoch = 9, Tick = 123456789, CamRel = new Vec3(1.5, -2.25, 60150.0),
                M00 = 0.5, M01 = -0.25, M02 = 0.125, M10 = 1.0, M11 = 0.0, M12 = -1.0, M20 = 0.75, M21 = 0.0625, M22 = -0.5,
            };
            S15SkyWire.WriteFrame(region, ref f);
            byte[] want = Hex(Vec("frame"));
            for (int i = 0; i < want.Length; i++) Assert.AreEqual(want[i], region[S15Conventions.Sky.FrameOffset + 4 + i], "frame byte " + (4 + i));
            Assert.AreEqual(2u, *(uint*)(region + S15Conventions.Sky.FrameOffset), "one complete seqlock write: seq 2");
            S15SkyFrame back;
            uint seq;
            Assert.AreEqual(SeqlockReadStatus.Fresh, S15SkyWire.TryReadFrame(region, out back, out seq));
            Assert.IsTrue(back.Valid);
            Assert.AreEqual(123456789UL, back.Tick);
            Assert.AreEqual(60150.0, back.CamRel.Z);
            Assert.AreEqual(0.0625, back.M21, 1e-7);
            for (int i = 0; i < 64; i++) Assert.AreEqual(0, region[i], "SeTime's 64 bytes untouched");

            var bodies = new List<S15Body>
            {
                new S15Body { Rel = new Vec3(0, 0, 300000), RadiusM = 19000f, AtmosphereM = 0f, Palette = 3, Flags = 0, Name = "Moon" },
                new S15Body { Rel = new Vec3(1, 2, 3), RadiusM = 60000f, AtmosphereM = 69000f, Palette = 1, Flags = 3, Name = "DE_4546B" },
                new S15Body { Rel = new Vec3(-4.5, 0.25, 9e5), RadiusM = 9500f, AtmosphereM = 10450f, Palette = 5, Flags = 2, Name = "Europa-with-a-very-long-name-cut" },
            };
            Assert.AreEqual(3, S15SkyWire.WriteList(region, 5, 9, bodies));
            want = Hex(Vec("list"));
            for (int i = 0; i < want.Length; i++) Assert.AreEqual(want[i], region[S15Conventions.Sky.ListOffset + 4 + i], "list byte " + (4 + i));
            var got = new List<S15Body>();
            uint ls, ep;
            Assert.AreEqual(SeqlockReadStatus.Fresh, S15SkyWire.TryReadList(region, got, out ls, out ep, out seq));
            Assert.AreEqual(5u, ls);
            Assert.AreEqual(3, got.Count);
            Assert.AreEqual("Europa-with-a-very-long-name", got[2].Name, "cut to 28");
            Assert.IsTrue(got[1].Bound && got[1].HasAtmosphere && !got[0].Bound);
            Assert.AreEqual(9e5, got[2].Rel.Z);
        }

        [TestMethod]
        public void Sky_Math_MatchesTheTwin()
        {
            SiteMap map = SiteMap.SiteV1Test;
            var cams = new[] { new Vec3(0, 150, 0), new Vec3(5000, 800, -3000), new Vec3(40000, 300, 20000), new Vec3(-12000, 900, 61000) };
            var named = new Dictionary<string, KeyValuePair<Vec3, double>>
            {
                { "Moon", new KeyValuePair<Vec3, double>(new Vec3(0, 0, 300000), 19000) },
                { "EarthLike", new KeyValuePair<Vec3, double>(new Vec3(-400000, 250000, 100000), 60000) },
                { "Mars", new KeyValuePair<Vec3, double>(new Vec3(900000, -150000, -500000), 60000) },
                { "Europa", new KeyValuePair<Vec3, double>(new Vec3(120000, 520000, -60000), 9500) },
            };
            int n = 0;
            foreach (string[] r in Lines("sky.tsv"))
            {
                Vec3 p = map.SnToSe(cams[I(r[0])]);
                S15SkyFrame f = S15Sky.FrameAt(map, p, false);
                double[] m = { f.M00, f.M01, f.M02, f.M10, f.M11, f.M12, f.M20, f.M21, f.M22 };
                for (int k = 0; k < 9; k++) Assert.AreEqual(D(r[7 + k]), m[k], 1e-9, "m" + k + " cam " + r[0]);
                KeyValuePair<Vec3, double> b = named[r[1]];
                Vec3 dir;
                double ang, dist;
                Assert.IsTrue(S15Sky.View(ref f, b.Key, b.Value, out dir, out ang, out dist));
                Assert.AreEqual(D(r[2]), dir.X, 1e-9);
                Assert.AreEqual(D(r[3]), dir.Y, 1e-9);
                Assert.AreEqual(D(r[4]), dir.Z, 1e-9);
                Assert.AreEqual(D(r[5]), ang, 1e-12);
                Assert.AreEqual(D(r[6]), dist, 1e-6);
                // the same through the SiteMap's own mapdir (the matrix is mapdir)
                Vec3 se;
                double a2;
                Assert.IsTrue(S15Sky.ViewSe(f.CamRel, b.Key, b.Value, out se, out a2));
                Assert.IsTrue(S15Sky.AngleDeg(map.DirSeToSn(p, se), dir) < 1e-5, "acos near 1 resolves ~1e-6 degree");
                n++;
            }
            Assert.AreEqual(16, n);
        }

        [TestMethod]
        public void Sky_IdentityMutant_IsWrong()
        {
            SiteMap map = SiteMap.SiteV1Test;
            Vec3 p = map.SnToSe(new Vec3(5000, 800, -3000));
            S15SkyFrame good = S15Sky.FrameAt(map, p, false), bad = S15Sky.FrameAt(map, p, true);
            Vec3 d1, d2;
            double a1, a2, x1, x2;
            Assert.IsTrue(S15Sky.View(ref good, new Vec3(0, 0, 300000), 19000, out d1, out a1, out x1));
            Assert.IsTrue(S15Sky.View(ref bad, new Vec3(0, 0, 300000), 19000, out d2, out a2, out x2));
            Assert.IsTrue(S15Sky.AngleDeg(d1, d2) > 5, "S15.M3 (SE directions sent unmapped) moves the body by degrees");
            Assert.AreEqual(a1, a2, 1e-15, "the size is not the mutant's");
        }

        [TestMethod]
        public void Light_MatchesTheTwin()
        {
            foreach (string[] r in Lines("light.tsv"))
            {
                double[] v = new double[r.Length];
                for (int i = 0; i < r.Length; i++) v[i] = D(r[i]);
                double l = S15Sky.Light(v[0], v[1], new Vec3(v[2], v[3], v[4]), new Vec3(v[5], v[6], v[7]), new Vec3(v[8], v[9], v[10]), new Vec3(v[11], v[12], v[13]));
                Assert.AreEqual(v[14], l, 1e-12);
                Assert.AreEqual(v[15], S15Sky.LimbWeight(Math.Sqrt(v[0] * v[0] + v[1] * v[1])), 1e-12);
            }
        }

        [TestMethod]
        public void Palette_MatchesTheTwin()
        {
            foreach (string[] r in Lines("palette.tsv")) Assert.AreEqual(I(r[1]), S15Palette.Of(r[0]), r[0]);
            Assert.AreEqual(S7tBandTargets.SnNearWater, S15Palette.Row(1).Rgb, "DE_4546B is Subnautica's measured sea");
        }

        [TestMethod]
        public void Colour_Tolerance()
        {
            double dm, dl;
            Assert.IsTrue(S15Colour.Within(65, 97, 122, S7tBandTargets.SnNearWater, out dm, out dl));
            Assert.IsFalse(S15Colour.Within(65, 130, 122, S7tBandTargets.SnNearWater, out dm, out dl));
            Assert.AreEqual(33, dm);
            foreach (S15Conventions.OrbitRow o in S15Conventions.Orbit) { Assert.AreEqual(S15Conventions.Ending.Sea, o.Centre); Assert.AreEqual(S15Conventions.Ending.Limb, o.Limb); }
        }
    }
}
