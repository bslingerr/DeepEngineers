using System;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>

 /// a Subnautica that stalls or exits (CONTRACT §6 "Link epoch", §7 "Subnautica stalls" / "Subnautica exits, crashes or
 /// quits"; ARCHITECTURE §4.3 PeerStalled / PeerExited, §5.14). Game-independent: every input is a value, no clock, no
 /// game type (SE.Tests S8Tests). The game glue is <see cref="S2Host"/> (decision per plugin Update, the HUD notice,
 /// the audio, the <c>s8.peer</c> / <c>s8.notice</c> rows) and <see cref="DisplayController"/> (the peer-loss guard of
 /// every display request).
 /// <para>The fallback decision table (peer state × view state × time since the loss):</para>
 /// <list type="table">
 /// <item><term>peer Alive</term><description>no fallback; the view machine and the mix run as usual.</description></item>
 /// <item><term>never alive on this link</term><description>no fallback, no notice: plain SE ("renderer offline" mode,
 /// CONTRACT §7 "SE without Subnautica").</description></item>
 /// <item><term>lost (Stalled, Exited, a new generation not judged yet, or the link detached) — any view</term><description>
 /// scene drawn on this frame (no skip, A′ not lowered, whatever a dev override asks), margins off after one presented
 /// scene-drawn frame (the render thread's acknowledgement), the S7a picture latch dropped (the crossfade snaps), the
 /// audio restored at once (no 300 ms ramp: SE's config volumes and the Water Mod's underwater sound back).</description></item>
 /// <item><term>lost while Underwater / Surfacing, or with Subnautica's picture on (S7a Site latch)</term><description>
 /// the HUD notice "underwater renderer offline" at once.</description></item>
 /// <item><term>lost elsewhere (Space, Track, Prime under Waterline)</term><description>the notice once the loss has
 /// lasted <see cref="NoticeDelayMs"/> (10 s), in a world with the site bound (hello published); never at the menu or
 /// while loading (it shows when such a world is entered and the loss is still on).</description></item>
 /// <item><term>back Alive (same PID after a stall: link epoch + 1; or a new generation)</term><description>the notice
 /// is armed again; the stream gate resends everything after the acknowledgement (<see cref="ResendOnRecovery"/>).</description></item>
 /// </list>
 /// One notice per loss. The texts tell the player what happens and what to do (<see cref="NoticeText"/>).
 /// </summary>
    internal static class S8Rules
    {
        /// <summary>ARCHITECTURE §4.3: the notice shows at once underwater, after 10 s elsewhere.</summary>
        public const ulong NoticeDelayMs = 10000;
        /// <summary>How long the HUD notice stays (IMyUtilities.ShowNotification disappearTimeMs).</summary>
        public const int NoticeShowMs = 8000;
        /// <summary>CONTRACT §6 "Link epoch": a stall of at least this long always resends everything.</summary>
        public const ulong ResendAfterStallMs = 10000;
        /// <summary>ARCHITECTURE §5.14: the peer process exit is polled once per second while no margins are on.</summary>
        public const ulong ExitPollSlowMs = 1000;

        public const string NoticeStalled = "DeepEngineers: underwater renderer offline (Subnautica is not responding) - " +
                                            "Space Engineers shows its own water until it recovers";
        public const string NoticeExited = "DeepEngineers: underwater renderer offline (Subnautica closed) - Space Engineers " +
                                           "shows its own water; restart the renderer from the launcher (restart-renderer)";

        /// <summary>What the plugin knows this update (S2Host fills it from the link and the last view step).</summary>
        internal struct Inputs
        {
            /// <summary>The section is attached (Link Up). False: searching or detached.</summary>
            public bool Linked;
            /// <summary>Wire.PeerState of the link (Unknown 0, Alive 1, Stalled 2, Exited 3).</summary>
            public uint Peer;
            /// <summary>Wire.ViewState of the last machine step (Space when the machine did not step).</summary>
            public uint ViewState;
            /// <summary>The S7a picture latch of the last step (Subnautica's picture shown).</summary>
            public bool PictureOn;
            /// <summary>SE in a world (InWorld, not loading).</summary>
            public bool InWorld;
            /// <summary>The site is bound and the hello published (a DE world).</summary>
            public bool SiteActive;
            public ulong NowMs;
        }

        /// <summary>The state the plugin keeps between updates (one per SE process).</summary>
        internal struct Memory
        {
            /// <summary>The peer of this link was Alive at least once (a later loss is a loss, not a start).</summary>
            public bool EverAlive;
            public bool Lost;
            public ulong SinceMs;
            /// <summary>The peer state at the loss (Stalled → the stalled text; anything else → the exited text).</summary>
            public uint LostPeer;
            /// <summary>The loss happened with Subnautica's picture on screen (Underwater, Surfacing or the latch on).</summary>
            public bool Urgent;
            public bool Shown;
            public long Losses, Recoveries, Notices;
        }

        /// <summary>The decision of one update.</summary>
        internal struct Decision
        {
            /// <summary>The fallback holds: scene drawn, margins off (render-acked), latch dropped, audio restored now.</summary>
            public bool Active;
            /// <summary>This update is the loss edge (alive → lost).</summary>
            public bool LostEdge;
            /// <summary>This update is the recovery edge (lost → alive).</summary>
            public bool RecoveredEdge;
            /// <summary>The fallback changes what is on screen now (a view other than Space, or the picture latch on).</summary>
            public bool Visible;
            /// <summary>Show the HUD notice now (once per loss; the caller shows it and the memory records it).</summary>
            public bool NoticeNow;
            /// <summary>When the notice is due (0: never in this loss, e.g. not lost).</summary>
            public ulong NoticeAtMs;
            /// <summary>The loss lasted this long (0 when not lost; on the recovery edge, the whole loss).</summary>
            public ulong LostForMs;
            public string Why;
        }

        /// <summary>
 /// The fallback decision table above, one update. The memory records the edges and the notice; the caller shows the
 /// notice when <see cref="Decision.NoticeNow"/>.
 /// </summary>
        public static Decision Decide(ref Inputs i, ref Memory m)
        {
            var d = new Decision();
            bool alive = i.Linked && i.Peer == PeerState.Alive;
            if (alive)
            {
                if (m.Lost)
                {
                    d.RecoveredEdge = true;
                    d.LostForMs = i.NowMs >= m.SinceMs ? i.NowMs - m.SinceMs : 0;
                    m.Recoveries++;
                }
                m.EverAlive = true;
                m.Lost = false;
                m.Shown = false;
                m.Urgent = false;
                d.Why = "peer alive";
                return d;
            }
            if (!m.EverAlive)
            {
                d.Why = i.Linked ? "no live peer yet on this link (start)" : "renderer offline (never linked)";
                return d;
            }
            bool onScreen = i.ViewState == ViewState.Underwater || i.ViewState == ViewState.Surfacing || i.PictureOn;
            if (!m.Lost)
            {
                m.Lost = true;
                m.SinceMs = i.NowMs;
                m.LostPeer = i.Linked ? i.Peer : PeerState.Exited;
                m.Urgent = onScreen;
                m.Shown = false;
                m.Losses++;
                d.LostEdge = true;
            }
            else if (i.Linked && i.Peer == PeerState.Exited) m.LostPeer = PeerState.Exited; // a stall that ended in an exit
            d.Active = true;
            d.Visible = onScreen;
            d.LostForMs = i.NowMs >= m.SinceMs ? i.NowMs - m.SinceMs : 0;
            d.NoticeAtMs = m.SinceMs + (m.Urgent ? 0UL : NoticeDelayMs);
            bool due = i.NowMs >= d.NoticeAtMs;
            d.NoticeNow = !m.Shown && due && i.InWorld && i.SiteActive;
            if (d.NoticeNow)
            {
                m.Shown = true;
                m.Notices++;
            }
            d.Why = (!i.Linked ? "link detached" : PeerName(i.Peer)) + (m.Urgent ? " with Subnautica's picture on screen" : " (no picture depends on it)");
            return d;
        }

        /// <summary>The notice text for the peer state at the loss.</summary>
        public static string NoticeText(uint lostPeer)
        {
            return lostPeer == PeerState.Stalled ? NoticeStalled : NoticeExited;
        }

        /// <summary>
 /// The display guard (DisplayController, every request): a linked peer that is not alive (stalled, or a new
 /// generation not judged yet) never gets the scene skipped or SE lowered. An exited peer is unlinked
 /// (LinkFacts.Linked false), which the render thread already treats the same way.
 /// </summary>
        public static bool PeerLost(bool linked, bool peerAlive)
        {
            return linked && !peerAlive;
        }

        /// <summary>
 /// The audio part of the fallback: SE's config volumes and the Water Mod's underwater sound at once (the machine's
 /// own mix would ramp back over 300 ms after leaving Underwater). Applied to a copy of the machine's outputs.
 /// </summary>
        public static void RestoreAudio(ref ViewOutputs o)
        {
            o.SeGameFactor = 1f;
            o.SeMusicFactor = 1f;
            o.WaterModFactor = 1f;
            o.MixLevel = 0.0;
            o.SeMixActive = false;
            o.SnBusTarget = 0f;
        }

        /// <summary>
 /// ARCHITECTURE §5.14: the peer process exit is polled every tick while SE's DWM margins are on or requested, or SE is
 /// lowered under Subnautica (A′), once per second otherwise.
 /// </summary>
        public static bool ExitPollEveryTick(bool marginsOn, bool lowered, bool requestMargins, bool requestLowered)
        {
            return marginsOn || lowered || requestMargins || requestLowered;
        }

        /// <summary>
 /// CONTRACT §6 "Link epoch": after a recovery SE resends everything when the stall lasted ≥ 10 s or Subnautica's
 /// counters disagree with what SE sent; otherwise it may continue. The SE stream gate (S3Logic StreamGate) is
 /// stricter and resends after every recovery; this rule is the contract's minimum, logged in <c>s8.peer</c>.
 /// </summary>
        public static bool ResendOnRecovery(ulong stalledForMs, bool countersAgree)
        {
            return stalledForMs >= ResendAfterStallMs || !countersAgree;
        }

        public static string PeerName(uint peer)
        {
            switch (peer)
            {
                case PeerState.Alive: return "Alive";
                case PeerState.Stalled: return "Stalled";
                case PeerState.Exited: return "Exited";
                default: return "Unknown";
            }
        }

        // ======================================================================================================= rows

 /// <summary>
 /// One <c>s8.peer</c> JSONL row (the trace of S8.T-L1a/b/g): on every change of the link's peer state or the link
 /// itself. Keys: tick, from, to, linked, linkEpoch, snPid, snGen, reason, view, picture, fallback, visible,
 /// noticeAtMs, lostForMs, resendRule.
 /// </summary>
        public static string PeerRow(string utc, ulong tick, uint fromPeer, bool fromLinked, uint toPeer, bool toLinked, uint linkEpoch, uint snPid,
                                     uint snGen, string reason, uint viewState, bool picture, ref Decision d)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(384);
            sb.Append("{\"t\":"); JsonText.AppendString(sb, utc);
            sb.Append(",\"ev\":\"s8.peer\",\"tick\":").Append(tick.ToString(ci));
            sb.Append(",\"from\":\"").Append(fromLinked ? PeerName(fromPeer) : "Offline").Append('"');
            sb.Append(",\"to\":\"").Append(toLinked ? PeerName(toPeer) : "Offline").Append('"');
            sb.Append(",\"linked\":").Append(toLinked ? "true" : "false");
            sb.Append(",\"linkEpoch\":").Append(linkEpoch.ToString(ci));
            sb.Append(",\"snPid\":").Append(snPid.ToString(ci));
            sb.Append(",\"snGen\":").Append(snGen.ToString(ci));
            sb.Append(",\"reason\":"); JsonText.AppendString(sb, reason ?? "");
            sb.Append(",\"view\":\"").Append(ViewName(viewState)).Append('"');
            sb.Append(",\"picture\":").Append(picture ? "true" : "false");
            sb.Append(",\"fallback\":").Append(d.Active ? "true" : "false");
            sb.Append(",\"visible\":").Append(d.Visible ? "true" : "false");
            sb.Append(",\"noticeAtMs\":").Append(d.NoticeAtMs.ToString(ci));
            sb.Append(",\"lostForMs\":").Append(d.LostForMs.ToString(ci));
            sb.Append(",\"resendRule\":\"").Append(d.RecoveredEdge ? (ResendOnRecovery(d.LostForMs, true) ? "contract: resend (stall >= 10 s); gate: resend" : "contract: continue or resend; gate: resend") : "-").Append('"');
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>One <c>s8.notice</c> row: the HUD notice shown (kind, the delay it waited, the view).</summary>
        public static string NoticeRow(string utc, ulong tick, uint lostPeer, ulong lostForMs, bool urgent, uint viewState)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(256);
            sb.Append("{\"t\":"); JsonText.AppendString(sb, utc);
            sb.Append(",\"ev\":\"s8.notice\",\"tick\":").Append(tick.ToString(ci));
            sb.Append(",\"kind\":\"").Append(lostPeer == PeerState.Stalled ? "stalled" : "exited").Append('"');
            sb.Append(",\"lostForMs\":").Append(lostForMs.ToString(ci));
            sb.Append(",\"urgent\":").Append(urgent ? "true" : "false");
            sb.Append(",\"view\":\"").Append(ViewName(viewState)).Append('"');
            sb.Append(",\"text\":"); JsonText.AppendString(sb, NoticeText(lostPeer));
            sb.Append('}');
            return sb.ToString();
        }

        public static string ViewName(uint v)
        {
            switch (v)
            {
                case ViewState.Space: return "Space";
                case ViewState.Approach: return "Approach";
                case ViewState.Underwater: return "Underwater";
                case ViewState.Surfacing: return "Surfacing";
                default: return "?";
            }
        }

        // ===================================================================================================== memory

 /// <summary>
 /// The S8.N3 memory columns of the per-second <c>sample</c> row (PerfLog): MB values rounded to 0.1, the machine's
 /// memory load and commit charge in percent (commit = 1 − availPageFile / totalPageFile). Pure formatting.
 /// </summary>
        public static void AppendMemory(StringBuilder sb, long workingSetBytes, long privateBytes, long gcHeapBytes, int gc0, int gc1, int gc2,
                                        uint memoryLoadPct, double commitPct)
        {
            var ci = CultureInfo.InvariantCulture;
            sb.Append(",\"workingSetMB\":").Append(Mb(workingSetBytes).ToString("0.0", ci));
            sb.Append(",\"privateMB\":").Append(Mb(privateBytes).ToString("0.0", ci));
            sb.Append(",\"gcHeapMB\":").Append(Mb(gcHeapBytes).ToString("0.0", ci));
            sb.Append(",\"gc0\":").Append(gc0.ToString(ci)).Append(",\"gc1\":").Append(gc1.ToString(ci)).Append(",\"gc2\":").Append(gc2.ToString(ci));
            sb.Append(",\"memoryLoadPct\":").Append(memoryLoadPct.ToString(ci));
            sb.Append(",\"commitPct\":").Append(double.IsNaN(commitPct) ? "null" : commitPct.ToString("0.0", ci));
        }

        public static double Mb(long bytes)
        {
            return bytes <= 0 ? 0.0 : Math.Round(bytes / (1024.0 * 1024.0), 1);
        }

        /// <summary>Commit charge in percent of the commit limit (NaN when the limit is unknown).</summary>
        public static double CommitPct(ulong totalPageFile, ulong availPageFile)
        {
            if (totalPageFile == 0 || availPageFile > totalPageFile) return double.NaN;
            return Math.Round(100.0 * (totalPageFile - availPageFile) / totalPageFile, 1);
        }
    }
}
