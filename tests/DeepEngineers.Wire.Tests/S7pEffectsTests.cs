using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Contact = DeepEngineers.Wire.S7pFxConventions.DrillContact;
using EvFlags = DeepEngineers.Wire.S7Conventions.EventFlags;
using EvType = DeepEngineers.Wire.S7Conventions.EventType;
using Knob = DeepEngineers.Wire.S7pFxSettings.Knob;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S7p-effects-under (ROADMAP "### S7p Polish pass" P2–P6a; CONTRACT §5.4 "S7p effects-under"): the canonical text
 /// <c>ext s7p fx 1</c> and its pin, the row's free bits (colour in 0x3E, length scale and contact in the subtype's high
 /// byte, the S7 low byte intact), the values surviving the window, the sustain tracker and the reader replay-exact,
 /// SE's flame colour and length rules, the vapor plume and its caps, the layered explosion, the gun, the drill, the
 /// donor scoring and the live scales of <c>de.s7p fx</c>.
 /// </summary>
    [TestClass]
    public unsafe class S7pEffectsTests
    {
        // ------------------------------------------------------------------------------------------------ the pin

        [TestMethod]
        public void CanonicalText_PinnedHashLinesAndBytes()
        {
            string text = S7pFxConventions.CanonicalText();
            Assert.IsTrue(text.EndsWith("\n", StringComparison.Ordinal));
            Assert.IsTrue(text.StartsWith(S7pFxConventions.Header + "\n", StringComparison.Ordinal));
            Assert.AreEqual(S7pFxConventions.CanonicalLines, text.Count(c => c == '\n'), "lines");
            Assert.AreEqual(S7pFxConventions.CanonicalBytes, text.Length, "bytes");
            Assert.IsTrue(text.All(c => c < 128), "ASCII only");
            Assert.AreEqual("0x" + S7pFxConventions.PinnedHash.ToString("x16"), "0x" + S7pFxConventions.Hash.ToString("x16"), "ext s7p fx 1 hash (a changed line bumps the text and the CONTRACT pin)");
            Assert.AreEqual(LayoutHash.Fnv1a64Ascii(text), S7pFxConventions.Hash);
        }

        [TestMethod]
        public void CanonicalText_ConstLinesSortedAndEqualToTheCode()
        {
            string[] c = S7pFxConventions.ConstLines();
            var sorted = c.Select(l => l.Split(' ')).OrderBy(p => p[1], StringComparer.Ordinal)
                          .ThenBy(p => int.Parse(p[3], CultureInfo.InvariantCulture)).ThenBy(p => p[2], StringComparer.Ordinal)
                          .Select(p => string.Join(" ", p)).ToArray();
            CollectionAssert.AreEqual(sorted, c);
            CollectionAssert.Contains(c, "const FlameExtra ClassShift 13");
            CollectionAssert.Contains(c, "const S7pLimits LengthScaleDefaultMilli 1150");
            CollectionAssert.Contains(c, "const S7pLimits StreakRayLayer 15");
            CollectionAssert.Contains(c, "const DrillContact Metal 4");
            string[] f = S7pFxConventions.FormatLines;
            CollectionAssert.AreEqual(f.OrderBy(l => l.Split(' ')[1], StringComparer.Ordinal).ToArray(), f, "format lines sorted by key");
        }

        [TestMethod]
        public void FieldLine_IsTheS7PadOfTheRow_LayoutAndS7PinUnchanged()
        {
            Assert.AreEqual(64, Marshal.SizeOf(typeof(S7EventRow)));
            Assert.AreEqual(0x3E, (int)Marshal.OffsetOf(typeof(S7EventRow), "extra"));
            Assert.AreEqual(0x3C, (int)Marshal.OffsetOf(typeof(S7EventRow), "subtype"));
            Assert.AreEqual("field event extra 0x3e 2 u16", S7pFxConventions.FieldLines.Single());
            Assert.AreEqual(16, S7pFxConventions.FlameExtra.ClassShift + S7pFxConventions.FlameExtra.ClassBits, "the class fills the top bits");
            Assert.AreEqual(S7pFxConventions.FlameExtra.ClassShift, S7pFxConventions.FlameExtra.RShift + S7pFxConventions.FlameExtra.RBits);
            Assert.AreEqual(S7pFxConventions.FlameExtra.RShift, S7pFxConventions.FlameExtra.GShift + S7pFxConventions.FlameExtra.GBits);
            Assert.AreEqual(S7pFxConventions.FlameExtra.GShift, S7pFxConventions.FlameExtra.BBits);
            CollectionAssert.Contains(S7Conventions.FieldLines(), "event pad 0x3e 2 u16", "the S7 text keeps its pad line (ext s7 look 1 unchanged)");
            Assert.AreEqual(S7Conventions.PinnedHash, S7Conventions.Hash, "ext s7 look 1 unchanged");
        }

        // ------------------------------------------------------------------------------------------------ the row's bits

        [TestMethod]
        public void Flame_RoundTrip_ColourWithinHalfAStep_ClassOfTheLengthScale_ZeroIsAbsent()
        {
            float r, g, b, ls;
            Assert.IsFalse(S7pFxCodec.TryDecodeFlame(0, out r, out g, out b, out ls), "0 = an older SE");
            Assert.AreEqual(1.15f, ls, 1e-6, "absent: the default scale");
            var rnd = new Random(7);
            for (int i = 0; i < 500; i++)
            {
                float a = (float)rnd.NextDouble(), c = (float)rnd.NextDouble(), d = (float)rnd.NextDouble();
                float scale = 0.8f + (float)rnd.NextDouble();
                ushort v = S7pFxCodec.EncodeFlame(a, c, d, scale);
                Assert.IsTrue(S7pFxCodec.TryDecodeFlame(v, out r, out g, out b, out ls), "never absent once encoded");
                Assert.AreEqual(a, r, 0.5 / 15 + 1e-6);
                Assert.AreEqual(c, g, 0.5 / 31 + 1e-6);
                Assert.AreEqual(d, b, 0.5 / 15 + 1e-6);
                Assert.AreEqual(Math.Max(0.875, Math.Min(1.625, scale)), ls, 0.0625 + 1e-6, "within half a class");
            }
            Assert.AreEqual((3 << 13) | (15 << 9) | (0 << 4) | 15, S7pFxCodec.EncodeFlame(2f, float.NaN, 1f, float.NaN), "clamped, NaN = 0, a bad scale = the default's class");
            Assert.AreEqual(3 << 13, S7pFxCodec.EncodeFlame(-1f, 0f, 0f, 1.15f), "black still carries a flame");
            Assert.AreEqual(3, S7pFxCodec.LengthClass(1.15f));
            Assert.AreEqual(5, S7pFxCodec.LengthClass(1.4f));
            Assert.AreEqual(6, S7pFxCodec.LengthClass(1.5f));
            Assert.AreEqual(1, S7pFxCodec.LengthClass(0.1f));
            Assert.AreEqual(7, S7pFxCodec.LengthClass(9f));
            Assert.AreEqual(1.125f, S7pFxCodec.LengthScaleOfClass(3), 1e-6);
            Assert.AreEqual(1.5f, S7pFxCodec.LengthScaleOf(S7pFxCodec.EncodeFlame(1f, 1f, 1f, 1.5f)), 1e-6);
            Assert.AreEqual(1.15f, S7pFxCodec.LengthScaleOf(0), 1e-6);
        }

        [TestMethod]
        public void FlameColour_IsSesLerpOverMaxThrust()
        {
            float r, g, b;
            // hydrogen, the vanilla definitions: idle (1, 0.85, 0.3), full (1, 0.75, 0.2); MAX_THRUST 1.5
            S7pFxCodec.FlameColour(1f, 0.85f, 0.3f, 1f, 0.75f, 0.2f, 0f, 1.5f, out r, out g, out b);
            Assert.AreEqual(0.85f, g, 1e-6); Assert.AreEqual(0.3f, b, 1e-6);
            S7pFxCodec.FlameColour(1f, 0.85f, 0.3f, 1f, 0.75f, 0.2f, 1.5f, 1.5f, out r, out g, out b);
            Assert.AreEqual(0.75f, g, 1e-6); Assert.AreEqual(0.2f, b, 1e-6);
            S7pFxCodec.FlameColour(1f, 0.85f, 0.3f, 1f, 0.75f, 0.2f, 1f, 1.5f, out r, out g, out b);
            Assert.AreEqual(0.85f - 0.1f * (1f / 1.5f), g, 1e-6, "strength 1 = two thirds of the way");
            S7pFxCodec.FlameColour(1f, 0.85f, 0.3f, 1f, 0.75f, 0.2f, 9f, 1.5f, out r, out g, out b);
            Assert.AreEqual(0.75f, g, 1e-6, "clamped at full");
            S7pFxCodec.FlameColour(0f, 0f, 0f, 1f, 1f, 1f, 0.75f, 0f, out r, out g, out b);
            Assert.AreEqual(0.5f, r, 1e-6, "a bad MAX_THRUST falls back to 1.5");
            // the kind's vanilla colour (no colour on the wire)
            S7pFxCodec.KindColour(S7Conventions.ThrusterKind.Ion, 1f, out r, out g, out b);
            Assert.AreEqual(0.6505882f, b, 1e-6);
            S7pFxCodec.KindColour(S7Conventions.ThrusterKind.Hydrogen, 0f, out r, out g, out b);
            Assert.AreEqual(0.85f, g, 1e-6, "hydrogen idle");
        }

        [TestMethod]
        public void Subtype_DrillsCarryTheContact_LowByteKeepsTheS7Meaning()
        {
            for (byte c = 0; c <= 5; c++)
            {
                ushort st = S7pFxCodec.ToolSubtype(S7Conventions.ToolSource.Ship, c);
                Assert.AreEqual(S7Conventions.ToolSource.Ship, S7pFxCodec.Low(st));
                Assert.AreEqual(c, S7pFxCodec.ContactOf(st));
            }
            Assert.AreEqual(S7Conventions.ToolSource.Hand, S7pFxCodec.ToolSubtype(S7Conventions.ToolSource.Hand, Contact.None), "no contact: the S7 subtype exactly");
            Assert.AreEqual(Contact.None, S7pFxCodec.ContactOf(S7Conventions.ToolSource.Hand), "S7 rows read contact None");
            foreach (ushort kind in new[] { S7Conventions.ThrusterKind.Other, S7Conventions.ThrusterKind.Ion, S7Conventions.ThrusterKind.Atmospheric, S7Conventions.ThrusterKind.Hydrogen })
                Assert.AreEqual(kind, S7pFxCodec.Low(kind), "a thruster's subtype is the S7 kind (the S7 and S7p readers switch on it)");
        }

        [TestMethod]
        public void ContactOfMaterial_Classes()
        {
            Assert.AreEqual(Contact.Rock, S7pFxCodec.ContactOfMaterial(null));
            Assert.AreEqual(Contact.Rock, S7pFxCodec.ContactOfMaterial("Rock"));
            Assert.AreEqual(Contact.Rock, S7pFxCodec.ContactOfMaterial("Iron_01"), "ores are rock-like");
            Assert.AreEqual(Contact.Ice, S7pFxCodec.ContactOfMaterial("Ice"));
            Assert.AreEqual(Contact.Ice, S7pFxCodec.ContactOfMaterial("Snow"));
            Assert.AreEqual(Contact.Sediment, S7pFxCodec.ContactOfMaterial("Sand"));
            Assert.AreEqual(Contact.Sediment, S7pFxCodec.ContactOfMaterial("Grass"));
            Assert.AreEqual(Contact.Sediment, S7pFxCodec.ContactOfMaterial("soil"));
            Assert.AreEqual("Metal", Contact.Name(Contact.Metal));
        }

        [TestMethod]
        public void StreakHit_InCentimetres_TheTracerStopsAtTheHitAtTheBulletsSpeed()
        {
            Assert.AreEqual(0, S7pFxCodec.EncodeHitCm(0f), "no hit");
            Assert.AreEqual(0, S7pFxCodec.EncodeHitCm(float.NaN));
            Assert.AreEqual(1, S7pFxCodec.EncodeHitCm(0.001f), "a hit at the muzzle is still a hit");
            Assert.AreEqual(2345, S7pFxCodec.EncodeHitCm(23.45f));
            Assert.AreEqual(65535, S7pFxCodec.EncodeHitCm(1e6f));
            float len;
            ushort ms;
            // a 60 m S7 streak at 400 m/s (150 ms), a hit at 20 m: 20 m in 50 ms
            Assert.IsTrue(S7pFxCodec.StreakTravel(60f, 150, S7pFxCodec.EncodeHitCm(20f), out len, out ms));
            Assert.AreEqual(20f, len, 1e-4);
            Assert.AreEqual(50, ms);
            Assert.IsTrue(S7pFxCodec.StreakTravel(60f, 150, 0, out len, out ms), "no hit: the whole S7 length");
            Assert.AreEqual(60f, len, 1e-6);
            Assert.AreEqual(150, ms);
            Assert.IsTrue(S7pFxCodec.StreakTravel(30f, 75, S7pFxCodec.EncodeHitCm(45f), out len, out ms), "a hit beyond the S7 length is cut to it");
            Assert.AreEqual(30f, len, 1e-6);
            Assert.IsFalse(S7pFxCodec.StreakTravel(0f, 75, 0, out len, out ms));
            Assert.IsFalse(S7pFxCodec.StreakTravel(30f, 0, 0, out len, out ms));
            Assert.IsTrue(S7pFxCodec.StreakTravel(60f, 150, 1, out len, out ms));
            Assert.AreEqual(1, ms, "at least 1 ms");
        }

        [TestMethod]
        public void Quantise_AndMuzzleSizes()
        {
            Assert.AreEqual(1.25f, S7pFxCodec.Quantise(1.2f, 0.25f), 1e-6);
            Assert.AreEqual(-0.5f, S7pFxCodec.Quantise(-0.6f, 0.25f), 1e-6);
            Assert.AreEqual(1.2f, S7pFxCodec.Quantise(1.2f, 0f), 1e-6);
            Assert.AreEqual(1f, S7pFxCodec.MuzzleSize(true, true), 1e-6);
            Assert.AreEqual(0.5f, S7pFxCodec.MuzzleSize(true, false), 1e-6);
            Assert.AreEqual(0.3f, S7pFxCodec.MuzzleSize(false, false), 1e-6);
        }

        // ------------------------------------------------------------------------------------------------ replay-exact

        [TestMethod]
        public void ColouredRows_SurviveTheWindowTheTrackerAndTheReader()
        {
            var tracker = new S7SustainTracker();
            var window = new S7EventWindow();
            window.SetStream(0x10001);
            ushort c0 = S7pFxCodec.EncodeFlame(1f, 0.85f, 0.3f, 1.15f), c1 = S7pFxCodec.EncodeFlame(1f, 0.75f, 0.2f, 1.15f);
            ushort st = S7Conventions.ThrusterKind.Hydrogen;
            var sent = new List<S7EventRow>();
            var stops = new S7EventRow[8];
            for (ulong tick = 100; tick < 140; tick++)
            {
                tracker.BeginTick();
                if (tick < 130)
                {
                    float strength = tick < 115 ? 0.2f : 1f;
                    var r = new S7EventRow
                    {
                        type = EvType.Thruster, flags = (byte)(EvFlags.Attached | EvFlags.Underwater), entityId = 42, key = 7,
                        pos = new F32x3(1, 2, 3), dir = new F32x3(0, 0, 1), intensity = strength, size = 0.8f, subtype = st,
                        extra = tick < 115 ? c0 : c1,
                    };
                    if (tracker.Observe(ref r, tick) != SustainEmit.None) window.TryAdd(r);
                }
                int n = tracker.CollectStops(tick, stops);
                for (int i = 0; i < n; i++) window.TryAdd(stops[i]);
                window.EndTick(tick);
                var rows = new S7EventRow[1024];
                int count = window.CopyRows(rows);
                for (int i = 0; i < count; i++) if (!sent.Any(s => s.seq == rows[i].seq)) sent.Add(rows[i]);
            }
            Assert.AreEqual(3, sent.Count, "start (100), the step at 115, the stop (130)");
            Assert.IsTrue(sent.All(s => s.subtype == st), "the subtype rides every row, the Stop row too");
            Assert.AreEqual(c0, sent.First().extra);
            Assert.AreEqual(c1, sent.First(s => s.tickLow == 115).extra, "the step row carries the new colour");
            Assert.IsTrue(sent.Last().IsStop);
            Assert.AreEqual(c1, sent.Last().extra, "the stop row keeps the last colour");

            // through a real C2-sized buffer and the Subnautica reader
            byte[] region = new byte[0x50000];
            var reader = new S7EventReader();
            fixed (byte* p = region)
            {
                var all = sent.ToArray();
                for (int i = 0; i < all.Length; i++) ((S7EventRow*)(p + S7Conventions.C2Events.Offset))[i] = all[i];
                C2Ext.Write(p, C2Ext.For(139, S7Conventions.C2ExtFlags.Events, all.Length, all[0].seq, 0x10001, 0, 0));
                reader.CopyInWindow(p);
            }
            reader.Commit(139);
            var got = new List<S7EventRow>();
            S7EventRow row;
            while (reader.TryDequeue(out row)) got.Add(row);
            Assert.AreEqual(sent.Count, got.Count);
            for (int i = 0; i < got.Count; i++)
            {
                Assert.AreEqual(sent[i].extra, got[i].extra);
                Assert.AreEqual(sent[i].subtype, got[i].subtype);
            }
        }

        // ------------------------------------------------------------------------------------------------ P2 flame

        [TestMethod]
        public void FlameLength_IsSesOwnLengthLinearInStrength()
        {
            Assert.AreEqual(0f, S7pEffectRules.FlameLength(0f, 1f, 1.15f, 1f));
            Assert.AreEqual(9.5f * 1.15f * 0.8f, S7pEffectRules.FlameLength(1f, 0.8f, 1.15f, 1f), 1e-5);
            float half = S7pEffectRules.FlameLength(0.5f, 0.8f, 1.15f, 1f), full = S7pEffectRules.FlameLength(1f, 0.8f, 1.15f, 1f);
            Assert.AreEqual(full / 2f, half, 1e-5, "linear");
            Assert.AreEqual(full * 2f, S7pEffectRules.FlameLength(1f, 0.8f, 1.15f, 2f), 1e-5, "the flame knob");
            Assert.AreEqual(0f, S7pEffectRules.FlameLength(1f, 0.8f, 1.15f, 0f), "knob 0 = off");
            Assert.AreEqual(S7pEffectRules.FlameLength(1f, 0.8f, 1.15f, 1f), S7pEffectRules.FlameLength(1f, 0.8f, 0f, 1f), 1e-5, "absent scale = 1.15");
            Assert.AreEqual(full / S7pEffectRules.FlameLifetimeS, S7pEffectRules.FlameSpeed(full), 1e-4);
            Assert.AreEqual(1.6f, S7pEffectRules.FlameDiameter(0.8f, 1f), 1e-6);
        }

        [TestMethod]
        public void FlameRate_ZeroAtRest_MonotoneAndCapped_LightAndShimmer()
        {
            Assert.AreEqual(0f, S7pEffectRules.FlameRate(0f, 1f, 1f));
            Assert.AreEqual(0f, S7pEffectRules.FlameRate(1f, 1f, 0f));
            float prev = 0f;
            for (int i = 1; i <= 10; i++)
            {
                float r = S7pEffectRules.FlameRate(i / 10f, 1f, 1f);
                Assert.IsTrue(r > prev);
                prev = r;
            }
            Assert.AreEqual(S7pEffectRules.FlameMaxRate, S7pEffectRules.FlameRate(1f, 50f, 1f), 1e-3);
            float li, lr;
            //: 0.15 + 0.35·i, range clamp(0.8·L + 1, 1.
            S7pEffectRules.FlameLight(1f, 10f, 1f, out li, out lr);
            Assert.AreEqual(0.5f, li, 1e-6);
            Assert.AreEqual(9f, lr, 1e-6);
            S7pEffectRules.FlameLight(1f, 100f, 0f, out li, out lr);
            Assert.AreEqual(0f, li);
            Assert.AreEqual(12f, lr, 1e-6);
            Assert.AreEqual(25f, S7pEffectRules.ShimmerRate(100f, 1f), 1e-6);
            Assert.AreEqual(0f, S7pEffectRules.ShimmerRate(100f, 0f));
            float or, og, ob, oa;
            S7pEffectRules.Glow(0.5f, 0.6f, 0.9f, 2f, out or, out og, out ob, out oa);
            Assert.AreEqual(1f, or, 1e-6); Assert.AreEqual(1f, ob, 1e-6); Assert.AreEqual(1f, oa, 1e-6);
            S7pEffectRules.Glow(0.5f, 0.6f, 0.9f, 0.5f, out or, out og, out ob, out oa);
            Assert.AreEqual(0.25f, or, 1e-6); Assert.AreEqual(0.5f, oa, 1e-6);
        }

        // ------------------------------------------------------------------------------------------------ P3 plume

        [TestMethod]
        public void Plume_FlameCrossingTheSurface_AtTheCrossing()
        {
            // a ship 2 m above the sea, flame pointing straight down, length 4 m
            S7pPlume h = S7pEffectRules.Plume(10f, 2f, -5f, 0f, -1f, 0f, 4f, 1f, 0.5f, S7pEffectRules.PlumeBandM);
            Assert.IsTrue(h.Hit);
            Assert.AreEqual(0f, h.Distance);
            Assert.AreEqual(2f, h.T, 1e-6);
            Assert.AreEqual(10f, h.X, 1e-6); Assert.AreEqual(-5f, h.Z, 1e-6);
            Assert.IsFalse(h.FromBelow);
            float reach = 4f * S7pEffectRules.PlumeReachFactor + 0.5f;
            Assert.AreEqual(1f - 0.5f * 2f / reach, h.Density, 1e-5);
            // slanted: 45° down from 1 m above: crossing 1 m along x
            float s = (float)Math.Sqrt(0.5);
            h = S7pEffectRules.Plume(0f, 1f, 0f, s, -s, 0f, 4f, 1f, 0.5f, 4f);
            Assert.IsTrue(h.Hit);
            Assert.AreEqual(1f, h.X, 1e-5);
            // under water, pointing up to the surface
            h = S7pEffectRules.Plume(0f, -1.5f, 0f, 0f, 1f, 0f, 2f, 0.8f, 0.5f, 4f);
            Assert.IsTrue(h.Hit && h.FromBelow && h.Distance == 0f);
        }

        [TestMethod]
        public void Plume_NearButNotCrossing_DensityFallsWithDistance_NoneBeyondTheBand()
        {
            // horizontal flames at depths 0.5..6 m: the nearest end is the nozzle itself
            float prev = 2f;
            for (float depth = 0.5f; depth <= 3.01f; depth += 0.5f)
            {
                S7pPlume h = S7pEffectRules.Plume(0f, -depth, 0f, 1f, 0f, 0f, 3f, 1f, 0.5f, 4f);
                Assert.IsTrue(h.Hit, "within the band at " + depth);
                Assert.AreEqual(depth, h.Distance, 1e-5);
                Assert.AreEqual(0f, h.T);
                Assert.IsTrue(h.Density < prev, "falls with the distance");
                prev = h.Density;
            }
            Assert.IsFalse(S7pEffectRules.Plume(0f, -3.5f, 0f, 1f, 0f, 0f, 3f, 1f, 0.5f, 4f).Hit, "too thin near the band edge (below PlumeMinDensity)");
            Assert.IsFalse(S7pEffectRules.Plume(0f, -4.5f, 0f, 1f, 0f, 0f, 3f, 1f, 0.5f, 4f).Hit, "beyond the band");
            Assert.IsFalse(S7pEffectRules.Plume(0f, -1f, 0f, 1f, 0f, 0f, 3f, 0f, 0.5f, 4f).Hit, "a cold flame");
            Assert.IsFalse(S7pEffectRules.Plume(0f, 30f, 0f, 0f, -1f, 0f, 4f, 1f, 0.5f, 4f).Hit, "a ship high above");
            // pointing away (up) from 1 m above: the nozzle end is nearest
            S7pPlume up = S7pEffectRules.Plume(0f, 1f, 0f, 0f, 1f, 0f, 4f, 1f, 0.5f, 4f);
            Assert.IsTrue(up.Hit);
            Assert.AreEqual(1f, up.Distance, 1e-6);
            // stronger thrust, denser vapor
            Assert.IsTrue(S7pEffectRules.Plume(0f, -1f, 0f, 1f, 0f, 0f, 3f, 1f, 0.5f, 4f).Density >
                          S7pEffectRules.Plume(0f, -1f, 0f, 1f, 0f, 0f, 3f, 0.3f, 0.5f, 4f).Density);
            Assert.AreEqual(0f, S7pEffectRules.PlumeRate(0.5f, 1f, 0f));
            Assert.AreEqual(0.5f * 50f * 2f, S7pEffectRules.PlumeRate(0.5f, 1f, 1f), 1e-5);
            Assert.AreEqual(4f, S7pEffectRules.PlumeSize(100f, 1f), 1e-6);   // 4 m at most since
        }

        [TestMethod]
        public void SelectPlumes_PerEntityAndTotalCaps_StrongestFirst_Deterministic()
        {
            var density = new float[] { 0.2f, 0.9f, 0.5f, 0.7f, 0.1f, 0.8f, 0.3f, 0f };
            var entity = new ulong[] { 1, 1, 1, 1, 2, 2, 3, 3 };
            var take = new bool[8];
            int kept = S7pEffectRules.SelectPlumes(density, entity, 8, 3, 12, take);
            Assert.AreEqual(6, kept, "entity 1: 3 of 4; entity 2: 2; entity 3: 1 (a zero density is none)");
            CollectionAssert.AreEqual(new[] { false, true, true, true, true, true, true, false }, take);
            kept = S7pEffectRules.SelectPlumes(density, entity, 8, 3, 2, take);
            Assert.AreEqual(2, kept);
            CollectionAssert.AreEqual(new[] { false, true, false, false, false, true, false, false }, take, "the two strongest");
            var tie = new float[] { 0.5f, 0.5f, 0.5f };
            var te = new ulong[] { 9, 9, 9 };
            var tt = new bool[3];
            S7pEffectRules.SelectPlumes(tie, te, 3, 2, 12, tt);
            CollectionAssert.AreEqual(new[] { true, true, false }, tt, "ties by index");
        }

        // ------------------------------------------------------------------------------------------------ P4 explosion

        [TestMethod]
        public void Explosion_ScalesWithTheRadius_Bounded_KnobZeroLeavesTheS7Look()
        {
            S7pExplosionRecipe small = S7pEffectRules.Explosion(2f, 0.5f, true, 1f);
            S7pExplosionRecipe big = S7pEffectRules.Explosion(20f, 0.5f, true, 1f);
            Assert.IsFalse(small.Destruction, "small: the crashfish detonation");
            Assert.IsTrue(big.Destruction, "large: the Seamoth destruction effect");
            Assert.AreEqual(20f / S7pEffectRules.DestructionRadiusM, big.PrefabScale, 1e-5);
            Assert.AreEqual(S7EventRules.ExplosionScale(2f, false), small.PrefabScale, 1e-6);
            Assert.IsTrue(big.FireballCount > small.FireballCount && big.FireballSize > small.FireballSize);
            Assert.IsTrue(big.RingRadius > small.RingRadius && big.FlashRange > small.FlashRange);
            Assert.AreEqual(40f, big.RingRadius, 1e-4, "the shock ring reaches twice the radius");
            Assert.AreEqual(big.RingRadius / big.RingLife, big.RingSpeed, 1e-3);
            Assert.IsTrue(big.BubbleCount > 0 && small.BubbleCount > 0, "under water: bubbles");
            S7pExplosionRecipe air = S7pEffectRules.Explosion(20f, 0.5f, false, 1f);
            Assert.AreEqual(0, air.BubbleCount, "no bubbles in air");
            Assert.IsTrue(air.FireballLife > big.FireballLife, "the fire is quenched under water");
            Assert.IsTrue(air.SparkSpeed > big.SparkSpeed, "water slows the debris");
            S7pExplosionRecipe huge = S7pEffectRules.Explosion(1e6f, 1f, true, 10f);
            Assert.IsTrue(huge.FireballCount <= 160 && huge.SmokeCount <= 200 && huge.RingCount <= 96 && huge.SparkCount <= 120 && huge.BubbleCount <= 200);
            Assert.IsTrue(huge.FlashRange <= 120f && huge.FlashIntensity <= 12f);
            S7pExplosionRecipe off = S7pEffectRules.Explosion(20f, 0.5f, true, 0f);
            Assert.AreEqual(0, off.FireballCount + off.SmokeCount + off.RingCount + off.SparkCount + off.BubbleCount);
            Assert.IsTrue(off.FlashIntensity > 0 && off.Destruction, "the flash and the prefab choice stay");
            S7pExplosionRecipe bad = S7pEffectRules.Explosion(float.NaN, float.NaN, true, 1f);
            Assert.IsTrue(bad.FireballCount > 0 && !float.IsNaN(bad.FireballSize));
        }

        // ------------------------------------------------------------------------------------------------ P5 gun

        [TestMethod]
        public void Gun_TracerFliesMuzzleToHitAtTheBulletSpeed_BubbleStreakUnderWaterOnly()
        {
            // a 30 m shot at 400 m/s: SE's lifetime 75 ms
            S7pGunRecipe g = S7pEffectRules.Gun(1f, 30f, 75, true, 1f, 1f, 1f, 1f);
            Assert.AreEqual(400f, g.TracerSpeed, 1e-3);
            Assert.AreEqual(0.075f, g.TracerLife, 1e-6);
            Assert.AreEqual(4f, g.TracerStretch, 1e-4);
            Assert.AreEqual(48, g.StreakBubbles, "30 m / 0.6 m = 50, capped 48");
            Assert.IsTrue(g.FlashSize > 0 && g.FlashCount > 0 && g.MuzzleSparks > 0 && g.MuzzleBubbles > 0 && g.LightIntensity > 0);
            S7pGunRecipe air = S7pEffectRules.Gun(1f, 30f, 75, false, 1f, 1f, 1f, 1f);
            Assert.AreEqual(0, air.StreakBubbles);
            Assert.AreEqual(0, air.MuzzleBubbles);
            Assert.IsTrue(air.LightIntensity > g.LightIntensity, "the flash is dimmer under water");
            S7pGunRecipe hand = S7pEffectRules.Gun(0.3f, 6f, 15, true, 1f, 1f, 1f, 1f);
            Assert.IsTrue(hand.FlashSize < g.FlashSize);
            Assert.AreEqual(10, hand.StreakBubbles);
            S7pGunRecipe off = S7pEffectRules.Gun(1f, 30f, 75, true, 0f, 0f, 0f, 0f);
            Assert.AreEqual(0f, off.FlashSize + off.TracerSpeed + off.LightIntensity);
            Assert.AreEqual(0, off.StreakBubbles + off.MuzzleSparks + off.MuzzleBubbles);
            S7pGunRecipe slow = S7pEffectRules.Gun(1f, 60f, 1000, true, 1f, 1f, 1f, 1f);
            Assert.AreEqual(1.2f, slow.TracerStretch, 1e-6, "a short streak for a slow round");
            int sp, bu;
            S7pEffectRules.Impact(1, true, 1f, 1f, out sp, out bu);
            Assert.AreEqual(8, sp); Assert.AreEqual(6, bu);
            S7pEffectRules.Impact(2, false, 1f, 1f, out sp, out bu);
            Assert.AreEqual(4, sp); Assert.AreEqual(0, bu);
            S7pEffectRules.Impact(1, true, 1f, 0f, out sp, out bu);
            Assert.AreEqual(0, sp, "sparks knob 0");
        }

        // ------------------------------------------------------------------------------------------------ P6a drill

        [TestMethod]
        public void Drill_DustOnlyWhileTouching_SparksOnMetal_LightCapped()
        {
            Assert.AreEqual(0f, S7pEffectRules.DustRate(false, Contact.Rock, 1f, 1f));
            Assert.AreEqual(18f, S7pEffectRules.DustRate(true, Contact.Rock, 1f, 1f), 1e-6);
            Assert.AreEqual(26f, S7pEffectRules.DustRate(true, Contact.Sediment, 1f, 1f), 1e-6);
            Assert.AreEqual(0f, S7pEffectRules.DustRate(true, Contact.Metal, 1f, 1f), "metal: sparks, no dust");
            Assert.AreEqual(36f, S7pEffectRules.DustRate(true, Contact.Rock, 2f, 1f), 1e-6, "a bigger drill");
            Assert.AreEqual(30f, S7pEffectRules.SparkRate(true, Contact.Metal, 1f), 1e-6);
            Assert.AreEqual(0f, S7pEffectRules.SparkRate(true, Contact.Sediment, 1f));
            Assert.AreEqual(0f, S7pEffectRules.SparkRate(false, Contact.Metal, 1f));
            float r, g, b, a;
            S7pEffectRules.DustColour(Contact.Ice, out r, out g, out b, out a);
            Assert.IsTrue(b > r && a < 1f);
            float li, lr;
            S7pEffectRules.DrillLight(true, Contact.Metal, 10f, out li, out lr);
            Assert.AreEqual(S7pEffectRules.DrillLightMaxIntensity, li, 1e-6, "never blinding");
            Assert.IsTrue(lr <= S7pEffectRules.DrillLightMaxRange);
            S7pEffectRules.DrillLight(false, Contact.Metal, 1f, out li, out lr);
            Assert.AreEqual(0f, li);
            Assert.AreEqual(1.5f, S7pEffectRules.CapIntensity(8f, 1.5f));
            Assert.AreEqual(0f, S7pEffectRules.CapRange(float.NaN, 6f));
        }

        // ------------------------------------------------------------------------------------------------ donors

        [TestMethod]
        public void DonorScore_GlowAdditive_SmokeAndDustNeverAdditive_ShimmerNeedsADistortionShader()
        {
            var R = S7pEffectRules.DonorRole.Glow;
            Assert.AreEqual(0, S7pEffectRules.DonorScore(R, 0, "xFlare", "flare_mat", "UWE/Particles/UBER", "flare", false, false), "glow must be additive");
            int fire = S7pEffectRules.DonorScore(R, 0, "xFire", "fire", "Particles/Additive", "x_fire", true, false);
            int bubble = S7pEffectRules.DonorScore(R, 0, "xBubbles", "bubbles", "Particles/Additive", "bubble", true, false);
            Assert.IsTrue(fire > bubble);
            Assert.IsTrue(S7pEffectRules.DonorScore(R, 0, "a", "b", "c", "d", true, false) > S7pEffectRules.DonorScore(R, 5, "a", "b", "c", "d", true, false), "earlier sources first");
            Assert.AreEqual(0, S7pEffectRules.DonorScore(S7pEffectRules.DonorRole.Smoke, 0, "xSmoke", "smoke", "Particles/Additive", "smoke", true, false), "smoke never additive");
            Assert.AreEqual(0, S7pEffectRules.DonorScore(S7pEffectRules.DonorRole.Dust, 0, "xDust", "dust", "x", "dust", true, false), "dust never additive (the white-out)");
            Assert.IsTrue(S7pEffectRules.DonorScore(S7pEffectRules.DonorRole.Smoke, 0, "xSmoke", "smoke", "Particles/Alpha Blended", "smoke", false, false) >
                          S7pEffectRules.DonorScore(S7pEffectRules.DonorRole.Smoke, 0, "xBubbles", "bubbles", "Particles/Alpha Blended", "bubble", false, false));
            Assert.IsTrue(S7pEffectRules.DonorScore(S7pEffectRules.DonorRole.Spark, 0, "sparks", "spark", "add", "spark", true, true) >
                          S7pEffectRules.DonorScore(S7pEffectRules.DonorRole.Spark, 0, "glow", "glow", "add", "glow", true, false));
            Assert.AreEqual(0, S7pEffectRules.DonorScore(S7pEffectRules.DonorRole.Shimmer, 0, "xFire", "fire", "Particles/Additive", "fire", true, false));
            Assert.IsTrue(S7pEffectRules.DonorScore(S7pEffectRules.DonorRole.Shimmer, 3, "x", "y", "UWE/Particles/Distortion", "z", false, false) > 0);
            // the relaxed pass (blending unreadable): names only
            Assert.IsTrue(S7pEffectRules.DonorKeyword(S7pEffectRules.DonorRole.Glow, "xFlareCore", null, null));
            Assert.IsFalse(S7pEffectRules.DonorKeyword(S7pEffectRules.DonorRole.Glow, "xBubbles", "bubble", "bubble"));
            Assert.IsTrue(S7pEffectRules.DonorKeyword(S7pEffectRules.DonorRole.Smoke, null, "x_Steam", null));
            Assert.IsTrue(S7pEffectRules.DonorKeyword(S7pEffectRules.DonorRole.Dust, null, null, "sand_cloud"));
            Assert.IsTrue(S7pEffectRules.DonorKeyword(S7pEffectRules.DonorRole.Spark, "Sparks", null, null));
            Assert.IsFalse(S7pEffectRules.DonorKeyword(S7pEffectRules.DonorRole.Shimmer, "fire", "fire", "fire"));
        }

        // ------------------------------------------------------------------------------------------------ settings

        [TestMethod]
        public void Settings_DefaultsEnvAndCommand()
        {
            var s = new S7pFxSettings();
            Assert.AreEqual(1f, s[Knob.Flame]);
            Assert.AreEqual(0f, s[Knob.Above], "above water stays scene-above's by default");
            Assert.AreEqual(0f, s[Knob.DrillGame]);
            Assert.IsTrue(s.On(Knob.Colour) && s.On(Knob.Ray) && s.On(Knob.Contact));
            Assert.AreEqual("", s.Changed());
            Assert.IsNull(s.ApplyText("flame=1.5, plume=0;above=on"));
            Assert.AreEqual(1.5f, s[Knob.Flame]);
            Assert.AreEqual(0f, s[Knob.Plume]);
            Assert.AreEqual(1f, s[Knob.Above]);
            Assert.AreEqual("flame=1.5 plume=0 above=1", s.Changed());
            string err = s.ApplyText("nope=1,drill=abc,explosion=99,colour=2");
            Assert.IsNotNull(err);
            StringAssert.Contains(err, "unknown type 'nope'");
            StringAssert.Contains(err, "drill");
            StringAssert.Contains(err, "explosion");
            Assert.AreEqual(1f, s[Knob.Colour], "a switch: any value > 0 is on");
            Assert.AreEqual(2f, s[Knob.Explosion], "a refused value leaves the knob (default 2 since 2026-10-18)");
            string e;
            Assert.AreEqual("set", s.Command(new[] { "explosion", "2.5" }, out e));
            Assert.AreEqual(2.5f, s[Knob.Explosion]);
            Assert.AreEqual("set", s.Command(new[] { "vapor", "0.5" }, out e), "aliases");
            Assert.AreEqual(0.5f, s[Knob.Plume]);
            Assert.AreEqual("set", s.Command(new[] { "drill=0.4", "sparks=2" }, out e));
            Assert.AreEqual(0.4f, s[Knob.Drill]);
            Assert.AreEqual("status", s.Command(new string[0], out e));
            Assert.IsNull(s.Command(new[] { "flame" }, out e));
            StringAssert.Contains(e, "usage");
            Assert.IsNull(s.Command(new[] { "flame", "-1" }, out e));
            Assert.IsNull(s.Command(new[] { "warp", "1" }, out e));
            int v = s.Version;
            Assert.AreEqual("reset", s.Command(new[] { "reset" }, out e));
            Assert.IsTrue(s.Version > v);
            Assert.AreEqual("", s.Changed());
            StringAssert.StartsWith(s.Describe(), "flame=1 glow=0.15 shimmer=1 plume=0.5 explosion=2");   // glow 0.4 since
            Knob k;
            Assert.IsTrue(S7pFxSettings.TryParseKnob("COLOR", out k) && k == Knob.Colour);
            Assert.AreEqual("DEEPENGINEERS_S7P_FX", S7pFxSettings.EnvVar);
        }

        [TestMethod]
        public void Tokens_TheSharedVerbsForms_OnlyFxIsOurs()
        {
            bool mine;
            Func<object, string> T = a => { var t = S7pFxSettings.Tokens(a, out mine); return (mine ? "mine:" : "not:") + string.Join("|", t); };
            Assert.AreEqual("mine:flame|1.5", T(new Dictionary<string, object> { { "argv", new List<object> { "fx", "flame", "1.5" } } }), "/de sn s7p fx flame 1.5");
            Assert.AreEqual("not:", T(new Dictionary<string, object> { { "argv", new List<object> { "above", "se" } } }), "another feature's argv");
            Assert.AreEqual("mine:flame|1.5", T(new Dictionary<string, object> { { "fx", "flame" }, { "action", "1.5" } }), "SE chat /de s7p fx flame 1.5");
            Assert.AreEqual("mine:flame=1.5", T(new Dictionary<string, object> { { "action", "fx" }, { "flame", "1.5" } }), "SE chat /de s7p fx flame=1.5");
            Assert.AreEqual("mine:status", T(new Dictionary<string, object> { { "fx", "status" } }));
            Assert.AreEqual("mine:", T(new Dictionary<string, object> { { "action", "fx" } }), "bare: status");
            Assert.AreEqual("mine:flame=2|plume=0", T(new Dictionary<string, object> { { "fx", new Dictionary<string, object> { { "plume", 0.0 }, { "flame", 2.0 } } } }));
            Assert.AreEqual("mine:explosion|2.5", T(new Dictionary<string, object> { { "action", "fx" }, { "type", "explosion" }, { "scale", 2.5 } }));
            Assert.AreEqual("mine:above=1", T(new Dictionary<string, object> { { "fx", new Dictionary<string, object> { { "above", true } } } }));
            Assert.AreEqual("not:", T(new Dictionary<string, object> { { "above", "se" } }), "scene-above's key");
            Assert.AreEqual("not:", T(new Dictionary<string, object> { { "hold", "on" } }), "gameplay's key");
            Assert.AreEqual("not:", T(null));
            var s = new S7pFxSettings();
            string e;
            Assert.AreEqual("set", s.Command(S7pFxSettings.Tokens(new Dictionary<string, object> { { "fx", "flame" }, { "action", "1.5" } }, out mine), out e));
            Assert.AreEqual(1.5f, s[Knob.Flame]);
        }
    }
}
