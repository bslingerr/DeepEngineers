using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// The S3 review fixes of on the SE side's game-independent code: the consumer audit that resends when
 /// Subnautica's C7 counters stay short of what SE sent with both rings drained (finding 5: a corrupt-framing re-sync
 /// on the Subnautica side otherwise loses records for good).
 /// </summary>
    [TestClass]
    public class S3ReviewFixTests
    {
        // one healthy stream: 1 grid, 1 part, 1000 B of C3 written (payload + 8-B headers), Subnautica drained them all
        private static string Tick(ConsumerAudit a, ulong now, ulong snFrame, ulong drained, long written = 1000, uint ents = 1, uint pending = 0,
                                   bool idle = true, bool ringsDrained = true, uint grids = 1, uint expectPending = 0)
        {
            return a.Tick(now, idle, ringsDrained, written, grids, expectPending, snFrame, drained, ents, pending);
        }

        [TestMethod]
        public void ConsumerAudit_HealthyStream_NeverResends()
        {
            var a = new ConsumerAudit();
            ulong f = 100;
            for (ulong t = 0; t < 30000; t += 16, f++)
                Assert.IsNull(Tick(a, 1000 + t, f, 5000, written: 5000), "counters agree");
            Assert.AreEqual(0, a.Resends);
            Assert.IsTrue(a.Checks > 0, "it did check");
            // SE writes more later (a block added): Subnautica drains it too
            Assert.IsNull(Tick(a, 40000, f + 10, 6000, written: 6000));
            Assert.IsNull(Tick(a, 50000, f + 20, 6000, written: 6000));
            Assert.AreEqual(0, a.Resends);
        }

        [TestMethod]
        public void ConsumerAudit_C3BytesSkippedByAResync_ResendAfterTwoSecondsThenBackoff()
        {
            var a = new ConsumerAudit();
            Assert.IsNull(Tick(a, 1000, 10, 5000, written: 5000));
            Assert.IsNull(Tick(a, 1016, 12, 5000, written: 5000), "baseline taken (SE − SN = 0)");
            // SE writes 3000 B more; Subnautica re-synced its tail over them (corrupt framing): it drained nothing more
            Assert.IsNull(Tick(a, 2000, 20, 5000, written: 8000, pending: 1), "rings drained (the tail jumped), quiet starts");
            Assert.IsNull(Tick(a, 2016, 23, 5000, written: 8000, pending: 1), "mismatch seen: wait 2 s");
            Assert.IsNotNull(a.Pending);
            StringAssert.Contains(a.Pending, "3000 B fewer");
            Assert.IsNull(Tick(a, 3500, 60, 5000, written: 8000, pending: 1), "still within 2 s");
            string why = Tick(a, 4100, 80, 5000, written: 8000, pending: 1);
            Assert.IsNotNull(why, "2 s of mismatch with both rings drained: resend everything");
            Assert.AreEqual(1, a.Resends);
            // the caller restarted the stream; a second loss right away waits for the 10 s backoff
            Assert.IsNull(Tick(a, 4200, 90, 5000, written: 8000, pending: 1));
            Assert.IsNull(Tick(a, 4300, 95, 5000, written: 9000, pending: 1));
            Assert.IsNull(Tick(a, 6500, 140, 5000, written: 9000, pending: 1));
            Assert.IsNull(Tick(a, 9000, 200, 5000, written: 9000, pending: 1), "backoff (10 s)");
            Assert.IsNotNull(Tick(a, 14200, 300, 5000, written: 9000, pending: 1), "after the backoff");
        }

        [TestMethod]
        public void ConsumerAudit_EntityOrPendingMismatch_AndTransientsDoNotResend()
        {
            var a = new ConsumerAudit();
            Tick(a, 1000, 10, 5000, written: 5000);
            Tick(a, 1016, 12, 5000, written: 5000);
            // a lost GridAdd (C4 re-sync): Subnautica knows no grid
            Assert.IsNull(Tick(a, 1100, 20, 5000, written: 5000, ents: 0, pending: 1));
            StringAssert.Contains(a.Pending, "mirroredEntityCount 0");
            Assert.IsNotNull(Tick(a, 3200, 70, 5000, written: 5000, ents: 0, pending: 1));

            // pending parts whose model SE could not load are expected, not a loss
            var b = new ConsumerAudit();
            for (ulong t = 0; t < 5000; t += 16)
                Assert.IsNull(Tick(b, 1000 + t, 10 + t / 16, 5000, written: 5000, pending: 2, expectPending: 2));

            // a mismatch that clears within 2 s (Subnautica still preparing) never resends
            var c = new ConsumerAudit();
            Tick(c, 1000, 10, 5000, written: 5000);
            Tick(c, 1016, 12, 5000, written: 5000);
            Assert.IsNull(Tick(c, 1100, 20, 5000, written: 5000, pending: 1));
            Assert.IsNull(Tick(c, 2000, 40, 5000, written: 5000, pending: 0));
            Assert.IsNull(Tick(c, 4000, 80, 5000, written: 5000, pending: 0));
            Assert.AreEqual(0, c.Resends);

            // not idle or rings not drained: nothing is judged (SE still sending, Subnautica still draining)
            var d = new ConsumerAudit();
            for (ulong t = 0; t < 10000; t += 16)
            {
                Assert.IsNull(Tick(d, 1000 + t, 10 + t / 16, 0, written: 9000, ents: 0, pending: 5, idle: t % 32 == 0, ringsDrained: t % 32 != 0));
            }
            Assert.AreEqual(0, d.Checks, "only idle and drained moments are checked");

            // a C7 not yet newer than the drained rings is not judged
            var e = new ConsumerAudit();
            Assert.IsNull(Tick(e, 1000, 10, 0, written: 9000, ents: 0));
            Assert.IsNull(Tick(e, 1100, 11, 0, written: 9000, ents: 0));
            Assert.AreEqual(0, e.Checks, "snFrame must advance 2 frames past the quiet start");
        }
    }
}
