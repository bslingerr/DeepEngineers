using System;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;

namespace DeepEngineers.SN
{
    /// <summary>
 /// Section creation by the CONTRACT §6 creator rules, through the Wire helper (SectionCreator). The SN plugin is the
 /// only creator; it holds `Local\DeepEngineers_v1_sn` while it does this.
 /// </summary>
    internal static unsafe class SectionLink
    {
        /// <summary>
 /// Rules 1-3: a new section is initialised (generation 1); an existing compatible section whose recorded snPid
 /// is dead is re-initialised in place (generation + 1, consumer tails C3/C4/C11c:= heads, producer heads and
 /// seqlock seq words kept, own records rewritten, magic last); anything else is refused without writing.
 /// </summary>
        public static CreatorResult Create(uint snFlags, ulong snHwnd, string sectionName = WireInfo.SectionName)
        {
            var o = new CreatorOptions
            {
                Name = sectionName,
                SnFlags = snFlags,
                SnHwnd = snHwnd,
                RewriteOwnRecords = s => RewriteOwnRecords(s, snHwnd),
            };
            return SectionCreator.CreateOrAttach(o);
        }

        /// <summary>
 /// Rules 1-3, then the validation of both sides (CONTRACT §6 steps 1-5 and the region table) on the section just
 /// created or re-initialised. Review finding 1: a creator never bridges on a section it cannot validate; on a
 /// failure the section is withdrawn (snPhase None, view closed) and the caller stays dormant.
 /// </summary>
        public static BridgeSection CreateValidated(uint snFlags, ulong snHwnd, string sectionName = WireInfo.SectionName)
        {
            var r = new BridgeSection { Creator = Create(snFlags, snHwnd, sectionName) };
            if (r.Creator.Section == null) return r;
            r.Own = ValidateOwn(r.Creator.Section, null);
            if (Classify(r.Own) != OwnValidation.Ok)
            {
                Withdraw(r.Creator.Section);
                r.Withdrawn = true;
            }
            return r;
        }

        /// <summary>Validation steps 1-5 (step 6 when <paramref name="selfTestPass"/> is known) plus the region table.</summary>
        public static ValidationResult ValidateOwn(SharedSection s, bool? selfTestPass)
        {
            return SectionValidation.Validate(s, new ValidationOptions { SelfTestPass = selfTestPass, CheckRegionTable = true });
        }

        /// <summary>
 /// What a validation of our own section means for the bridge: OK; DisplayOff (step 6, the WM-1 self-check, failed:
 /// the link stays up with C7 wireSelfTestPass = 0 so SE keeps the display bridge off); Withdraw (any other step,
 /// or magic 0: the section is not the one this creator wrote).
 /// </summary>
        public static OwnValidation Classify(ValidationResult v)
        {
            if (v == null) return OwnValidation.Withdraw;
            if (v.Ok) return OwnValidation.Ok;
            if (v.Outcome == ValidationOutcome.Reject && v.Step == 6) return OwnValidation.DisplayOff;
            return OwnValidation.Withdraw;
        }

        /// <summary>snPhase None (an SE that already opened the section reads PeerExited), then the view is closed.</summary>
        public static void Withdraw(SharedSection s)
        {
            if (s == null) return;
            try
            {
                if (s.IsOpen) { HeaderView hv = s.Header; hv.SnPhase = SnPhase.None; }
            }
            catch (Exception) { }
            s.Dispose();
        }

        /// <summary>
 /// S7 (statics into SE): the C12 <c>SnStatics</c> producer of this section (CONTRACT §5.1: SN → SE byte ring, 4 MiB;
 /// "producer tries once, re-queues the batch"). Built after the creator step, it continues from the head it finds
 /// (a producer never zeroes a head). Used by <c>S7Statics</c>; one producer per section.
 /// </summary>
        public static ByteRing C12Producer(byte* sectionBase)
        {
            return new ByteRing(sectionBase, RingIndex.C12SnStatics);
        }

        /// <summary>
 /// Creator rule 3 "rewrite its own seqlock records in place" (called while magic is 0): C7 SnStatus (Booting,
 /// hostKind 1), the C9 header and the C10 result, each `seq` continuing from the value found. Uses the
 /// non-generic seqlock writer (no `unmanaged` generic constraint on the Unity Mono path).
 /// </summary>
        private static void RewriteOwnRecords(SharedSection s, ulong snHwnd)
        {
            byte* b = s.Base;
            SnStatus st = default(SnStatus);
            st.snPhase = SnPhase.Booting;
            st.hostKind = HostKind.Subnautica1;
            st.snHwnd = snHwnd;
            Seqlock.WriteBytes(Layout.RegionPtr(b, RegionId.C7SnStatus), (byte*)&st, sizeof(SnStatus));
            SnCreaturesHeader ch = default(SnCreaturesHeader);
            Seqlock.WriteBytes(Layout.RegionPtr(b, RegionId.C9SnCreatures), (byte*)&ch, sizeof(SnCreaturesHeader));
            CaptureResult cr = default(CaptureResult);
            Seqlock.WriteBytes(Layout.RegionPtr(b, RegionId.C10Capture) + CaptureLayout.ResultOffset, (byte*)&cr, sizeof(CaptureResult));
        }
    }

    internal enum OwnValidation
    {
        Ok = 0,
        /// <summary>Validation step 6 failed: display bridge off (C7 wireSelfTestPass = 0); the link stays up.</summary>
        DisplayOff = 1,
        /// <summary>Steps 1-5, the region table, or magic 0: withdraw the bridge (dormant).</summary>
        Withdraw = 2,
    }

    /// <summary>Outcome of <see cref="SectionLink.CreateValidated"/>.</summary>
    internal sealed class BridgeSection
    {
        public CreatorResult Creator;
        /// <summary>Validation of the section after rule 1 or 3 (null when nothing was created).</summary>
        public ValidationResult Own;
        /// <summary>The own validation failed: the section was withdrawn (snPhase None, closed); stay dormant.</summary>
        public bool Withdrawn;
    }
}
