using System;

namespace DeepEngineers.Wire
{
    /// <summary>
 /// One JSONL row per view-machine step ("vsm" rows of <c>se-*.jsonl</c>), replayed by the independent oracle
 /// <c>tests/s2/state_traces.py</c>. The row holds the step index, the inputs, the state before the step and the
 /// outputs, so the oracle can replay contiguous rows with its own state and reseed from a row after a gap (the
 /// plugin may log every step only around transitions). Doubles that thresholds compare are logged twice: a rounded
 /// decimal for people and the exact IEEE-754 bits (16 lower-case hex digits) for the replay.
 /// <code>
 /// {"ev":"vsm","v","n":step,"tick":T,"us":nowUs,
 /// inputs "bu","site","zone","rho","rhob","y","yb","vy","vyb","peer","ph","snf","pst","abt","zok","pau","fg",
 /// "gui","cur","ld","kau","evt","ack","mrd","dm","mut","pp",
 /// state "s_v","s_a","s_sw","s_ls","s_pc","s_bw","s_xt","s_rp","s_rt","s_rs","s_rq","s_ma","s_pf","s_pz",
 /// (before) "s_mf"(bits),"s_ms","s_mu","s_sp","s_bt","s_ps","s_pt","s_la","s_lt",
 /// outputs "o_vs","o_am","o_pm","o_fps","o_rq","o_rs","o_zr","o_fz","o_xt","o_rdy","o_blk","o_ov","o_c5f",
 /// "o_bus","o_xf","o_sg","o_sm","o_wm","o_aw","o_ma","o_mix"(bits),"o_rsn","o_sp","o_pe","o_pb","o_prs","o_pp"}
 /// </code>
 /// Booleans are 0/1. Formatting allocates nothing (<see cref="Format"/> writes into a caller-owned buffer).
 /// Version 2 adds the input <c>mrd</c> (<see cref="ViewInputs.MarginsReady"/>); a version 1
 /// row has no <c>mrd</c> and replays as <c>mrd</c> = 1 (its acknowledgement implied the margins).
 /// Version 3 has the same keys and marks the machine whose Prime quiet timeout is 10 s
 /// below +5 m (<see cref="ViewThresholds.PrimeQuietNearUs"/>); the oracle replays version 1 and 2 rows with the
 /// 3 s timeout at every altitude (the machine that wrote them).
 /// Version 4 adds the picture policy: input <c>pp</c> (<see cref="ViewInputs.Picture"/>), state
 /// <c>s_sp</c> (the picture latch), <c>s_bt</c> (bandTick), <c>s_ps</c>/<c>s_pt</c> (the last latch change), outputs
 /// <c>o_sp</c> (picture), <c>o_pe</c> (picture edge −1/0/+1), <c>o_pb</c> (picture blocked), <c>o_prs</c> (picture
 /// reason), <c>o_pp</c> (effective policy). The oracle replays rows of version 3 or lower as the <c>Waterline</c>
 /// policy (the S2 machine that wrote them).
 /// </summary>
    public static class ViewTrace
    {
        public const string Event = "vsm";
        public const int Version = 5;   // 5: s_la/s_lt (the altitude-exit dwell, F15); the twin applies the dwell from v5 on
 /// <summary>A buffer of this many chars always holds one row.</summary>
        public const int MaxRowChars = 2048;

