using System;
using System.Collections.Generic;
using System.Linq;
using DeepEngineers.SE;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using EvType = DeepEngineers.Wire.S7Conventions.EventType;
using EvFlags = DeepEngineers.Wire.S7Conventions.EventFlags;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S7 effect events through a real named section (CONTRACT §5.4 "C2 effect events"): B2's
 /// <c>Link.WriteSeEntities</c> overload writes the light rows, the event window (0x40000) and the header extension
 /// (<c>pad_1c</c>) in the C2 seqlock window; Subnautica's reader half (<see cref="S7EventReader"/>, as the C2 table
 /// reader drives it) applies every event once in seq order across overlapping windows; a later S5-only write leaves a
 /// stale extension that the reader ignores; counts beyond the tables are refused.
 /// </summary>
    [TestClass]
    public unsafe class S7EventsLinkTests
    {
        public TestContext TestContext { get; set; }

        [TestMethod]
        public void Link_C2WithLightsEventsAndExtension_ReaderAppliesEachSeqOnce()
        {
            using (var rig = new Rig(TestContext))
            {
                string name = Rig.Name();
                var link = new Link(() => true, name, Rig.MutexName());
                var input = Rig.Input(SePhase.InWorld);
                CreatorResult cr = rig.CreateSn(name, Rig.SelfPid);
                using (SharedSection sn = cr.Section)
                {
                    HeaderView hv = sn.Header;
                    hv.SnPhase = SnPhase.ReadyIdle;
                    rig.Run(link, ref input, 1100, hv);
                    Assert.AreEqual(LinkState.Up, link.State);
                    byte* c2 = Layout.RegionPtr(sn.Base, RegionId.C2SeEntities);

                    var window = new S7EventWindow();
                    window.SetStream(S7EventRules.Stream(link.SeGeneration, 1));
                    var reader = new S7EventReader();
                    var rows = new S7EventRow[S7Conventions.C2Events.MaxRows];
                    var lights = new SeLight[SeEntitiesLimits.MaxLights];
                    lights[0] = new SeLight { ownerId = 55, range = 7f, intensity = 2.5f, flags = SeLightFlags.On | S7Conventions.C2LightFlagsExt.Version1 };
                    lights[1] = new SeLight { ownerId = 0, range = 3f, intensity = 1f, flags = SeLightFlags.On | SeLightFlags.Spot | S7Conventions.C2LightFlagsExt.Version1, coneDeg = 40f };
                    var entity = new SeEntity { entityId = 55, kind = SeEntityKind.RootGrid, groupId = 55, q = new F32x4(0, 0, 0, 1), s = 1, k = 1 };
                    var got = new List<S7EventRow>();
                    for (ulong tick = 1000; tick < 1030; tick++)
                    {
                        window.TryAdd(new S7EventRow { type = EvType.WeaponMuzzle, pos = new F32x3(1, 2, 3), dir = new F32x3(0, 0, -1), intensity = 1, size = 1, entityId = 55, flags = EvFlags.Attached | EvFlags.Underwater });
                        window.TryAdd(new S7EventRow { type = EvType.BulletStreak, pos = new F32x3(1, 2, 3), dir = new F32x3(0, 0, -1), intensity = 0.125f, size = 60, lifetimeMs = 150 });
                        window.EndTick(tick);
                        int n = window.CopyRows(rows);
                        C2Ext ext = window.Extension(tick, n, S7Conventions.C2ExtFlags.Lights, 2);
                        Assert.IsTrue(link.WriteSeEntities(tick, 1, new[] { entity }, 1, null, 0, lights, 2, rows, n, ext));
                        if (tick % 3 == 0) Read(c2, reader, got); // Subnautica reads every third tick: the window covers it
                    }
                    Read(c2, reader, got);
                    Assert.AreEqual(60, got.Count, "every event once");
                    CollectionAssert.AreEqual(Enumerable.Range(1, 60).Select(i => (uint)i).ToList(), got.Select(r => r.seq).ToList());
                    Assert.AreEqual(0, reader.Missed);
                    Assert.AreEqual(30, got.Count(r => r.type == EvType.WeaponMuzzle && r.Attached && r.entityId == 55));
                    Assert.AreEqual(S7Conventions.C2Ext.Version, reader.LastExt.version);
                    Assert.AreEqual(2u, reader.LastExt.lightsFound);
                    Assert.IsTrue(reader.LastExt.HasLights && reader.LastExt.HasEvents);

                    var hdr = *(SeEntitiesHeader*)c2;
                    Assert.AreEqual(2u, hdr.lightCount);
                    var l1 = *(SeLight*)(c2 + SeEntitiesLimits.LightsOffset + SeEntitiesLimits.LightSize);
                    Assert.AreEqual(40f, l1.coneDeg);
                    C2Ext back;
                    Assert.IsTrue(C2Ext.TryRead(c2, hdr.tick, out back));
                    Assert.AreEqual((ushort)16, back.eventCount, "8 ticks x 2 rows");

                    // the S5 overload afterwards: the extension is stale (tickLow of the older write) and ignored
                    Assert.IsTrue(link.WriteSeEntities(2000, 1, new[] { entity }, 1, null, 0));
                    long queued = reader.Queued;
                    Read(c2, reader, got);
                    Assert.AreEqual(queued, reader.Queued);
                    Assert.AreEqual(1, reader.StaleExtension);
                    Assert.IsFalse(C2Ext.TryRead(c2, 2000, out back));

                    // the extension's count and tickLow come from the call, whatever the given extension says
                    window.TryAdd(new S7EventRow { type = EvType.Explosion, pos = new F32x3(0, -5, 0), size = 10, intensity = 1, lifetimeMs = 3000 });
                    window.EndTick(2001);
                    int m = window.CopyRows(rows);
                    C2Ext wrong = C2Ext.For(5, S7Conventions.C2ExtFlags.Events, 0, window.FirstSeq, window.Stream, 0, 0);
                    Assert.IsTrue(link.WriteSeEntities(2001, 1, new[] { entity }, 1, null, 0, null, 0, rows, m, wrong));
                    Assert.IsTrue(C2Ext.TryRead(c2, 2001, out back));
                    Assert.AreEqual((ushort)m, back.eventCount);
                    Read(c2, reader, got);
                    Assert.AreEqual(EvType.Explosion, got.Last().type);
                    Assert.AreEqual(0, reader.Missed);

                    try { link.WriteSeEntities(1, 1, new SeEntity[0], 0, null, 0, lights, 65, rows, 0, wrong); Assert.Fail("lights beyond the table refused"); }
                    catch (ArgumentOutOfRangeException) { }
                    try { link.WriteSeEntities(1, 1, new SeEntity[0], 0, null, 0, null, 0, rows, 1025, wrong); Assert.Fail("events beyond the window refused"); }
                    catch (ArgumentOutOfRangeException) { }
                }
            }
        }

        /// <summary>The C2 table reader's order: the copy inside the seqlock window, the commit after a coherent read.</summary>
        private static void Read(byte* c2, S7EventReader reader, List<S7EventRow> got)
        {
            for (int attempt = 0; attempt < 16; attempt++)
            {
                uint seq;
                if (!Seqlock.ReadBegin(c2, out seq)) continue;
                reader.CopyInWindow(c2);
                ulong tick = ((SeEntitiesHeader*)c2)->tick;
                if (!Seqlock.ReadEnd(c2, seq)) continue;
                reader.Commit(tick);
                break;
            }
            S7EventRow r;
            while (reader.TryDequeue(out r)) got.Add(r);
        }
    }
}
