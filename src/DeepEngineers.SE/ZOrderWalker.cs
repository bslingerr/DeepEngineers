using System;
using System.Collections.Generic;
using System.Text;

namespace DeepEngineers.SE
{
    /// <summary>One top-level window as the z-order walker sees it.</summary>
    internal struct WindowInfo
    {
        public IntPtr Hwnd;
        public bool Visible;
        public bool Cloaked;
        public int Width, Height;
        /// <summary><c>GetWindow(h, GW_OWNER)</c>.</summary>
        public IntPtr Owner;

        public static WindowInfo Read(IntPtr h)
        {
            var w = new WindowInfo { Hwnd = h };
            try
            {
                w.Visible = NativeWin.IsWindowVisible(h);
                if (!w.Visible) return w; // skipped anyway: no further calls (the walk runs every render frame in Prime)
                w.Cloaked = NativeWin.IsCloaked(h);
                NativeWin.RECT r;
                if (NativeWin.GetWindowRect(h, out r)) { w.Width = r.Right - r.Left; w.Height = r.Bottom - r.Top; }
                w.Owner = NativeWin.GetWindow(h, NativeWin.GW_OWNER);
            }
            catch (Exception) { }
            return w;
        }
    }

    /// <summary>The walker's verdict (S2.T-D1 oracle and C5 <c>zOrderOk</c>).</summary>
    internal struct WalkResult
    {
        /// <summary>The first remaining window directly under <c>seHwnd</c> is <c>snHwnd</c>, visible, uncloaked, non-empty.</summary>
        public bool Ok;
        /// <summary>The first remaining window (Zero when the chain ended or seHwnd was invalid).</summary>
        public IntPtr First;
        public int Walked;
        public int SkippedCount;
        /// <summary>The skipped windows in order (at most <see cref="ZOrderWalker.MaxLoggedSkipped"/>).</summary>
        public IntPtr[] Skipped;
        public string[] SkippedWhy;
        /// <summary>Why not ok ("" when ok).</summary>
        public string Why;

