using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// ROADMAP S7 statics (B3): the C12 codec (S7 version in the header pad 0x06, the census flag, malformed messages
 /// dropped whole), the filter by entity type with the census allow-list (and the seeded failures S7.M7, S7.M3 as
 /// rules), the grow-only batch set (dedup to 1 cm, epochs, the MaxPerBatch cap), the host names and life cycle
 /// rules, the batch lattice and the SE geometry's closed-form properties. The numbers against the independent Python
 /// reading are in <see cref="S7StaticsVectorTests"/>.
 /// </summary>
    [TestClass]
    public unsafe class S7StaticsTests
    {
        private static StaticPrimitive Prim(byte kind, float x, float y, float z, float sx, float sy, float sz, uint cls = 7, float qx = 0, float qy = 0, float qz = 0, float qw = 1)
        {
            return new StaticPrimitive
            {
                kind = kind, flags = 0, centreSn = new F32x3(x, y, z), qSn = new F32x4(qx, qy, qz, qw), size = new F32x3(sx, sy, sz), classIdHash = cls,
            };
        }

        // ------------------------------------------------------------------------------------------- layout

        [TestMethod]
        public void Records_AreTheLayoutSizes_AndTheS7FieldsSitInThePads()
        {
            Assert.AreEqual(16, sizeof(BatchStaticsHeader));
            Assert.AreEqual(48, sizeof(StaticPrimitive));
            Assert.AreEqual(16, sizeof(BatchClear));
            Assert.AreEqual(0x06, S7StaticsCodec.VersionOffset, "field statics version 0x6 2 u16");
            Assert.AreEqual(0x06, (int)Marshal.OffsetOf(typeof(BatchStaticsHeader), "pad_6"));
            Assert.AreEqual(0x01, (int)Marshal.OffsetOf(typeof(StaticPrimitive), "flags"), "field prim flags 0x1 1 u8");
            Assert.AreEqual(1, S7Conventions.C12Ext.Version);
            Assert.AreEqual(1, S7Conventions.StaticFlags.Census);
            Assert.AreEqual(2, S7Conventions.StaticFlags.AllowListed);
            Assert.AreEqual(4, S7Conventions.StaticFlags.SkewApprox);
            Assert.AreEqual(16 + 48 * 4096, S7StaticsCodec.MaxPayloadBytes);
            long ringHalf = Layout.Region(RegionId.C12SnStatics).Size / 2 - ByteRing.HeaderBytes;
            Assert.IsTrue(S7StaticsCodec.MaxPayloadBytes <= ringHalf, "a full batch fits one C12 message (half the 4 MiB ring)");
        }

        // ------------------------------------------------------------------------------------------- codec

        [TestMethod]
        public void Codec_RoundTrip_VersionAndCensusFlag()
        {
            var prims = new[]
            {
                Prim(StaticKind.Box, 1, -2, 3, 1, 2, 3),
                Prim(StaticKind.Capsule, 4, -5, 6, 0.5f, 2f, 1f, 9, 0, 0.70710677f, 0, 0.70710677f),
                Prim(StaticKind.Sphere, 7, -8, 9, 3, 0, 0),
                Prim(StaticKind.MeshObb, 10, -11, 12, 30, 4, 5, 11),
            };
            prims[1].flags = S7Conventions.StaticFlags.AllowListed;
            var buf = new byte[S7StaticsCodec.PayloadBytes(prims.Length)];
            int n = S7StaticsCodec.Encode(3, 17, -2, 42, prims, prims.Length, buf);
            Assert.AreEqual(16 + 48 * 4, n);
            Assert.AreEqual(1, BitConverter.ToUInt16(buf, 6), "S7 version at 0x06");
            var dst = new StaticPrimitive[8];
            BatchStaticsHeader h;
            int count;
            Assert.AreEqual(S7StaticsStatus.Ok, S7StaticsCodec.TryDecode(buf, n, out h, dst, out count));
            Assert.AreEqual(4, count);
            Assert.AreEqual(3, h.bx); Assert.AreEqual(17, h.by); Assert.AreEqual(-2, h.bz); Assert.AreEqual(42u, h.epoch);
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(prims[i].kind, dst[i].kind);
                Assert.AreEqual(prims[i].flags, dst[i].flags);
                Assert.AreEqual(prims[i].centreSn.y, dst[i].centreSn.y);
                Assert.AreEqual(prims[i].size.x, dst[i].size.x);
                Assert.AreEqual(prims[i].classIdHash, dst[i].classIdHash);
            }
            // the census copy: Census OR-ed into every primitive, nothing else changed
            var cen = new byte[n];
            fixed (byte* p = cen) S7StaticsCodec.Encode(3, 17, -2, 42, prims, 0, prims.Length, p, cen.Length, S7Conventions.StaticFlags.Census);
            Assert.AreEqual(S7StaticsStatus.Ok, S7StaticsCodec.TryDecode(cen, n, out h, dst, out count));
            for (int i = 0; i < 4; i++) Assert.AreEqual(prims[i].flags | 1, dst[i].flags);
            for (int i = 0; i < n; i++)
                if (i < 16 || (i - 16) % 48 != 1) Assert.AreEqual(buf[i], cen[i], "byte " + i);
        }

        [TestMethod]
        public void Codec_MalformedMessages_AreRefusedWhole()
        {
            var prims = new[] { Prim(StaticKind.Box, 1, -2, 3, 1, 2, 3), Prim(StaticKind.Capsule, 4, -5, 6, 0.5f, 2f, 1f) };
            var good = new byte[S7StaticsCodec.PayloadBytes(2)];
            S7StaticsCodec.Encode(1, 2, 3, 9, prims, 2, good);
            var dst = new StaticPrimitive[4];
            BatchStaticsHeader h;
            int count;
            Func<byte[], int, S7StaticsStatus> dec = (b, len) => S7StaticsCodec.TryDecode(b, len, out h, dst, out count);

            Assert.AreEqual(S7StaticsStatus.TooShort, dec(good, 15));
            Assert.AreEqual(S7StaticsStatus.BadCount, dec(good, good.Length - 48));
            byte[] v0 = (byte[])good.Clone(); v0[6] = 0; Assert.AreEqual(S7StaticsStatus.BadVersion, dec(v0, v0.Length), "an encoder before S7");
            byte[] tm = (byte[])good.Clone(); BitConverter.GetBytes(4097u).CopyTo(tm, 12); Assert.AreEqual(S7StaticsStatus.TooMany, dec(tm, tm.Length));
            byte[] k = (byte[])good.Clone(); k[16] = 5; Assert.AreEqual(S7StaticsStatus.BadKind, dec(k, k.Length));
            byte[] nan = (byte[])good.Clone(); BitConverter.GetBytes(float.NaN).CopyTo(nan, 16 + 48 + 0x20); Assert.AreEqual(S7StaticsStatus.NonFinite, dec(nan, nan.Length));
            byte[] rot = (byte[])good.Clone(); BitConverter.GetBytes(0.5f).CopyTo(rot, 16 + 0x1C); Assert.AreEqual(S7StaticsStatus.BadRotation, dec(rot, rot.Length));
            byte[] sz = (byte[])good.Clone(); BitConverter.GetBytes(0f).CopyTo(sz, 16 + 0x24); Assert.AreEqual(S7StaticsStatus.BadSize, dec(sz, sz.Length));
            byte[] ax = (byte[])good.Clone(); BitConverter.GetBytes(3f).CopyTo(ax, 16 + 48 + 0x28); Assert.AreEqual(S7StaticsStatus.BadAxis, dec(ax, ax.Length));
            var small = new StaticPrimitive[1];
            Assert.AreEqual(S7StaticsStatus.BufferTooSmall, S7StaticsCodec.TryDecode(good, good.Length, out h, small, out count));
            Assert.AreEqual(0, count, "nothing partially accepted");
            Assert.AreEqual(S7StaticsStatus.Ok, dec(good, good.Length));
        }

        [TestMethod]
        public void Codec_Clear_AndEncoderGuards()
        {
            var b = new byte[16];
            Assert.AreEqual(16, S7StaticsCodec.EncodeClear(-1, 5, 25, 6, b));
            BatchClear c, ignored;
            fixed (byte* p = b)
            {
                Assert.IsFalse(S7StaticsCodec.TryDecodeClear(p, 15, out ignored));
                Assert.IsTrue(S7StaticsCodec.TryDecodeClear(p, 16, out c));
            }
            Assert.AreEqual(-1, c.bx); Assert.AreEqual(5, c.by); Assert.AreEqual(25, c.bz); Assert.AreEqual(6u, c.epoch);
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => S7StaticsCodec.Encode(1, 2, 3, 1, new StaticPrimitive[4097], 4097, new byte[300000]));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => S7StaticsCodec.Encode(40000, 2, 3, 1, new StaticPrimitive[1], 1, new byte[64]));
            Assert.ThrowsException<ArgumentException>(() => S7StaticsCodec.Encode(1, 2, 3, 1, new StaticPrimitive[2], 2, new byte[63]));
        }

        [TestMethod]
        public void Ring_FullBatchesThroughA4MiBByteRing_FullIsTriedOnceAndRequeued()
        {
            // the C12 path as both plugins use it: S7StaticsBatchSet.Encode → ByteRing.TryWrite (SN) → ByteRing.TryRead →
 // S7StaticsCodec.TryDecode (SE), with the ring of the layout's size (4 MiB)
            long cap = Layout.Region(RegionId.C12SnStatics).Size;
            IntPtr mem = Marshal.AllocHGlobal((IntPtr)(cap + 64));
            try
            {
                ulong* head = (ulong*)mem, tail = head + 1;
                *head = 0; *tail = 0;
                byte* data = (byte*)mem + 64;
                var producer = new ByteRing(head, tail, data, cap);
                var consumer = new ByteRing(head, tail, data, cap);
                var set = new S7StaticsBatchSet(12, 18, 12);
                for (int i = 0; i < S7Conventions.StaticsLimits.MaxPerBatch; i++)
                {
                    StaticPrimitive p = Prim((byte)(1 + i % 4), i * 0.07f, -3 - (i % 50), 20, 1, 0.5f, 1 + i % 3);
                    if (p.kind == StaticKind.Capsule) p.size = new F32x3(0.5f, 1f, 2f);
                    if (p.kind == StaticKind.Sphere) p.size = new F32x3(1f, 0, 0);
                    Assert.AreEqual(S7StaticsAdd.Added, set.Add(ref p));
                }
                var buf = new byte[S7StaticsCodec.MaxPayloadBytes];
                int written = 0;
                RingWriteStatus st;
                while (true)
                {
                    set.NextEpoch();
                    int n = set.Encode(buf, false);
                    st = producer.TryWrite(C12Type.BatchStatics, buf, 0, n);
                    if (st != RingWriteStatus.Ok) break;
                    written++;
                }
                Assert.AreEqual(RingWriteStatus.Full, st, "a full ring refuses whole messages (the reporter re-queues)");
                Assert.AreEqual(21, written, "4 MiB holds 21 full batches of 196,624 B (+ 8 B framing each)");
                set.Requeue();
                Assert.IsTrue(set.Dirty);
                var rbuf = new byte[S7StaticsCodec.MaxPayloadBytes + 64];
                var dst = new StaticPrimitive[S7Conventions.StaticsLimits.MaxPerBatch];
                uint lastEpoch = 0;
                for (int m = 0; m < written; m++)
                {
                    uint type;
                    int bytes;
                    Assert.AreEqual(RingReadStatus.Ok, consumer.TryRead(out type, rbuf, out bytes));
                    Assert.AreEqual(C12Type.BatchStatics, type);
                    BatchStaticsHeader h;
                    int count;
                    Assert.AreEqual(S7StaticsStatus.Ok, S7StaticsCodec.TryDecode(rbuf, bytes, out h, dst, out count));
                    Assert.AreEqual(4096, count);
                    Assert.IsTrue(h.epoch > lastEpoch, "epochs only grow");
                    lastEpoch = h.epoch;
                }
                uint t2;
                int b2;
                Assert.AreEqual(RingReadStatus.Empty, consumer.TryRead(out t2, rbuf, out b2));
                set.NextEpoch();
                Assert.AreEqual(RingWriteStatus.Ok, producer.TryWrite(C12Type.BatchStatics, buf, 0, set.Encode(buf, false)), "room again after the drain");
            }
            finally { Marshal.FreeHGlobal(mem); }
        }

        // ------------------------------------------------------------------------------------------- filter

        private static S7StaticsCandidate Static()
        {
            return new S7StaticsCandidate { Enabled = true, Layer = 0, UnderWorld = true };
        }

        [TestMethod]
        public void Filter_ByEntityType_WithTheAllowList()
        {
            S7StaticsCandidate c = Static();
            Assert.AreEqual(S7StaticsReject.None, S7StaticsFilter.Decide(ref c, false), "a wreck box");
            c = Static(); c.Enabled = false; Assert.AreEqual(S7StaticsReject.Disabled, S7StaticsFilter.Decide(ref c, false));
            c = Static(); c.Trigger = true; Assert.AreEqual(S7StaticsReject.Trigger, S7StaticsFilter.Decide(ref c, false));
            c = Static(); c.Layer = 17; Assert.AreEqual(S7StaticsReject.Layer, S7StaticsFilter.Decide(ref c, false));
            c = Static(); c.Proxy = true; Assert.AreEqual(S7StaticsReject.Proxy, S7StaticsFilter.Decide(ref c, false), "never our S6 proxies");
            c = Static(); c.Bridge = true; Assert.AreEqual(S7StaticsReject.Bridge, S7StaticsFilter.Decide(ref c, false), "never our mirror/ghost");
            c = Static(); c.UnderWorld = false; Assert.AreEqual(S7StaticsReject.NotWorld, S7StaticsFilter.Decide(ref c, false));
            c = Static(); c.Pickupable = true; c.AllowListed = true; Assert.AreEqual(S7StaticsReject.Pickupable, S7StaticsFilter.Decide(ref c, false), "the allow-list never waives entity types");
            c = Static(); c.Vehicle = true; Assert.AreEqual(S7StaticsReject.Vehicle, S7StaticsFilter.Decide(ref c, false));
            c = Static(); c.WorldForces = true; Assert.AreEqual(S7StaticsReject.WorldForces, S7StaticsFilter.Decide(ref c, false));
            c = Static(); c.Rigidbody = true; c.RigidbodyKinematic = true; Assert.AreEqual(S7StaticsReject.Rigidbody, S7StaticsFilter.Decide(ref c, false), "kinematic is not static");
            c.AllowListed = true; Assert.AreEqual(S7StaticsReject.None, S7StaticsFilter.Decide(ref c, false), "an allow-listed wreck piece with a kinematic body");
            c = Static(); c.LiveMixin = true; Assert.AreEqual(S7StaticsReject.LiveMixin, S7StaticsFilter.Decide(ref c, false));
            c.AllowListed = true; Assert.AreEqual(S7StaticsReject.None, S7StaticsFilter.Decide(ref c, false));
        }

        [TestMethod]
        public void Filter_ReviewF3_WorldForcesWaivedForAListedKinematicWreck_Only()
        {
            // the five explorable wrecks: a WorldForces script on a serialized-kinematic body, class on allowWorldForces
            S7StaticsCandidate c = Static();
            c.WorldForces = true; c.Rigidbody = true; c.RigidbodyKinematic = true; c.AllowListed = true; c.AllowListedWorldForces = true;
            Assert.AreEqual(S7StaticsReject.None, S7StaticsFilter.Decide(ref c, false), "a listed kinematic wreck is reported");
            c.RigidbodyKinematic = false;
            Assert.AreEqual(S7StaticsReject.WorldForces, S7StaticsFilter.Decide(ref c, false), "a dynamic body never");
            c.RigidbodyKinematic = true; c.AllowListedWorldForces = false;
            Assert.AreEqual(S7StaticsReject.WorldForces, S7StaticsFilter.Decide(ref c, false), "not on the list: refused as before");
            c.AllowListedWorldForces = true; c.AllowListed = false;
            Assert.AreEqual(S7StaticsReject.Rigidbody, S7StaticsFilter.Decide(ref c, false), "the waiver is WorldForces only; the body needs the allow-list");
            c = Static(); c.WorldForces = true; c.AllowListedWorldForces = true;
            Assert.AreEqual(S7StaticsReject.WorldForces, S7StaticsFilter.Decide(ref c, false), "no body at all: nothing kinematic to waive for");
            var reaper = new S7StaticsCandidate
            {
                Enabled = true, Layer = 0, UnderWorld = true, Creature = true, Freeze = true, WorldForces = true, Rigidbody = true,
                RigidbodyKinematic = true, AllowListed = true, AllowListedWorldForces = true,
            };
            Assert.AreEqual(S7StaticsReject.Creature, S7StaticsFilter.Decide(ref reaper, false), "a frozen reaper stays out (Creature before WorldForces)");
        }

        [TestMethod]
        public void Filter_ReviewF5_PlayerSolidMask_DiveReelIn_TerrainTriggerOut()
        {

            int mask = S7StaticsFilter.PlayerSolidMask(l => l != 9, 30, 21);
            Assert.AreNotEqual(0, mask & 1, "Default");
            Assert.AreNotEqual(0, mask & (1 << 26), "DiveReel: the wreck walls");
            Assert.AreEqual(0, mask & (1 << 9), "OnlyVehicle");
            Assert.AreEqual(0, mask & (1 << 30), "TerrainCollider: the S4 octrees own terrain");
            Assert.AreEqual(0, mask & (1 << 21), "Trigger");
            Assert.AreEqual(S7StaticsFilter.DefaultMask, S7StaticsFilter.PlayerSolidMask(null, 30, 21), "no Unity answer: Default only");
            S7StaticsCandidate c = Static(); c.Layer = 26;
            Assert.AreEqual(S7StaticsReject.Layer, S7StaticsFilter.Decide(ref c, false), "the two-argument form keeps the Default-only mask");
            Assert.AreEqual(S7StaticsReject.None, S7StaticsFilter.Decide(ref c, false, mask));
            c.Layer = 30;
            Assert.AreEqual(S7StaticsReject.Layer, S7StaticsFilter.Decide(ref c, false, mask));
            c.Layer = 40;
            Assert.AreEqual(S7StaticsReject.Layer, S7StaticsFilter.Decide(ref c, false, mask), "an out-of-range layer id");
        }

        [TestMethod]
        public void BatchSet_ReviewF16_SameCentreDifferentSizeOrRotation_Kept()
        {
            var set = new S7StaticsBatchSet(1, 2, 3);
            StaticPrimitive a = Prim(StaticKind.Box, 10, -20, 30, 2, 1, 1);
            StaticPrimitive nested = Prim(StaticKind.Box, 10, -20, 30, 1, 0.5f, 0.5f);
            StaticPrimitive twin = Prim(StaticKind.Box, 10, -20, 30, 2, 1, 1, 7, 0, 0, 0.7071068f, 0.7071068f);
            StaticPrimitive again = Prim(StaticKind.Box, 10.004f, -20, 30, 2.004f, 1, 1);
            Assert.AreEqual(S7StaticsAdd.Added, set.Add(ref a));
            Assert.AreEqual(S7StaticsAdd.Added, set.Add(ref nested), "a nested box sharing the centre");
            Assert.AreEqual(S7StaticsAdd.Added, set.Add(ref twin), "the rotated twin sharing the centre (crossed beams)");
            Assert.AreEqual(S7StaticsAdd.Duplicate, set.Add(ref again), "within 1 cm of centre and size: the same primitive");
            Assert.AreEqual(3, set.Count);
            Assert.AreEqual(1, S7StaticsMath.QuantRot(0.0005f));
            Assert.AreEqual(-1, S7StaticsMath.QuantRot(-0.0005f));
            Assert.AreEqual(0, S7StaticsMath.QuantRot(0.00049f));
        }

        [TestMethod]
        public void Filter_FrozenReaper_RefusedByType_ReportedUnderS7M7()
        {
            // a reaper 60 m from the camera: FreezeRigidbodyWhenFar made its body kinematic beyond 48 m (S7.N9)
            var reaper = new S7StaticsCandidate
            {
                Enabled = true, Layer = 0, UnderWorld = true, Creature = true, Freeze = true, WorldForces = true,
                Rigidbody = true, RigidbodyKinematic = true, LiveMixin = true,
            };
            Assert.AreEqual(S7StaticsReject.Creature, S7StaticsFilter.Decide(ref reaper, false));
            Assert.AreEqual(S7StaticsReject.None, S7StaticsFilter.Decide(ref reaper, true), "S7.M7: static-or-kinematic reports the frozen reaper (S7.N9 red)");
            reaper.RigidbodyKinematic = false; // the same reaper near the camera, swimming
            Assert.AreEqual(S7StaticsReject.Rigidbody, S7StaticsFilter.Decide(ref reaper, true));
            // our proxies stay out even under the mutant
            var proxy = Static(); proxy.Proxy = true;
            Assert.AreEqual(S7StaticsReject.Proxy, S7StaticsFilter.Decide(ref proxy, true));
        }

        [TestMethod]
        public void Filter_S7M3_DropsCapsulesOnly()
        {
            Assert.AreEqual(S7StaticsReject.None, S7StaticsFilter.KindAllowed(StaticKind.Capsule, false));
            Assert.AreEqual(S7StaticsReject.MutantCapsule, S7StaticsFilter.KindAllowed(StaticKind.Capsule, true));
            Assert.AreEqual(S7StaticsReject.None, S7StaticsFilter.KindAllowed(StaticKind.Box, true));
            Assert.AreEqual(S7StaticsReject.None, S7StaticsFilter.KindAllowed(StaticKind.Sphere, true));
            Assert.AreEqual(S7StaticsReject.None, S7StaticsFilter.KindAllowed(StaticKind.MeshObb, true));
            for (int r = 0; r < S7StaticsFilter.ReasonCount; r++)
                Assert.IsFalse(S7StaticsFilter.ReasonName((S7StaticsReject)r).StartsWith("reason"), "every reason named: " + r);
        }

        // ------------------------------------------------------------------------------------------- batch set

        [TestMethod]
        public void BatchSet_GrowsOnly_DedupsToOneCentimetre_EpochsAdvance()
        {
            var s = new S7StaticsBatchSet(4, 5, 6);
            StaticPrimitive a = Prim(StaticKind.Box, 10.001f, -20, 30, 1, 1, 1);
            Assert.AreEqual(S7StaticsAdd.Added, s.Add(ref a));
            Assert.IsTrue(s.Dirty);
            // same cm of centre and size, same rotation, kind and class: one wreck piece seen twice
            StaticPrimitive dup = Prim(StaticKind.Box, 10.003f, -20, 30, 1.002f, 1, 1);
            Assert.AreEqual(S7StaticsAdd.Duplicate, s.Add(ref dup));
            StaticPrimitive otherKind = Prim(StaticKind.MeshObb, 10.001f, -20, 30, 1, 1, 1);
            Assert.AreEqual(S7StaticsAdd.Added, s.Add(ref otherKind));
            StaticPrimitive otherClass = Prim(StaticKind.Box, 10.001f, -20, 30, 1, 1, 1, 8);
            Assert.AreEqual(S7StaticsAdd.Added, s.Add(ref otherClass));
            Assert.AreEqual(3, s.Count);
            Assert.AreEqual(1u, s.NextEpoch());
            Assert.IsFalse(s.Dirty);
            Assert.AreEqual(S7StaticsAdd.Duplicate, s.Add(ref a), "a rescan adds nothing: no resend");
            Assert.IsFalse(s.Dirty);
            StaticPrimitive b = Prim(StaticKind.Sphere, 50, -20, 30, 1, 0, 0);
            Assert.AreEqual(S7StaticsAdd.Added, s.Add(ref b));
            Assert.AreEqual(2u, s.NextEpoch(), "new primitives: resent with epoch + 1");
            s.Requeue();
            Assert.IsTrue(s.Dirty);
            Assert.AreEqual(2u, s.Epoch, "a failed write keeps its epoch");

            var buf = new byte[S7StaticsCodec.MaxPayloadBytes];
            int n = s.Encode(buf, true);
            var dst = new StaticPrimitive[8];
            BatchStaticsHeader h;
            int count;
            Assert.AreEqual(S7StaticsStatus.Ok, S7StaticsCodec.TryDecode(buf, n, out h, dst, out count));
            Assert.AreEqual(4, count);
            Assert.AreEqual(2u, h.epoch);
            for (int i = 0; i < count; i++) Assert.AreEqual(S7Conventions.StaticFlags.Census, dst[i].flags & S7Conventions.StaticFlags.Census);
        }

        [TestMethod]
        public void BatchSet_CapsAtMaxPerBatch()
        {
            var s = new S7StaticsBatchSet(0, 0, 0);
            for (int i = 0; i < S7Conventions.StaticsLimits.MaxPerBatch; i++)
            {
                StaticPrimitive p = Prim(StaticKind.Box, i * 0.05f, -1, 0, 1, 1, 1);
                Assert.AreEqual(S7StaticsAdd.Added, s.Add(ref p), "item " + i);
            }
            StaticPrimitive extra = Prim(StaticKind.Box, -5, -1, 0, 1, 1, 1);
            Assert.AreEqual(S7StaticsAdd.Full, s.Add(ref extra));
            Assert.AreEqual(1, s.Overflow);
            Assert.AreEqual(S7Conventions.StaticsLimits.MaxPerBatch, s.Count);
            var buf = new byte[S7StaticsCodec.MaxPayloadBytes];
            s.NextEpoch();
            Assert.AreEqual(S7StaticsCodec.MaxPayloadBytes, s.Encode(buf, false));
        }

        // ------------------------------------------------------------------------------------------- hosts and rules

        [TestMethod]
        public void HostNames_SplitAndLifeCycle()
        {
            Assert.AreEqual("DE.Statics.12.18.7.e3", S7StaticsMath.HostName(12, 18, 7, 3, -1));
            Assert.AreEqual("DE.Statics.12.18.7.e3.p1", S7StaticsMath.HostName(12, 18, 7, 3, 1));
            int bx, by, bz, part; uint ep;
            Assert.IsTrue(S7StaticsMath.TryParseHostName("DE.Statics.-1.2.30.e4294967295.p12", out bx, out by, out bz, out ep, out part));
            Assert.AreEqual(-1, bx); Assert.AreEqual(2, by); Assert.AreEqual(30, bz); Assert.AreEqual(uint.MaxValue, ep); Assert.AreEqual(12, part);
            Assert.IsTrue(S7StaticsMath.TryParseHostName("DE.Statics.1.2.3.e4", out bx, out by, out bz, out ep, out part));
            Assert.AreEqual(-1, part);
            foreach (string bad in new[] { "DE.Static.1.2.3.e4", "DE.Statics.1.2.e4", "DE.Statics.1.2.3.4", "DE.Statics.1.2.3.e4.q1", "DE.Statics.a.2.3.e4", null })
                Assert.IsFalse(S7StaticsMath.TryParseHostName(bad, out bx, out by, out bz, out ep, out part), bad ?? "null");

            Assert.AreEqual(1, S7StaticsMath.HostParts(0));
            Assert.AreEqual(1, S7StaticsMath.HostParts(1024));
            Assert.AreEqual(2, S7StaticsMath.HostParts(1025));
            Assert.AreEqual(4, S7StaticsMath.HostParts(4096));
            for (int count = 0; count <= 4096; count += 511)
            {
                int parts = S7StaticsMath.HostParts(count), covered = 0;
                for (int k = 0; k < parts; k++)
                {
                    int start, n;
                    S7StaticsMath.HostRange(k, count, out start, out n);
                    Assert.AreEqual(covered, start);
                    Assert.IsTrue(n <= S7Conventions.StaticsLimits.MaxPerHost);
                    covered += n;
                }
                Assert.AreEqual(count, covered, "every primitive in exactly one host, count " + count);
            }

            Assert.IsTrue(S7StaticsMath.HostWanted(false, 300.0));
            Assert.IsFalse(S7StaticsMath.HostWanted(false, 300.01), "not created beyond 300 m");
            Assert.IsTrue(S7StaticsMath.HostWanted(true, 349.9), "kept between 300 and 350 m");
            Assert.IsFalse(S7StaticsMath.HostWanted(true, 350.01), "closed beyond 350 m");
            Assert.IsFalse(S7StaticsMath.HostWanted(true, double.NaN));

            Assert.IsTrue(S7StaticsMath.Supersedes(false, false, 0, true, 1), "nothing yet: a census file is used");
            Assert.IsTrue(S7StaticsMath.Supersedes(true, true, 9, false, 1), "live replaces census");
            Assert.IsFalse(S7StaticsMath.Supersedes(true, false, 1, true, 9), "census never replaces live");
            Assert.IsTrue(S7StaticsMath.Supersedes(true, false, 1, false, 2), "newer epoch wins");
            Assert.IsFalse(S7StaticsMath.Supersedes(true, false, 2, false, 2));
            Assert.IsFalse(S7StaticsMath.Supersedes(true, false, 3, false, 2), "an older epoch is stale");

            Assert.AreEqual("3.-1.25.c12", S7StaticsMath.CensusFileName(3, -1, 25));
            Assert.IsTrue(S7StaticsMath.TryParseCensusFileName("3.-1.25.c12", out bx, out by, out bz));
            Assert.AreEqual(-1, by);
            Assert.IsFalse(S7StaticsMath.TryParseCensusFileName("3.1.c12", out bx, out by, out bz));
            Assert.IsFalse(S7StaticsMath.TryParseCensusFileName("3.1.2.bin", out bx, out by, out bz));
            Assert.IsTrue(S7StaticsMath.CensusBatchesFolder(@"C:\L").EndsWith(@"DeepEngineers\census\batches"));
        }

        [TestMethod]
        public void BatchLattice_Keys_AndDistances()
        {
            int bx, by, bz;
            S7StaticsMath.BatchOfSn(new Vec3(-2048, -3040, -2048), out bx, out by, out bz);
            Assert.AreEqual(0, bx); Assert.AreEqual(0, by); Assert.AreEqual(0, bz);
            S7StaticsMath.BatchOfSn(new Vec3(2047.9, 159.9, 2047.9), out bx, out by, out bz);
            Assert.AreEqual(25, bx); Assert.AreEqual(19, by); Assert.AreEqual(25, bz);
            Assert.IsTrue(S7StaticsMath.InLattice(25, 19, 25));
            Assert.IsFalse(S7StaticsMath.InLattice(26, 0, 0));
            S7StaticsMath.BatchOfSn(new Vec3(0, -10, 0), out bx, out by, out bz);
            Assert.AreEqual(12, bx); Assert.AreEqual(18, by); Assert.AreEqual(12, bz);
            Assert.AreEqual(0.0, S7StaticsMath.DistanceToBatchSn(new Vec3(0, -10, 0), 12, 18, 12));
            Assert.AreEqual(Vec3.Zero.X + (-2048 + 12 * 160 + 80), S7StaticsMath.BatchCentreSn(12, 18, 12).X);
            foreach (var t in new[] { new[] { 0, 0, 0 }, new[] { -1, -2, -3 }, new[] { 25, 19, 25 }, new[] { short.MinValue, short.MaxValue, -7 } })
            {
                long key = S7StaticsMath.BatchKey(t[0], t[1], t[2]);
                S7StaticsMath.UnpackBatchKey(key, out bx, out by, out bz);
                Assert.AreEqual(t[0], bx); Assert.AreEqual(t[1], by); Assert.AreEqual(t[2], bz);
            }
            Assert.AreNotEqual(S7StaticsMath.BatchKey(1, 2, 3), S7StaticsMath.BatchKey(3, 2, 1));
        }

        // ------------------------------------------------------------------------------------------- geometry

        [TestMethod]
        public void Geometry_Box_DeterminantPlusOne_CentreRoundTrip_HalfBetweenHOverSAndH()
        {
            SiteMap site = SiteMap.SiteV1Test;
            var rnd = new Random(17);
            for (int i = 0; i < 300; i++)
            {
                var q = new Quat(rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1).Normalized().WithNonNegativeW();
                StaticPrimitive p = Prim(i % 2 == 0 ? StaticKind.Box : StaticKind.MeshObb, (float)(rnd.NextDouble() * 3000 - 1500), (float)(-rnd.NextDouble() * 1500 - 1),
                                         (float)(rnd.NextDouble() * 3000 - 1500), 1 + (float)rnd.NextDouble() * 20, 1 + (float)rnd.NextDouble() * 20, 1 + (float)rnd.NextDouble() * 20,
                                         7, (float)q.X, (float)q.Y, (float)q.Z, (float)q.W);
                S7StaticSe r = S7StaticsMath.ToSe(site, ref p);
                Assert.IsTrue(r.Ok, r.Drop);
                Assert.AreEqual(1.0, r.Determinant, 1e-9);
                Vec3 back = site.SeToSn(r.Centre);
                Assert.AreEqual(p.centreSn.x, back.X, 1e-6); Assert.AreEqual(p.centreSn.y, back.Y, 1e-6); Assert.AreEqual(p.centreSn.z, back.Z, 1e-6);
                Assert.IsTrue(r.S > 1.0, "below the sea: s = Rs/r > 1");
                for (int k = 0; k < 3; k++)
                {
                    double h = k == 0 ? p.size.x : k == 1 ? p.size.y : p.size.z;
                    Assert.IsTrue(r.Half[k] <= h + 1e-9 && r.Half[k] >= h / r.S - 1e-9, "half extent between h/s and h");
                }
                Assert.IsTrue(r.BoundingRadius > 0);
            }
        }

        [TestMethod]
        public void Geometry_CapsuleEndsExact_SphereRadius_LowestY()
        {
            SiteMap site = SiteMap.SiteV1Test;
            StaticPrimitive c = Prim(StaticKind.Capsule, 100, -30, -40, 0.5f, 3f, 1f, 7, 0, 0, 0.38268343f, 0.9238795f);
            S7StaticSe r = S7StaticsMath.ToSe(site, ref c);
            Assert.IsTrue(r.Ok);
            var q = new Quat(0, 0, 0.38268343f, 0.9238795f).Normalized();
            Vec3 a = q.Rotate(Vec3.UnitY);
            var centre = new Vec3(100, -30, -40);
            Assert.IsTrue(Vec3.Distance(site.SeToSn(r.A), centre - a * 3.0) < 1e-6, "end A = F^-1(c - a h)");
            Assert.IsTrue(Vec3.Distance(site.SeToSn(r.B), centre + a * 3.0) < 1e-6, "end B = F^-1(c + a h)");
            Assert.AreEqual(0.5 / r.S, r.Radius, 1e-12);
            Assert.AreEqual(Vec3.Distance(r.A, r.B) * 0.5 + r.Radius, r.BoundingRadius, 1e-9);

            StaticPrimitive s = Prim(StaticKind.Sphere, 0, -2, 0, 3, 0, 0);
            S7StaticSe rs = S7StaticsMath.ToSe(site, ref s);
            Assert.AreEqual(3.0 / rs.S, rs.Radius, 1e-12);
            Assert.AreEqual(-5.0, S7StaticsMath.LowestY(ref s), 1e-9);
            Assert.IsFalse(S7StaticsMath.EntirelyAboveSea(ref s));
            StaticPrimitive high = Prim(StaticKind.Sphere, 0, 10, 0, 3, 0, 0);
            Assert.IsTrue(S7StaticsMath.EntirelyAboveSea(ref high), "a rock top above the sea is not sent");
            StaticPrimitive straddle = Prim(StaticKind.Box, 0, 1, 0, 2, 2, 2);
            Assert.IsFalse(S7StaticsMath.EntirelyAboveSea(ref straddle), "a pier reaching under the sea is sent");
            Assert.AreEqual(1 - Math.Abs(a.Y) * 3.0 - 0.5, S7StaticsMath.LowestY(ref c) + 31, 1e-6);
        }

        [TestMethod]
        public void UnitySizes_CapsuleSphereBoxShear()
        {
            Vec3 cs = S7StaticsMath.CapsuleSize(0.5, 3.0, 1, new Vec3(1, 1, 1));
            Assert.AreEqual(0.5, cs.X); Assert.AreEqual(1.0, cs.Y); Assert.AreEqual(1.0, cs.Z);
            cs = S7StaticsMath.CapsuleSize(1.0, 1.0, 1, new Vec3(1, 1, 1));
            Assert.AreEqual(0.0, cs.Y, "shorter than its caps: a sphere-like capsule");
            cs = S7StaticsMath.CapsuleSize(0.5, 2.0, 2, new Vec3(2, -3, 1));
            Assert.AreEqual(1.5, cs.X, "radius x the larger scale across the axis");
            Assert.AreEqual(0.0, cs.Y);
            Assert.AreEqual(2.0, cs.Z);
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => S7StaticsMath.CapsuleSize(1, 1, 3, new Vec3(1, 1, 1)));
            Assert.AreEqual(6.0, S7StaticsMath.SphereRadius(2.0, new Vec3(1, -3, 2)));
            Vec3 bh = S7StaticsMath.BoxHalf(new Vec3(2, 0, 4), new Vec3(1, 1, -2));
            Assert.AreEqual(1.0, bh.X); Assert.AreEqual(S7StaticsMath.MinHalfExtentM, bh.Y); Assert.AreEqual(4.0, bh.Z);
            Assert.AreEqual(0.0, S7StaticsMath.Shear(new Vec3(1, 0, 0), new Vec3(0, 2, 0), new Vec3(0, 0, 3)));
            Assert.IsTrue(S7StaticsMath.IsSkewed(new Vec3(1, 0, 0), new Vec3(0.1, 1, 0), new Vec3(0, 0, 1)));
            Assert.IsFalse(S7StaticsMath.IsSkewed(new Vec3(1, 0, 0), new Vec3(0.005, 1, 0), new Vec3(0, 0, 1)));
        }

        [TestMethod]
        public void LightmappedSceneRoots_AreWorld_WithASceneClassId()
        {
            Assert.AreEqual("scene:aurora", S7StaticsMath.LightmappedSceneClassId("Aurora-MainPrefab"), "LightmappedPrefabs names the Aurora root <scene>-MainPrefab");
            Assert.AreEqual("scene:escapepod", S7StaticsMath.LightmappedSceneClassId("EscapePod-MainPrefab"));
            Assert.IsNull(S7StaticsMath.LightmappedSceneClassId("-MainPrefab"));
            Assert.IsNull(S7StaticsMath.LightmappedSceneClassId("__LIGHTMAPPED_PREFAB__"), "not yet renamed: not a loaded scene root");
            Assert.IsNull(S7StaticsMath.LightmappedSceneClassId("Batch (12, 18, 12) objects"));
            Assert.IsNull(S7StaticsMath.LightmappedSceneClassId(null));
        }

        [TestMethod]
        public void ClassIdHash_KnownValues()
        {
            Assert.AreEqual(0u, S7StaticsMath.ClassIdHash(null));
            Assert.AreEqual(0x811c9dc5u, S7StaticsMath.ClassIdHash(""));
            Assert.AreEqual(0xe40c292cu, S7StaticsMath.ClassIdHash("a"));
            Assert.AreEqual(0xbf9cf968u, S7StaticsMath.ClassIdHash("foobar"));
            Assert.AreEqual(13, S7StaticsMath.Quant(0.125f), "half away from zero");
            Assert.AreEqual(-13, S7StaticsMath.Quant(-0.125f));
            Assert.AreEqual(0, S7StaticsMath.Quant(0.005f), "0.005f is 0.00499999988...: rounds to 0 cm");
            Assert.AreEqual(int.MaxValue, S7StaticsMath.Quant(3e9f));
        }
    }
}