        /// <summary>
 /// Formats the row of one step into <paramref name="buf"/>; returns its length, or −1 if the buffer is too
 /// small. <paramref name="pre"/> is the state before <see cref="ViewStateMachine.Step"/>.
 /// </summary>
        public static int Format(ref ViewMachineState pre, ref ViewInputs i, ref ViewOutputs o, char[] buf)
        {
            var w = new CharSink(buf);
            w.Raw("{\"ev\":\"vsm\",\"v\":"); w.U64((ulong)Version); w.Raw(",\"n\":"); w.U64(pre.Steps);
            w.Raw(",\"tick\":"); w.U64(i.Tick);
            w.Raw(",\"us\":"); w.I64(i.NowUs);
            //: the band is a live setting; every row carries it so the twin replays with the plugin's band
            w.Raw(",\"be\":"); w.Fixed(ViewThresholds.ApproachEnterY, 1);
            w.Raw(",\"bx\":"); w.Fixed(ViewThresholds.ApproachExitY, 1);

            w.Raw(",\"bu\":"); w.Bit(i.BridgeUp);
            w.Raw(",\"site\":"); w.Bit(i.OnSite);
            w.Raw(",\"zone\":"); w.U64(i.Zone);
            w.Raw(",\"rho\":"); w.Fixed(i.RhoInfM, 2);
            w.Raw(",\"rhob\":"); w.HexBits(i.RhoInfM);
            w.Raw(",\"y\":"); w.Fixed(i.Y, 4);
            w.Raw(",\"yb\":"); w.HexBits(i.Y);
            w.Raw(",\"vy\":"); w.Fixed(i.Vy, 4);
            w.Raw(",\"vyb\":"); w.HexBits(i.Vy);
            w.Raw(",\"peer\":"); w.U64(i.Peer);
            w.Raw(",\"ph\":"); w.U64(i.SnPhase);
            w.Raw(",\"snf\":"); w.U64(i.SnFlags);
            w.Raw(",\"pst\":"); w.U64(i.PresentedSeTick);
            w.Raw(",\"abt\":"); w.U64(i.AreaBuiltSeTick);
            w.Raw(",\"zok\":"); w.Bit(i.ZOrderOk);
            w.Raw(",\"pau\":"); w.Bit(i.Paused);
            w.Raw(",\"fg\":"); w.Bit(i.Foreground);
            w.Raw(",\"gui\":"); w.Bit(i.GuiScreenOpen);
            w.Raw(",\"cur\":"); w.Bit(i.CursorOrChat);
            w.Raw(",\"ld\":"); w.Bit(i.Loading);
            w.Raw(",\"kau\":"); w.Bit(i.KeepAudibleUnfocused);
            w.Raw(",\"evt\":"); w.U64(i.Events);
            w.Raw(",\"ack\":"); w.U64(i.RenderAckSeq);
            w.Raw(",\"mrd\":"); w.Bit(i.MarginsReady);
            w.Raw(",\"dm\":"); w.U64(i.DisplayMode);
            w.Raw(",\"mut\":"); w.U64(i.Mutants);
            w.Raw(",\"pp\":"); w.U64(i.Picture);

            w.Raw(",\"s_v\":"); w.U64(pre.View);
            w.Raw(",\"s_a\":"); w.U64(pre.Approach);
            w.Raw(",\"s_sw\":"); w.Bit(pre.HasSwitched);
            w.Raw(",\"s_ls\":"); w.I64(pre.LastSwitchUs);
            w.Raw(",\"s_pc\":"); w.I64(pre.PrimeCondUs);
            w.Raw(",\"s_bw\":"); w.Bit(pre.Below);
            w.Raw(",\"s_xt\":"); w.U64(pre.CrossingTick);
            w.Raw(",\"s_rp\":"); w.Bit(pre.ResettlePending);
            w.Raw(",\"s_rt\":"); w.U64(pre.ResettleTick);
            w.Raw(",\"s_rs\":"); w.U64(pre.RenderSeq);
            w.Raw(",\"s_rq\":"); w.U64(pre.Request);
            w.Raw(",\"s_ma\":"); w.Bit(pre.MarginsApplied);
            w.Raw(",\"s_pf\":"); w.Bit(pre.PrevForeground);
            w.Raw(",\"s_pz\":"); w.Bit(pre.PrevZOrderOk);
            w.Raw(",\"s_mf\":"); w.HexBits(pre.MixFrom);
            w.Raw(",\"s_ms\":"); w.I64(pre.MixStartUs);
            w.Raw(",\"s_mu\":"); w.Bit(pre.MixToUnderwater);
            w.Raw(",\"s_sp\":"); w.Bit(pre.Picture);
            w.Raw(",\"s_bt\":"); w.U64(pre.BandTick);
            w.Raw(",\"s_ps\":"); w.Bit(pre.PicSwitched);
            w.Raw(",\"s_pt\":"); w.I64(pre.PicSwitchUs);
            w.Raw(",\"s_la\":"); w.Bit(pre.LeftArmed);
            w.Raw(",\"s_lt\":"); w.I64(pre.LeftSinceUs);

            w.Raw(",\"o_vs\":"); w.U64(o.ViewState);
            w.Raw(",\"o_am\":"); w.U64(o.ApproachMode);
            w.Raw(",\"o_pm\":"); w.U64(o.PerfMode);
            w.Raw(",\"o_fps\":"); w.U64(o.TargetFps);
            w.Raw(",\"o_rq\":"); w.U64(o.Request);
            w.Raw(",\"o_rs\":"); w.U64(o.RenderSeq);
            w.Raw(",\"o_zr\":"); w.Bit(o.ZOrderReassert);
            w.Raw(",\"o_fz\":"); w.Bit(o.Freeze);
            w.Raw(",\"o_xt\":"); w.U64(o.CrossingTick);
            w.Raw(",\"o_rdy\":"); w.Bit(o.SnReady);
            w.Raw(",\"o_blk\":"); w.U64(o.Blocked);
            w.Raw(",\"o_ov\":"); w.U64(o.Overlays);
            w.Raw(",\"o_c5f\":"); w.U64(o.C5Flags);
            w.Raw(",\"o_bus\":"); w.Fixed(o.SnBusTarget, 6);
            w.Raw(",\"o_xf\":"); w.Fixed(o.CrossfadeMs, 1);
            w.Raw(",\"o_sg\":"); w.Fixed(o.SeGameFactor, 6);
            w.Raw(",\"o_sm\":"); w.Fixed(o.SeMusicFactor, 6);
            w.Raw(",\"o_wm\":"); w.Fixed(o.WaterModFactor, 6);
            w.Raw(",\"o_aw\":"); w.Bit(o.SeAudioWrite);
            w.Raw(",\"o_ma\":"); w.Bit(o.SeMixActive);
            w.Raw(",\"o_mix\":"); w.HexBits(o.MixLevel);
            w.Raw(",\"o_rsn\":"); w.U64(o.Reason);
            w.Raw(",\"o_sp\":"); w.Bit(o.Picture);
            w.Raw(",\"o_pe\":"); w.I64(o.PictureEdge);
            w.Raw(",\"o_pb\":"); w.U64(o.PictureBlocked);
            w.Raw(",\"o_prs\":"); w.U64(o.PictureReason);
            w.Raw(",\"o_pp\":"); w.U64(o.PicturePolicy);
            w.Raw("}");
            return w.Overflow ? -1 : w.Length;
        }

