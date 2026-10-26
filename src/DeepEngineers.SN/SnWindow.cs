using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The S2 window role of Subnautica (ROADMAP S2 SN plugin; ARCHITECTURE §5.1 "Mechanism (Subnautica)", §5.2; CONTRACT
 /// §1 "Input focus", "Window z-order", §1.1 "window styles, size, monitor"):
 /// <list type="number">
 /// <item>At ReadyIdle (before SE starts), on the Unity window found by S0's <see cref="WindowRole.FindMainWindow"/>
 /// (top-level, unowned <c>UnityWndClass</c> of our process): <c>ShowWindow(SW_HIDE)</c> → <c>SetWindowLongPtr(GWL_EXSTYLE,
 /// … | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)</c> (WS_EX_APPWINDOW cleared) → <c>SetWindowPos(NOMOVE|NOSIZE|NOZORDER|
 /// NOACTIVATE|FRAMECHANGED)</c> → <c>ShowWindow(SW_SHOWNOACTIVATE)</c> (the shell evaluates taskbar styles only when
 /// a window is shown), then <c>SetWindowPos(HWND_BOTTOM, NOMOVE|NOSIZE|NOACTIVATE)</c> and
 /// <c>AllowSetForegroundWindow(ASFW_ANY)</c> while Subnautica is still the foreground process. C0 snFlags bit 2
 /// stylesApplied is set when the styles read back. Fallback <c>ITaskbarList:DeleteTab</c> when they do not (or
 /// always, with <c>DEEPENGINEERS_SN_TASKBAR=deletetab</c>).</item>
 /// <item>Verified every second, and on every frame for 30 frames after <c>DisplayManager.OnDisplayChanged</c> (resolution,
 /// fullscreen mode, vSyncCount: Unity rewrites window styles on display changes) or our own resolution
 /// correction. Lost styles are re-applied with the same sequence; the window then goes back directly under SE
 /// (<c>SetWindowPos(snHwnd, seHwnd, NOACTIVATE)</c>, our own window, SE's only as the reference) or to the bottom
 /// when no SE window is known.</item>
 /// <item>Monitor and rectangle, every second: the window rectangle in screen pixels (<c>GetWindowRect</c>, C7 snRect,
 /// (left, top, right, bottom): the same call and order the SE side uses for C5 seRect; the client rectangle is logged)
 /// against C5 seRect → C7 rectOk (<see cref="RectRules"/>). A correctable size mismatch is fixed with
 /// <c>Screen.SetResolution(w, h, FullScreenMode.FullScreenWindow)</c>, at most 3 times 5 s apart, before/after
 /// logged; another monitor cannot be fixed at run time in Unity 2019.4 (logged
 /// option). Unity itself writes its Screenmanager registry keys on SetResolution (the launcher restores them,
 /// CONTRACT §1.1); the plugin never calls PlayerPrefs.Save or GameSettings.SaveAsync.</item>
 /// </list>
 /// Never activates the window. Every call is guarded; the first exception disables the window role only (S2Host).
 /// </summary>
    internal sealed class SnWindow
    {
        public const int VerifyFramesAfterChange = 30;

        private readonly IS2Bridge _bridge;
        private readonly RectCorrector _corrector = new RectCorrector();
        private bool _hooked, _displayChanged;
        private int _verifyFramesLeft;
        private string _verifyReason = "";
        private bool _correctionAfterPending;
        private int _correctionNo;
        private RectVerdict _lastLoggedVerdict = (RectVerdict)(-1);
        private bool _displayLogged;

        public bool Enabled;
        /// <summary>
 /// Review fix (finding 3): false while SE shows Subnautica (C5 margins on, see-through, or a view state
 /// other than Space; set by <see cref="S2Host"/> every frame). A mid-session re-application then restyles without
 /// <c>SW_HIDE</c>/<c>SW_SHOWNOACTIVATE</c> (a hidden window under SE's transparent pixels shows whatever is below
 /// it, and SE's window watch would take it for a lost peer); the taskbar button is then removed with
 /// <c>ITaskbarList:DeleteTab</c>, since the shell evaluates the styles only when a window is shown.
 /// </summary>
        public bool HideAllowed = true;
        public long HidesSkipped;
        public IntPtr Hwnd { get; private set; }
        public bool Applied { get; private set; }
        public bool StylesOk { get; private set; }
        public long ExStyle { get; private set; }
        public long Applications, Reapplications, StyleChecks, StyleFailures, DisplayChanges, DeleteTabs, HwndRefinds;

        public RectI SnRect, SnClientRect, SnMonitor, SeRect, SeMonitor;
        public bool SnPrimary, SePrimary;
        public RectVerdict Verdict = RectVerdict.NoSeRect;
        public bool RectOk { get; private set; }
        public uint Dpi;

        public SnWindow(IS2Bridge bridge) { _bridge = bridge; }

        public int CorrectionAttempts { get { return _corrector.Attempts; } }

        // ------------------------------------------------------------------------------------------ hooks

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Hook()
        {
            if (_hooked) return;
            DisplayManager.OnDisplayChanged += OnDisplayChanged;
            _hooked = true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Unhook()
        {
            if (!_hooked) return;
            _hooked = false;
            DisplayManager.OnDisplayChanged -= OnDisplayChanged;
        }

        private void OnDisplayChanged()
        {
            // DisplayManager.Update (main thread): verify from our next frame on, never inside Unity's own update
            _displayChanged = true;
        }

        // ------------------------------------------------------------------------------------------ ReadyIdle

 /// <summary>The first application, at ReadyIdle, before SE is started (Subnautica is still the foreground process).</summary>
        public void ApplyAtReadyIdle(ulong now)
        {
            if (!Enabled) { DeLog.Info("DE.Window S2 role OFF (" + S2Switches.WindowVar + "=off): no restyle, no rect check"); return; }
            if (!EnsureHwnd("ReadyIdle")) { DeLog.Warn("DE.Window no UnityWndClass window at ReadyIdle: restyle retried every second"); return; }
            IntPtr fgHwnd;
            uint fgPid = SnNative.ForegroundPid(out fgHwnd);
            // Hiding the active window lets Windows activate another one, after which this process may no longer pass the
 // foreground on: ASFW_ANY is granted before the restyle (while Subnautica is certainly the foreground process)
 // and again after it, where ARCHITECTURE §5.1 places it (both results logged).
            int err1, err2;
            bool asfw1 = Win32.AllowSetForegroundWindow(Win32.AsfwAny, out err1);
            Apply("ReadyIdle", true);
            bool asfw2 = Win32.AllowSetForegroundWindow(Win32.AsfwAny, out err2);
            IntPtr fgAfter;
            uint fgPidAfter = SnNative.ForegroundPid(out fgAfter);
            DeLog.Info("DE.Window AllowSetForegroundWindow(ASFW_ANY) before the restyle " + (asfw1 ? "ok" : "FAILED error " + err1) +
                       ", after " + (asfw2 ? "ok" : "FAILED error " + err2) +
                       " (Subnautica foreground before: " + (fgPid == Win32.CurrentProcessId() ? "yes" : "no, pid " + fgPid + " " + SnNative.Hwnd(fgHwnd)) +
                       "; after: " + (fgPidAfter == Win32.CurrentProcessId() ? "Subnautica" : "pid " + fgPidAfter + " " + SnNative.Hwnd(fgAfter)) + ")");
            LogDisplay("ReadyIdle");
            CheckRect(now, default(RectI), false);
        }

        /// <summary>
 /// The restyle sequence on our own window. <paramref name="first"/>: lowered to HWND_BOTTOM (ReadyIdle, before SE);
 /// otherwise placed directly under SE's window when it is known (a mid-session re-application).
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Apply(string why, bool first)
        {
            IntPtr h = Hwnd;
            long before = SnNative.GetExStyle(h);
            bool visibleBefore = SnNative.IsWindowVisible(h);
            long want = WindowStyleRules.WithRequired(before);
            bool hideNow = HideAllowed; // also for a late first application, should SE already show Subnautica
            bool hide = hideNow && SnNative.ShowWindow(h, SnNative.SW_HIDE);
            if (!hideNow) HidesSkipped++;
            int setErr;
            bool set = SnNative.SetExStyle(h, want, out setErr);
            bool frame = SnNative.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
                SnNative.SWP_NOMOVE | SnNative.SWP_NOSIZE | SnNative.SWP_NOZORDER | SnNative.SWP_NOACTIVATE | SnNative.SWP_FRAMECHANGED);
            if (hideNow) SnNative.ShowWindow(h, SnNative.SW_SHOWNOACTIVATE);
            string z;
            IntPtr se = new IntPtr((long)_bridge.SeHwnd);
            if (!first && se != IntPtr.Zero && SnNative.IsWindow(se))
            {
                bool ok = SnNative.SetWindowPos(h, se, 0, 0, 0, 0, SnNative.SWP_NOMOVE | SnNative.SWP_NOSIZE | SnNative.SWP_NOACTIVATE);
                z = "under seHwnd " + SnNative.Hwnd(se) + (ok ? "" : " FAILED");
            }
            else
            {
                bool ok = SnNative.SetWindowPos(h, SnNative.HWND_BOTTOM, 0, 0, 0, 0, SnNative.SWP_NOMOVE | SnNative.SWP_NOSIZE | SnNative.SWP_NOACTIVATE);
                z = "HWND_BOTTOM" + (ok ? "" : " FAILED");
            }
            long after = SnNative.GetExStyle(h);
            ExStyle = after;
            StylesOk = WindowStyleRules.HasRequired(after);
            Applied = true;
            if (first) Applications++; else Reapplications++;
            _bridge.SetStylesApplied(StylesOk);
            DeLog.Info("DE.Window restyle (" + why + ") hwnd=" + SnNative.Hwnd(h) + " exStyle " + WindowStyleRules.Describe(before) + " -> " +
                       WindowStyleRules.Describe(after) + " hide=" + (hideNow ? hide.ToString() : "SKIPPED (SE shows Subnautica: no hide/show mid-session)") +
                       " set=" + (set ? "ok" : "FAILED error " + setErr) +
                       " framechanged=" + frame + " visible " + visibleBefore + "->" + SnNative.IsWindowVisible(h) + " z=" + z +
                       " stylesOk=" + StylesOk + " (#" + (Applications + Reapplications) + ")");
            if (!StylesOk || S2Switches.AlwaysDeleteTab || !hideNow)
            {
                string r;
                bool ok = TaskbarTab.DeleteTab(h, out r);
                DeleteTabs++;
                DeLog.Info("DE.Window ITaskbarList::DeleteTab " + (ok ? "ok" : "FAILED") + " (" +
                           (!StylesOk ? "fallback: the styles did not verify" : !hideNow ? "restyled without hide/show: the shell keeps the button otherwise" :
                            "forced by " + S2Switches.TaskbarVar + "=deletetab") + "; " + r + ")");
            }
        }

        /// <summary>The window handle: S0's, re-found when it is no longer a window (published in C0 and C7).</summary>
        private bool EnsureHwnd(string why)
        {
            IntPtr h = _bridge.SnHwnd;
            if (h != IntPtr.Zero && SnNative.IsWindow(h)) { Hwnd = h; return true; }
            string how;
            IntPtr found = WindowRole.FindMainWindow(out how);
            if (found == IntPtr.Zero) { Hwnd = IntPtr.Zero; return false; }
            if (found != h)
            {
                HwndRefinds++;
                _bridge.PublishHwnd(found, how + ", " + why);
            }
            Hwnd = found;
            return true;
        }

        // ------------------------------------------------------------------------------------------ per frame / second

 /// <summary>Per frame: styles verified on every frame for 30 frames after a display change.</summary>
        public void Frame(ulong now)
        {
            if (!Enabled || !Applied) return;
            if (_displayChanged)
            {
                _displayChanged = false;
                DisplayChanges++;
                _verifyFramesLeft = VerifyFramesAfterChange;
                _verifyReason = "DisplayManager.OnDisplayChanged";
            }
            if (_verifyFramesLeft > 0)
            {
                _verifyFramesLeft--;
                VerifyStyles(_verifyReason);
            }
        }

        /// <summary>Per second: handle, styles, rectangles and rectOk, the size correction.</summary>
        public void Second(ulong now, bool c5Valid, RectI seRect)
        {
            if (!Enabled) return;
            if (!EnsureHwnd("per-second check")) { RectOk = false; return; }
            if (!Applied) { ApplyAtReadyIdle(now); return; }
            VerifyStyles("per-second check");
            CheckRect(now, seRect, c5Valid);
        }

        private void VerifyStyles(string why)
        {
            if (Hwnd == IntPtr.Zero) return;
            long ex = SnNative.GetExStyle(Hwnd);
            ExStyle = ex;
            StyleChecks++;
            if (WindowStyleRules.HasRequired(ex))
            {
                _failedInARow = 0;
                if (!StylesOk) { StylesOk = true; _bridge.SetStylesApplied(true); }
                return;
            }
            StyleFailures++;
            StylesOk = false;
            _bridge.SetStylesApplied(false);
            if (_failedInARow >= MaxFailedInARow)
            {
                // the restyle does not hold: stop hiding and showing the window every second (C0 stylesApplied stays 0)
                DeLog.WarnOnce("window.giveup", "DE.Window styles still missing after " + MaxFailedInARow + " re-applications in a row (exStyle " +
                               WindowStyleRules.Describe(ex) + "): no further re-application; C0 stylesApplied stays 0 (the taskbar fallback)");
                return;
            }
            _failedInARow++;
            DeLog.Warn("DE.Window styles lost (" + why + "): exStyle " + WindowStyleRules.Describe(ex) + "; re-applying (#" + StyleFailures + ")");
            Apply("re-apply after " + why, false);
            if (StylesOk) _failedInARow = 0;
        }

        public const int MaxFailedInARow = 5;
        private int _failedInARow;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CheckRect(ulong now, RectI seRect, bool c5Valid)
        {
            RectI sn, client, mon;
            bool primary;
            SnNative.TryWindowRect(Hwnd, out sn);
            SnNative.TryClientRectOnScreen(Hwnd, out client);
            SnNative.TryMonitorOfWindow(Hwnd, out mon, out primary);
            SnRect = sn;
            SnClientRect = client;
            SnMonitor = mon;
            SnPrimary = primary;
            SeRect = c5Valid ? seRect : default(RectI);
            RectI seMon = default(RectI);
            bool sePrimary = false;
            if (!SeRect.Empty) SnNative.TryMonitorOfRect(SeRect, out seMon, out sePrimary);
            SeMonitor = seMon;
            SePrimary = sePrimary;
            if (SnNative.IsIconic(Hwnd)) sn = default(RectI);
            RectVerdict v = RectRules.Compare(sn, SeRect, SnMonitor, SeMonitor);
            Verdict = v;
            RectOk = v == RectVerdict.Ok;

            if (_correctionAfterPending)
            {
                _correctionAfterPending = false;
                DeLog.Info("DE.Window rect correction #" + _correctionNo + " after: snRect=" + SnRect + " client=" + SnClientRect + " seRect=" + SeRect +
                           " Screen=" + Screen.width + "x" + Screen.height + " mode=" + Screen.fullScreenMode + " verdict=" + RectRules.Describe(v));
            }
            if (v != _lastLoggedVerdict)
            {
                _lastLoggedVerdict = v;
                string line = "DE.Window rect " + RectRules.Describe(v) + ": snRect=" + SnRect + " (client " + SnClientRect + ", monitor " + SnMonitor +
                              (SnPrimary ? " primary" : "") + ") seRect=" + SeRect + " (monitor " + SeMonitor + (SePrimary ? " primary" : "") + ") rectOk=" + RectOk;
                if (v == RectVerdict.Ok || v == RectVerdict.NoSeRect) DeLog.Info(line);
                else DeLog.Warn(line + (v == RectVerdict.OtherMonitor ? " (owner fallback: set Subnautica's Display option to SE's monitor once)" : ""));
            }
            if (_corrector.ShouldAttempt(v, S2Switches.RectCorrect, now))
            {
                _corrector.Note(now);
                _correctionNo = _corrector.Attempts;
                int w = SeRect.W, h = SeRect.H;
                DeLog.Info("DE.Window rect correction #" + _correctionNo + " before: snRect=" + SnRect + " seRect=" + SeRect + " Screen=" + Screen.width + "x" +
                           Screen.height + " mode=" + Screen.fullScreenMode + " -> Screen.SetResolution(" + w + ", " + h + ", FullScreenWindow)" +
                           " (Unity writes its Screenmanager keys; the launcher restores them; nothing else persisted)");
                Screen.SetResolution(w, h, FullScreenMode.FullScreenWindow);
                _correctionAfterPending = true;
                _verifyFramesLeft = VerifyFramesAfterChange;
                _verifyReason = "Screen.SetResolution";
                if (_corrector.Exhausted) DeLog.Warn("DE.Window rect correction budget used (" + RectCorrector.MaxAttempts + " attempts); rectOk stays as measured");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void LogDisplay(string why)
        {
            if (_displayLogged) return;
            _displayLogged = true;
            string monitorArg = "absent";
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length; i++)
                    if (string.Equals(args[i], "-monitor", StringComparison.OrdinalIgnoreCase)) monitorArg = i + 1 < args.Length ? args[i + 1] : "(no value)";
            }
            catch (Exception) { }
            int displays = -1, index = -1, pref = -2;
            try { displays = Display.displays.Length; } catch (Exception) { }
            try { index = DisplayManager.GetCurrentDisplayIndex(); } catch (Exception) { }
            try { pref = PlayerPrefs.GetInt("UnitySelectMonitor", -1); } catch (Exception) { }
            RectI mon;
            bool primary;
            SnNative.TryMonitorOfWindow(Hwnd, out mon, out primary);
            Dpi = SnNative.DpiForWindow(Hwnd);
            Resolution cur = Screen.currentResolution;
            DeLog.Info("DE.Window display (" + why + "): -monitor " + monitorArg + " (1-based) DisplayManager index=" + index + " UnitySelectMonitor=" + pref +
                       " displays=" + displays + " Screen=" + Screen.width + "x" + Screen.height + " mode=" + Screen.fullScreenMode +
                       " currentResolution=" + cur.width + "x" + cur.height + "@" + cur.refreshRate + "Hz monitor=" + mon + (primary ? " primary" : " NOT primary") +
                       " dpi=" + Dpi + " (expected: the primary monitor, SE's; checked against C5 seRect every second)");
        }

        // ------------------------------------------------------------------------------------------ lifecycle

 /// <summary>
 /// SE exited or crashed (ARCHITECTURE §5.14; S8): minimise without activation before the quit. ShowWindow returns the
 /// previous visibility, not success (S8: the log says so; IsIconic afterwards is the check).
 /// </summary>
        public void MinimiseNoActivate(string why)
        {
            if (!Enabled || Hwnd == IntPtr.Zero) return;
            bool wasVisible = SnNative.ShowWindow(Hwnd, SnNative.SW_SHOWMINNOACTIVE);
            DeLog.Info("DE.Window ShowWindow(SW_SHOWMINNOACTIVE) wasVisible=" + wasVisible + " iconic=" + SnNative.IsIconic(Hwnd) + " (" + why + ")");
        }

        public string Describe()
        {
            var ci = CultureInfo.InvariantCulture;
            return "win{" + (Enabled ? "" : "OFF ") + "hwnd=" + SnNative.Hwnd(Hwnd) + " styles=" + (StylesOk ? "ok" : "MISSING") + " ex=0x" + ExStyle.ToString("x", ci) +
                   " applied=" + Applications + "+" + Reapplications + " lost=" + StyleFailures + " displayChanges=" + DisplayChanges + " deleteTab=" + DeleteTabs +
                   " hidesSkipped=" + HidesSkipped +
                   " rect=" + RectRules.Describe(Verdict) + " sn=" + SnRect + " se=" + SeRect + " corrections=" + _corrector.Attempts + "}";
        }
    }
}