        /// <summary>Log text: verdict, first window (with class name and process), skipped list. Allocates: call on changes only.</summary>
        public string Describe(IntPtr sn, bool live)
        {
            var sb = new StringBuilder(160);
            sb.Append(Ok ? "OK" : "NOT-OK").Append(" first=").Append(NativeWin.Hex(First));
            if (live && First != IntPtr.Zero)
                sb.Append('(').Append(First == sn ? "snHwnd " : "").Append(NativeWin.ClassName(First)).Append(" pid ").Append(NativeWin.ProcessOf(First)).Append(')');
            sb.Append(" walked=").Append(Walked).Append(" skipped=").Append(SkippedCount);
            if (Skipped != null && SkippedCount > 0)
            {
                sb.Append(" [");
                int n = Math.Min(SkippedCount, Skipped.Length);
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(NativeWin.Hex(Skipped[i])).Append(' ').Append(SkippedWhy[i]);
                    if (live) sb.Append(' ').Append(NativeWin.ClassName(Skipped[i]));
                }
                if (SkippedCount > n) sb.Append(", …");
                sb.Append(']');
            }
            if (!Ok && !string.IsNullOrEmpty(Why)) sb.Append(" why=").Append(Why);
            return sb.ToString();
        }
    }

    /// <summary>
 /// The corrected z-order walker of ARCHITECTURE §5.1: from <c>seHwnd</c> walk <c>GW_HWNDNEXT</c>, skipping windows
 /// that are invisible, DWM-cloaked (<c>DWMWA_CLOAKED</c>), zero-area, or owned by <c>snHwnd</c> or <c>seHwnd</c>
 /// the first remaining window must be
 /// <c>snHwnd</c>. <see cref="Decide"/> is the pure decision (tests feed synthetic lists); <see cref="WalkLive"/>
 /// gathers the same facts with user32 (render thread in the plugin) and applies the same predicate.
 /// </summary>
    internal static class ZOrderWalker
    {
        public const int MaxWalk = 512;
        public const int MaxLoggedSkipped = 16;

        /// <summary>Why <paramref name="w"/> is skipped, or null when it counts.</summary>
        public static string SkipReason(ref WindowInfo w, IntPtr se, IntPtr sn)
        {
            if (w.Hwnd == sn) return null; // snHwnd is judged, never skipped
            if (!w.Visible) return "invisible";
            if (w.Cloaked) return "cloaked";
            if (w.Width <= 0 || w.Height <= 0) return "zero-area";
            if (w.Owner != IntPtr.Zero && w.Owner == sn) return "owned-by-sn";
            if (w.Owner != IntPtr.Zero && w.Owner == se) return "owned-by-se";
            return null;
        }

        /// <summary>The verdict for the windows below <paramref name="se"/>, in z-order (top first).</summary>
        public static WalkResult Decide(IList<WindowInfo> chainBelowSe, IntPtr se, IntPtr sn)
        {
            var r = NewResult();
            if (se == IntPtr.Zero) { r.Why = "seHwnd unknown"; return r; }
            if (sn == IntPtr.Zero) { r.Why = "snHwnd unknown"; return r; }
            if (chainBelowSe != null)
                for (int i = 0; i < chainBelowSe.Count && r.Walked < MaxWalk; i++)
                {
                    WindowInfo w = chainBelowSe[i];
                    if (Visit(ref r, ref w, se, sn)) return r;
                }
            if (string.IsNullOrEmpty(r.Why)) r.Why = "chain ended without a counted window";
            return r;
        }

        /// <summary>
 /// The live walk with user32, stopping at the first counted window. Pass reusable <paramref name="skipped"/> /
 /// <paramref name="skippedWhy"/> buffers (length ≥ <see cref="MaxLoggedSkipped"/>) to walk without allocating; the
 /// result then refers to them until the next walk.
 /// </summary>
        public static WalkResult WalkLive(IntPtr se, IntPtr sn, IntPtr[] skipped = null, string[] skippedWhy = null)
        {
            var r = skipped != null && skippedWhy != null && skipped.Length >= MaxLoggedSkipped && skippedWhy.Length >= MaxLoggedSkipped
                ? new WalkResult { Skipped = skipped, SkippedWhy = skippedWhy, Why = "" }
                : NewResult();
            if (se == IntPtr.Zero || !SafeIsWindow(se)) { r.Why = "seHwnd invalid"; return r; }
            if (sn == IntPtr.Zero) { r.Why = "snHwnd unknown"; return r; }
            IntPtr h = se;
            while (r.Walked < MaxWalk)
            {
                try { h = NativeWin.GetWindow(h, NativeWin.GW_HWNDNEXT); }
                catch (Exception) { h = IntPtr.Zero; }
                if (h == IntPtr.Zero) break;
                WindowInfo w = WindowInfo.Read(h);
                if (Visit(ref r, ref w, se, sn)) return r;
            }
            if (string.IsNullOrEmpty(r.Why)) r.Why = r.Walked >= MaxWalk ? "walk limit " + MaxWalk + " reached" : "chain ended without a counted window";
            return r;
        }

        private static WalkResult NewResult()
        {
            return new WalkResult { Skipped = new IntPtr[MaxLoggedSkipped], SkippedWhy = new string[MaxLoggedSkipped], Why = "" };
        }

        /// <summary>Applies one window; true when the walk is decided.</summary>
        private static bool Visit(ref WalkResult r, ref WindowInfo w, IntPtr se, IntPtr sn)
        {
            r.Walked++;
            string skip = SkipReason(ref w, se, sn);
            if (skip != null)
            {
                if (r.SkippedCount < MaxLoggedSkipped) { r.Skipped[r.SkippedCount] = w.Hwnd; r.SkippedWhy[r.SkippedCount] = skip; }
                r.SkippedCount++;
                return false;
            }
            r.First = w.Hwnd;
            if (w.Hwnd != sn) { r.Ok = false; r.Why = "another window is between SE and Subnautica"; return true; }
            if (!w.Visible) { r.Ok = false; r.Why = "snHwnd invisible"; return true; }
            if (w.Cloaked) { r.Ok = false; r.Why = "snHwnd cloaked"; return true; }
            if (w.Width <= 0 || w.Height <= 0) { r.Ok = false; r.Why = "snHwnd zero-area (minimised?)"; return true; }
            r.Ok = true;
            r.Why = "";
            return true;
        }

        private static bool SafeIsWindow(IntPtr h)
        {
            try { return NativeWin.IsWindow(h); } catch (Exception) { return false; }
        }
    }
}