        /// <summary>Convenience for logs and tests (allocates the string).</summary>
        public static string FormatString(ref ViewMachineState pre, ref ViewInputs i, ref ViewOutputs o)
        {
            var buf = new char[MaxRowChars];
            int n = Format(ref pre, ref i, ref o, buf);
            return n < 0 ? null : new string(buf, 0, n);
        }

        /// <summary>A double's IEEE-754 bits as 16 lower-case hex digits (the trace's exact form).</summary>
        public static ulong Bits(double v)
        {
            return unchecked((ulong)BitConverter.DoubleToInt64Bits(v));
        }

        /// <summary>
 /// Appends chars to a fixed buffer without allocating. A mutable struct: keep it in a local.
 /// </summary>
        private struct CharSink
        {
            private static readonly ulong[] Pow10 = { 1UL, 10UL, 100UL, 1000UL, 10000UL, 100000UL, 1000000UL, 10000000UL, 100000000UL };
            private const string HexDigits = "0123456789abcdef";

            private readonly char[] _buf;
            private int _n;

            public CharSink(char[] buf)
            {
                _buf = buf;
                _n = 0;
                Overflow = buf == null;
            }

            public bool Overflow { get; private set; }
            public int Length { get { return _n; } }

            private bool Room(int count)
            {
                if (Overflow) return false;
                if (_n + count > _buf.Length) { Overflow = true; return false; }
                return true;
            }

            public void Raw(string s)
            {
                if (!Room(s.Length)) return;
                for (int k = 0; k < s.Length; k++) _buf[_n++] = s[k];
            }

            private void Ch(char c)
            {
                if (!Room(1)) return;
                _buf[_n++] = c;
            }

            public void Bit(bool b)
            {
                Ch(b ? '1' : '0');
            }

            public void U64(ulong v)
            {
                int len = 1;
                for (ulong t = v / 10; t != 0; t /= 10) len++;
                if (!Room(len)) return;
                for (int k = len - 1; k >= 0; k--)
                {
                    _buf[_n + k] = (char)('0' + (int)(v % 10));
                    v /= 10;
                }
                _n += len;
            }

            public void I64(long v)
            {
                if (v < 0)
                {
                    Ch('-');
                    U64(unchecked((ulong)(-(v + 1)) + 1UL));
                }
                else U64((ulong)v);
            }

            /// <summary>Fixed-point decimal (half away from zero); <c>null</c> for NaN, infinities and huge values.</summary>
            public void Fixed(double v, int decimals)
            {
                if (double.IsNaN(v) || double.IsInfinity(v) || decimals < 0 || decimals >= Pow10.Length) { Raw("null"); return; }
                ulong scale = Pow10[decimals];
                double scaled = Math.Round(Math.Abs(v) * scale, MidpointRounding.AwayFromZero);
                if (scaled >= 9.0e18) { Raw("null"); return; }
                ulong q = (ulong)scaled;
                if (v < 0 && q != 0) Ch('-');
                U64(q / scale);
                if (decimals == 0) return;
                Ch('.');
                ulong frac = q % scale;
                for (int k = decimals - 1; k >= 0; k--)
                {
                    ulong p = Pow10[k];
                    Ch((char)('0' + (int)(frac / p % 10)));
                }
            }

            public void HexBits(double v)
            {
                ulong bits = Bits(v);
                if (!Room(18)) return;
                _buf[_n++] = '"';
                for (int k = 15; k >= 0; k--) _buf[_n++] = HexDigits[(int)((bits >> (4 * k)) & 0xF)];
                _buf[_n++] = '"';
            }
        }
    }
}
