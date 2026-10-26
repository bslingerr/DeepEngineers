using System;
using System.Runtime.InteropServices;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// SharedSection (create with the CONTRACT security descriptor, open-only), the creator rules of CONTRACT §6
 /// (new, re-initialise, refuse = S0.M6 / S0.N10 in process) and the validation steps of both sides (S0.M1 in process).
 /// </summary>
    [TestClass]
    public unsafe class SectionTests
    {
        public TestContext TestContext { get; set; }

        // ---- test-only native calls to read a kernel object's security descriptor back ----
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint GetSecurityInfo(IntPtr handle, int objectType, uint securityInfo, IntPtr owner, IntPtr group,
            IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(IntPtr sd, uint revision, uint securityInfo,
            out IntPtr stringSd, IntPtr length);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr p);

        private const int SE_KERNEL_OBJECT = 6;
        private const uint DACL_SECURITY_INFORMATION = 0x4, LABEL_SECURITY_INFORMATION = 0x10;

        private static string ReadSddl(IntPtr handle)
        {
            IntPtr sd;
            uint err = GetSecurityInfo(handle, SE_KERNEL_OBJECT, DACL_SECURITY_INFORMATION | LABEL_SECURITY_INFORMATION,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out sd);
            Assert.AreEqual(0u, err, "GetSecurityInfo");
            try
            {
                IntPtr str;
                Assert.IsTrue(ConvertSecurityDescriptorToStringSecurityDescriptorW(sd, 1, DACL_SECURITY_INFORMATION | LABEL_SECURITY_INFORMATION, out str, IntPtr.Zero));
                try { return Marshal.PtrToStringUni(str); }
                finally { LocalFree(str); }
            }
            finally { LocalFree(sd); }
        }

        // ------------------------------------------------------------------------------------------------ section

        [TestMethod]
        public void Section_OpenExisting_AbsentReturnsNull()
        {
            Assert.IsNull(SharedSection.OpenExisting(TestUtil.UniqueSectionName(), Layout.SectionSize));
            Assert.IsNull(SharedSection.OpenExisting(TestUtil.UniqueSectionName(), Layout.SectionSize, readOnly: true));
        }

        [TestMethod]
        public void Section_CreateThenOpen_SharesMemory_ExistedFlag()
        {
            string name = TestUtil.UniqueSectionName();
            using (SharedSection a = SharedSection.Create(name, Layout.SectionSize, TestUtil.Sddl, out bool existedA))
            {
                Assert.IsFalse(existedA);
                Assert.AreEqual(Layout.SectionSize, a.MappedSize);
                Assert.AreEqual(0, a.Base[0x4FFFFFF], "zero-filled by the OS");
                using (SharedSection b = SharedSection.OpenExisting(name, Layout.SectionSize))
                {
                    Assert.IsNotNull(b);
                    Assert.AreEqual(Layout.SectionSize, b.MappedSize);
                    a.Base[0x4FFFFFF] = 0x5A;
                    Assert.AreEqual(0x5A, b.Base[0x4FFFFFF]);
                    b.Base[0x1234] = 0x77;
                    Assert.AreEqual(0x77, a.Base[0x1234]);
                }
                using (SharedSection c = SharedSection.Create(name, Layout.SectionSize, TestUtil.Sddl, out bool existedC))
                {
                    Assert.IsTrue(existedC, "ERROR_ALREADY_EXISTS");
                    Assert.AreEqual(0x77, c.Base[0x1234]);
                }
                using (SharedSection ro = SharedSection.OpenExisting(name, Layout.SectionSize, readOnly: true))
                {
                    Assert.IsTrue(ro.ReadOnly);
                    Assert.AreEqual(0x77, ro.Base[0x1234]);
                }
            }
            Assert.IsNull(SharedSection.OpenExisting(name, Layout.SectionSize), "gone with the last handle");
        }

        [TestMethod]
        public void Section_Create_RequiresASecurityDescriptor()
        {
            bool existed;
            try { SharedSection.Create(TestUtil.UniqueSectionName(), 4096, null, out existed); Assert.Fail("no default security"); }
            catch (ArgumentException) { }
        }

        [TestMethod]
        public void Section_SecurityDescriptor_IsTheContractOne()
        {
            string sid = Win32.CurrentUserSid();
            string sddl = WireInfo.SectionSddl(sid);
            StringAssert.StartsWith(sddl, "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;S-1-");
            StringAssert.EndsWith(sddl, ")S:(ML;;NW;;;ME)");
            Assert.AreEqual(System.Security.Principal.WindowsIdentity.GetCurrent().User.Value, sid, "SID from the process token");
            using (SharedSection s = SharedSection.Create(TestUtil.UniqueSectionName(), 4096, sddl, out bool existed))
            {
                string actual = ReadSddl(s.Handle);
                TestContext.WriteLine("section SD: " + actual);
                StringAssert.StartsWith(actual, "D:P", "protected DACL");
                StringAssert.Contains(actual, ";;;SY)");
                StringAssert.Contains(actual, ";;;BA)");
                StringAssert.Contains(actual, ";;;" + sid + ")");
                Assert.AreEqual(3, actual.Split(new[] { "(A;" }, StringSplitOptions.None).Length - 1, "exactly three allow ACEs");
                StringAssert.Contains(actual, "S:", "a SACL");
                StringAssert.Contains(actual, "(ML;;NW;;;ME)", "medium label, no write up (Windows adds the AI flag to the SACL)");
            }
        }

        // ------------------------------------------------------------------------------------------------ creator

        [TestMethod]
        public void Creator_NewSection_HeaderRegionTableSnBlockMagic()
        {
            string name = TestUtil.UniqueSectionName();
            CreatorResult r = SectionCreator.CreateOrAttach(TestUtil.SnOptions(name, 4242));
            Assert.AreEqual(CreatorOutcome.Created, r.Outcome, r.ToString());
            using (SharedSection s = r.Section)
            {
                HeaderView h = s.Header;
                Assert.AreEqual(WireInfo.Magic, h.Magic);
                Assert.AreEqual((ushort)1, h.LayoutMajor);
                Assert.AreEqual((ushort)0, h.LayoutMinor);
                Assert.AreEqual(0x01020304u, h.ByteOrderMark);
                Assert.AreEqual(0x1000u, h.HeaderSize);
                Assert.AreEqual(0x5000000UL, h.SectionSize);
                Assert.AreEqual(LayoutHash.Compiled, h.LayoutHash);
                Assert.AreEqual(1u, h.WireVersion);
                Assert.AreEqual(1u, h.SiteVersion);
                Assert.AreEqual(1u, h.DamageScaleVersion);
                Assert.AreEqual(17u, h.RegionCount);
                Assert.AreEqual((ulong)Win32.QpcFrequency(), h.QpcFrequency);
                Assert.AreEqual(0u, h.DevControl);
                Assert.AreEqual(4242u, h.SnPid);
                Assert.AreEqual(1u, h.SnGeneration);
                Assert.AreEqual(1u, r.Generation);
                Assert.AreEqual(SnPhase.Booting, h.SnPhase);
                Assert.AreEqual(SnFlags.BridgeMode, h.SnFlags);
                Assert.IsTrue(h.SnHeartbeatMs > 0);
                Assert.AreEqual(0u, h.SePid, "the SE block is SE's");
                for (int i = 0; i < 17; i++)
                {
                    RegionEntry e = h.Region(i);
                    RegionDef g = Layout.Regions[i];
                    Assert.AreEqual((uint)g.Id, e.id); Assert.AreEqual((uint)g.Kind, e.kind);
                    Assert.AreEqual((ulong)g.Offset, e.offset); Assert.AreEqual((ulong)g.Size, e.size);
                    Assert.AreEqual((uint)g.RecordSize, e.recordSize); Assert.AreEqual(1u, e.version);
                }
                // the raw bytes at the CONTRACT offsets
                Assert.AreEqual((byte)'D', s.Base[0]); Assert.AreEqual((byte)'E', s.Base[1]);
                Assert.AreEqual((byte)'E', s.Base[2]); Assert.AreEqual((byte)'P', s.Base[3]);
                Assert.AreEqual(4242u, *(uint*)(s.Base + 0x040));
                Assert.AreEqual(0x50u, *(uint*)(s.Base + 0x100 + 6 * 32), "region table row 6 = C5 id 0x50 (id order)");
                Assert.AreEqual(0x2000UL, *(ulong*)(s.Base + 0x100 + 6 * 32 + 8), "C5 offset");

                ValidationResult v = SectionValidation.Validate(s, new ValidationOptions { CheckRegionTable = true });
                Assert.IsTrue(v.Ok, v.ToString());
            }
        }

        [TestMethod]
        public void Creator_Reinitialise_DeadSnPid_KeepsHeadsAndSeq_ResyncsSnTails()
        {
            string name = TestUtil.UniqueSectionName();
            uint deadPid = TestUtil.DeadPid;
            CreatorResult first = SectionCreator.CreateOrAttach(TestUtil.SnOptions(name, deadPid));
            Assert.AreEqual(CreatorOutcome.Created, first.Outcome);
            using (SharedSection se = SharedSection.OpenExisting(name, Layout.SectionSize)) // SE keeps the section alive
            {
                first.Section.Dispose(); // the first Subnautica "exits"
                byte* b = se.Base;
                new HeaderView(b).AttachSe(777, 1);
                for (int r = 0; r < Layout.RingCount; r++)
                {
                    RingControl.PublishHead(b, (RingIndex)r, 1000UL + (ulong)r * 8);
                    RingControl.PublishTail(b, (RingIndex)r, 500UL + (ulong)r * 8);
                }
                Seqlock.Write(Layout.RegionPtr(b, RegionId.C5SeState), new SeState { tick = 9 });     // SE record
                *(uint*)Layout.RegionPtr(b, RegionId.C5SeState) = 4000;                                 // seq found
                *(uint*)Layout.RegionPtr(b, RegionId.C7SnStatus) = 600;                                 // SN record seq found
                *(uint*)(Layout.RegionPtr(b, RegionId.C10Capture) + CaptureLayout.RequestOffset) = 88;  // SE request seq

                bool sawMagicZero = false;
                CreatorOptions o = TestUtil.SnOptions(name, 5555);
                o.RewriteOwnRecords = sec =>
                {
                    var hv = sec.Header;
                    sawMagicZero = hv.Magic == 0 && hv.SnPhase == SnPhase.Booting && hv.SnGeneration == 2 &&
                                   RingControl.ReadTail(sec.Base, RingIndex.C4SeParts) == RingControl.ReadHead(sec.Base, RingIndex.C4SeParts);
                    SectionCreator.RewriteSnRecords(sec.Base);
                };
                CreatorResult second = SectionCreator.CreateOrAttach(o);
                Assert.AreEqual(CreatorOutcome.Reinitialised, second.Outcome, second.ToString());
                using (second.Section)
                {
                    Assert.IsTrue(sawMagicZero, "own records rewritten while magic = 0, after the SN block and the tails");
                    HeaderView h = new HeaderView(b);
                    Assert.AreEqual(WireInfo.Magic, h.Magic, "magic last");
                    Assert.AreEqual(2u, h.SnGeneration);
                    Assert.AreEqual(2u, second.Generation);
                    Assert.AreEqual(5555u, h.SnPid);
                    Assert.AreEqual(SnPhase.Booting, h.SnPhase);
                    Assert.AreEqual(777u, h.SePid, "SE block untouched");
                    for (int r = 0; r < Layout.RingCount; r++)
                    {
                        var ring = (RingIndex)r;
                        Assert.AreEqual(1000UL + (ulong)r * 8, RingControl.ReadHead(b, ring), "every head kept: " + ring);
                        bool snConsumes = ring == RingIndex.C3SeModels || ring == RingIndex.C4SeParts || ring == RingIndex.C11cCtlToSn;
                        Assert.AreEqual(snConsumes ? 1000UL + (ulong)r * 8 : 500UL + (ulong)r * 8, RingControl.ReadTail(b, ring),
                            (snConsumes ? "tail := head for " : "other consumers' tails untouched: ") + ring);
                    }
                    Assert.AreEqual(4000u, Seqlock.Current(Layout.RegionPtr(b, RegionId.C5SeState)), "SE seq untouched");
                    Assert.AreEqual(88u, Seqlock.Current(Layout.RegionPtr(b, RegionId.C10Capture)), "SE request seq untouched");
                    Assert.AreEqual(602u, Seqlock.Current(Layout.RegionPtr(b, RegionId.C7SnStatus)), "own C7 rewritten, seq continuing (never 0)");
                    Assert.AreEqual(SnPhase.Booting, ((SnStatus*)Layout.RegionPtr(b, RegionId.C7SnStatus))->snPhase);
                    Assert.IsTrue(SectionValidation.Validate(se).Ok);
                }
            }
        }

        private static void AssertRefusedUnchanged(string name, SharedSection fake, CreatorOptions o, string field)
        {
            var before = Resync.Snapshot(fake.Base, fake.MappedSize);
            CreatorResult r = SectionCreator.CreateOrAttach(o);
            Assert.AreEqual(CreatorOutcome.Refused, r.Outcome, "expected REFUSE on " + field + ", got " + r);
            Assert.AreEqual(field, r.Field, r.ToString());
            Assert.IsNotNull(r.Expected); Assert.IsNotNull(r.Found);
            Assert.IsNull(r.Section, "dormant: no section kept");
            TestUtil.AssertChecksumsEqual(before, Resync.Snapshot(fake.Base, fake.MappedSize), "refuse must write nothing");
        }

        /// <summary>A fake SE pre-creates the section with a valid layout-1.0 header (as a creator would) and then breaks one field.</summary>
        private static SharedSection FakePrecreated(string name, long size, Action<HeaderView> breakIt)
        {
            bool existed;
            SharedSection s = SharedSection.Create(name, size, TestUtil.Sddl, out existed);
            Assert.IsFalse(existed);
            var h = new HeaderView(s.Base);
            h.LayoutMajor = 1; h.LayoutMinor = 0; h.ByteOrderMark = WireInfo.ByteOrderMark; h.HeaderSize = 0x1000;
            h.SectionSize = 0x5000000; h.LayoutHash = LayoutHash.Compiled; h.WireVersion = 1; h.SiteVersion = 1;
            h.DamageScaleVersion = 1; h.RegionCount = 17; h.QpcFrequency = (ulong)Win32.QpcFrequency();
            if (s.MappedSize >= 0x2000) SectionCreator.WriteRegionTable(s.Base);
            h.SnPid = TestUtil.DeadPid; h.SnGeneration = 3;
            h.SetMagicRaw(WireInfo.Magic);
            breakIt(h);
            s.Base[s.MappedSize - 1] = 0xC3; // something to checksum at the far end
            return s;
        }

        [TestMethod]
        public void S0M6_Creator_RefusesLayout2_WritesNothing()
        {
            string name = TestUtil.UniqueSectionName();
            using (SharedSection fake = FakePrecreated(name, Layout.SectionSize, h => h.LayoutMajor = 2))
                AssertRefusedUnchanged(name, fake, TestUtil.SnOptions(name, 1), "layoutMajor");
        }

        [TestMethod]
        public void S0M6_Creator_RefusesADifferentSize_WritesNothing()
        {
            string name = TestUtil.UniqueSectionName();
            // the header claims the right size but the section is 64 MiB
            using (SharedSection fake = FakePrecreated(name, 0x4000000, h => { }))
            {
                Assert.AreEqual(0x4000000L, fake.MappedSize);
                AssertRefusedUnchanged(name, fake, TestUtil.SnOptions(name, 1), "sectionSize");
            }
            name = TestUtil.UniqueSectionName();
            // the header word differs
            using (SharedSection fake = FakePrecreated(name, Layout.SectionSize, h => h.SectionSize = 0x4000000))
                AssertRefusedUnchanged(name, fake, TestUtil.SnOptions(name, 1), "sectionSize");
            name = TestUtil.UniqueSectionName();
            // a bigger section
            using (SharedSection fake = FakePrecreated(name, 0x5001000, h => { }))
                AssertRefusedUnchanged(name, fake, TestUtil.SnOptions(name, 1), "sectionSize");
        }

        [TestMethod]
        public void S0M6_Creator_RefusesWrongMagicOrHash_WritesNothing()
        {
            string name = TestUtil.UniqueSectionName();
            using (SharedSection fake = FakePrecreated(name, Layout.SectionSize, h => h.SetMagicRaw(0x51454544))) // "DEEQ"
                AssertRefusedUnchanged(name, fake, TestUtil.SnOptions(name, 1), "magic");
            name = TestUtil.UniqueSectionName();
            using (SharedSection fake = FakePrecreated(name, Layout.SectionSize, h => h.LayoutHash = 0x1234))
                AssertRefusedUnchanged(name, fake, TestUtil.SnOptions(name, 1), "layoutHash");
            name = TestUtil.UniqueSectionName();
            using (SharedSection fake = FakePrecreated(name, Layout.SectionSize, h => h.SetMagicRaw(0)))   // half-initialised
                AssertRefusedUnchanged(name, fake, TestUtil.SnOptions(name, 1), "magic");
        }

        [TestMethod]
        public void Creator_RefusesASectionLeftByAnotherVersion_WritesNothing()
        {
            // Review finding 1: rule 3 rewrites only the SN block, so a restarted creator must not re-initialise a
 // section whose remaining identity words (validation steps 1-5) or region table are not its own. The
 // recorded snPid is dead in every case: only the identity word makes the creator refuse.
            var cases = new System.Collections.Generic.List<Tuple<string, Action<HeaderView>>>
            {
                Tuple.Create<string, Action<HeaderView>>("wireVersion", h => h.WireVersion = 2),
                Tuple.Create<string, Action<HeaderView>>("siteVersion", h => h.SiteVersion = 2),
                Tuple.Create<string, Action<HeaderView>>("damageScaleVersion", h => h.DamageScaleVersion = 2),
                Tuple.Create<string, Action<HeaderView>>("byteOrderMark", h => h.ByteOrderMark = 0x04030201),
                Tuple.Create<string, Action<HeaderView>>("headerSize", h => h.HeaderSize = 0x2000),
                Tuple.Create<string, Action<HeaderView>>("qpcFrequency", h => h.QpcFrequency = 1),
                Tuple.Create<string, Action<HeaderView>>("regionCount", h => h.RegionCount = 16),
            };
            foreach (var c in cases)
            {
                string name = TestUtil.UniqueSectionName();
                using (SharedSection fake = FakePrecreated(name, Layout.SectionSize, c.Item2))
                    AssertRefusedUnchanged(name, fake, TestUtil.SnOptions(name, 1), c.Item1);
            }
            string n2 = TestUtil.UniqueSectionName();
            using (SharedSection fake = FakePrecreated(n2, Layout.SectionSize, h => { }))
            {
                *(ulong*)(fake.Base + Layout.RegionTableOffset + 5 * Layout.RegionEntrySize + 8) += 0x1000; // region 5 offset
                AssertRefusedUnchanged(n2, fake, TestUtil.SnOptions(n2, 1), "region[5]");
            }
            // the same section with every word right is re-initialised (control)
            string n3 = TestUtil.UniqueSectionName();
            using (SharedSection fake = FakePrecreated(n3, Layout.SectionSize, h => { }))
            {
                CreatorResult r = SectionCreator.CreateOrAttach(TestUtil.SnOptions(n3, 1));
                Assert.AreEqual(CreatorOutcome.Reinitialised, r.Outcome, r.ToString());
                r.Section.Dispose();
            }
        }

        [TestMethod]
        public void Header_IdentityPairs_AreOneU64_LowWordPid()
        {
            // Review finding 9: PID and generation travel as one aligned u64 (atomic on x64); layout unchanged.
            using (var buf = new NativeBuffer(Layout.HeaderSize))
            {
                var h = new HeaderView(buf.Ptr);
                h.WriteSeIdentity(0x11223344u, 0x55667788u);
                Assert.AreEqual(0x11223344u, *(uint*)(buf.Ptr + 0x080));
                Assert.AreEqual(0x55667788u, *(uint*)(buf.Ptr + 0x084));
                Assert.AreEqual(0x11223344u, h.SePid);
                Assert.AreEqual(0x55667788u, h.SeGeneration);
                uint p, g;
                h.ReadSeIdentity(out p, out g);
                Assert.AreEqual(0x11223344u, p); Assert.AreEqual(0x55667788u, g);
                h.WriteSnIdentity(7, 9);
                Assert.AreEqual(7u, *(uint*)(buf.Ptr + 0x040)); Assert.AreEqual(9u, *(uint*)(buf.Ptr + 0x044));
                h.ReadSnIdentity(out p, out g);
                Assert.AreEqual(7u, p); Assert.AreEqual(9u, g);
                Assert.AreEqual(0x55667789u, h.AttachSe(42, 1000), "found + 1");
                Assert.AreEqual(42u, h.SePid);
                Assert.AreEqual(1000UL, h.SeHeartbeatMs);
                Assert.AreEqual(0UL, ((ulong)buf.Ptr + 0x080) % 8, "aligned");
            }
        }

        [TestMethod]
        public void S0N10_Creator_RefusesWhileTheRecordedSnPidIsAlive_WritesNothing()
        {
            string name = TestUtil.UniqueSectionName();
            // a first Subnautica (this very process: alive) holds the section
            using (SharedSection fake = FakePrecreated(name, Layout.SectionSize, h => h.SnPid = Win32.CurrentProcessId()))
            {
                var o = TestUtil.SnOptions(name, 1); // real liveness check: OpenProcess + WaitForSingleObject(h, 0)
                AssertRefusedUnchanged(name, fake, o, "snPid");
            }
        }

        [TestMethod]
        public void Creator_RecordedSnPidIsOwnPid_PidReuse_Reinitialises()
        {
            // The previous Subnautica died and the OS gave its PID to the new one while SE kept the section open:
 // the recorded snPid is the creator's own (alive) PID. It must count as stale, not as a live holder.
            string name = TestUtil.UniqueSectionName();
            uint me = Win32.CurrentProcessId();
            using (SharedSection fake = FakePrecreated(name, Layout.SectionSize, h => h.SnPid = me))
            {
                CreatorOptions o = TestUtil.SnOptions(name, me); // real liveness check: our own PID is alive
                CreatorResult r = SectionCreator.CreateOrAttach(o);
                Assert.AreEqual(CreatorOutcome.Reinitialised, r.Outcome, r.ToString());
                using (r.Section)
                {
                    Assert.AreEqual(4u, r.Generation, "generation found (3) + 1");
                    Assert.AreEqual(me, fake.Header.SnPid);
                    Assert.AreEqual(WireInfo.Magic, fake.Header.Magic);
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ validation

        private static ValidationResult ValidateAfter(SharedSection s, Action<HeaderView> change, ValidationOptions o = null)
        {
            var h = s.Header;
            var saved = new byte[Layout.HeaderSize];
            Marshal.Copy((IntPtr)s.Base, saved, 0, saved.Length);
            change(h);
            var before = Resync.Snapshot(s.Base, s.MappedSize);
            ValidationResult r = SectionValidation.Validate(s, o);
            TestUtil.AssertChecksumsEqual(before, Resync.Snapshot(s.Base, s.MappedSize), "validation reads only");
            Marshal.Copy(saved, 0, (IntPtr)s.Base, saved.Length);
            return r;
        }

        private static void AssertReject(ValidationResult r, int step, string field, string expected, string found)
        {
            Assert.AreEqual(ValidationOutcome.Reject, r.Outcome, r.ToString());
            Assert.AreEqual(step, r.Step, r.ToString());
            Assert.AreEqual(field, r.Field, r.ToString());
            Assert.AreEqual(expected, r.Expected, r.ToString());
            Assert.AreEqual(found, r.Found, r.ToString());
        }

        [TestMethod]
        public void S0M1_Validation_NamesTheFieldAndBothValues()
        {
            string name = TestUtil.UniqueSectionName();
            CreatorResult c = SectionCreator.CreateOrAttach(TestUtil.SnOptions(name, TestUtil.DeadPid));
            using (SharedSection sn = c.Section)
            using (SharedSection s = SharedSection.OpenExisting(name, Layout.SectionSize))
            {
                Assert.IsTrue(SectionValidation.Validate(s).Ok);
                AssertReject(ValidateAfter(s, h => h.SetMagicRaw(0x51454544)), 1, "magic", "0x50454544", "0x51454544"); // DEEQ (S0.M1 first run)
                AssertReject(ValidateAfter(s, h => h.ByteOrderMark = 0x04030201), 1, "byteOrderMark", "0x1020304", "0x4030201");
                AssertReject(ValidateAfter(s, h => h.HeaderSize = 0x2000), 1, "headerSize", "0x1000", "0x2000");
                AssertReject(ValidateAfter(s, h => h.SectionSize = 0x6000000), 1, "sectionSize", "0x5000000", "0x6000000");
                AssertReject(ValidateAfter(s, h => h.LayoutMajor = 2), 2, "layoutMajor", "1", "2");
                AssertReject(ValidateAfter(s, h => h.LayoutHash = 0xDEAD), 3, "layoutHash", LayoutHash.Format(LayoutHash.Compiled), "0x000000000000dead");
                AssertReject(ValidateAfter(s, h => h.WireVersion = 2), 4, "wireVersion", "1", "2");                    // S0.M1 second run
                AssertReject(ValidateAfter(s, h => h.SiteVersion = 2), 4, "siteVersion", "1", "2");
                AssertReject(ValidateAfter(s, h => h.DamageScaleVersion = 3), 4, "damageScaleVersion", "1", "3");
                AssertReject(ValidateAfter(s, h => { }, new ValidationOptions { QpcFrequency = 1 }), 5, "qpcFrequency", "1", Win32.QpcFrequency().ToString());
                AssertReject(ValidateAfter(s, h => { }, new ValidationOptions { SelfTestPass = false }), 6, "wm1SelfTest", "PASS", "FAIL");

                ValidationResult notReady = ValidateAfter(s, h => h.SetMagicRaw(0));
                Assert.AreEqual(ValidationOutcome.NotReady, notReady.Outcome, "magic 0 = creator still initialising → WAIT");
                Assert.AreEqual("magic", notReady.Field);

                ValidationResult minor = ValidateAfter(s, h => h.LayoutMinor = 1);
                Assert.IsTrue(minor.Ok, "a different minor is accepted");
                Assert.AreEqual(1, minor.Notes.Count, "and logged");

                ValidationResult table = ValidateAfter(s, h => *(uint*)(s.Base + 0x100 + 5 * 32 + 8) = 0x9000,
                    new ValidationOptions { CheckRegionTable = true });
                Assert.AreEqual(ValidationOutcome.Reject, table.Outcome);
                Assert.AreEqual("region[5]", table.Field);
                Assert.IsTrue(SectionValidation.Validate(s, new ValidationOptions { CheckRegionTable = true, SelfTestPass = true }).Ok, "restored");
            }
        }

        [TestMethod]
        public void S0M1_Validation_MappedSizeDiffers()
        {
            string name = TestUtil.UniqueSectionName();
            using (SharedSection fake = FakePrecreated(name, 0x4000000, h => { }))
            using (SharedSection s = SharedSection.OpenExisting(name, Layout.SectionSize))
            {
                Assert.AreEqual(0x4000000L, s.MappedSize);
                ValidationResult r = SectionValidation.Validate(s);
                Assert.AreEqual(ValidationOutcome.Reject, r.Outcome);
                Assert.AreEqual("sectionSize", r.Field);
                StringAssert.Contains(r.Found, "mapped");
            }
        }

        [TestMethod]
        public void Opener_AttachSe_BumpsGenerationAndWritesOnlyTheSeBlock()
        {
            string name = TestUtil.UniqueSectionName();
            CreatorResult c = SectionCreator.CreateOrAttach(TestUtil.SnOptions(name, TestUtil.DeadPid));
            using (c.Section)
            using (SharedSection s = SharedSection.OpenExisting(name, Layout.SectionSize))
            {
                byte* b = s.Base;
                ulong snBlock = LayoutHash.Fnv1a64(b, 0x80);
                ulong rest = LayoutHash.Fnv1a64(b + 0x100, Layout.SectionSize - 0x100);
                HeaderView h = s.Header;
                Assert.AreEqual(1u, h.AttachSe(31337, 12345));
                Assert.AreEqual(2u, h.AttachSe(31337, 12346), "every attach bumps the generation");
                Assert.AreEqual(31337u, h.SePid);
                Assert.AreEqual(12346UL, h.SeHeartbeatMs);
                Assert.AreEqual(snBlock, LayoutHash.Fnv1a64(b, 0x80), "common words and SN block untouched");
                Assert.AreEqual(rest, LayoutHash.Fnv1a64(b + 0x100, Layout.SectionSize - 0x100), "nothing else written");
            }
        }
    }
}
