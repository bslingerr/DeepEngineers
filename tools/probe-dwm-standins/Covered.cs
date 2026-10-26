// S2.N8 (1) covered-render stand-in (ROADMAP S2 oracle, ARCHITECTURE 4.4 "First S2 gate"; no game is launched).
//
// A flip-model D3D11 window (the Subnautica stand-in: Unity 2019.4 uses a flip-model swap chain) renders as fast as its
// swap chain lets it (Present(1): vsync, the "Full (1, any)" recipe) while another window covers it completely:
// covered an opaque bit-blt window (SE's DXGI 1.0 SwapEffect.Discard swap chain, no DWM call) on top: the

// margins1 the same top window with DwmExtendFrameIntoClientArea(-1) and alpha 1 everywhere: the Prime set-up
// marginsHole margins on, alpha 1 except a 1x1 px alpha-0 corner (ARCHITECTURE 4.4 alternative (a))
// uncovered no top window (control)
// The below window logs at 1 Hz (below-stats.jsonl): frames and fps, Present call duration and the interval between
// Present returns (mean/p50/p99/max, QPC), DXGI_STATUS_OCCLUDED and other return codes, the swap chain's frame
// statistics (PresentCount = frames DWM actually displayed, LastPresentCount = Present calls), CPU time, and a 5x3
// hit-test grid over its own rectangle (top stand-in / below / any other window: a user window moved over the test
// is visible in the record). summary.json per case carries the stand-in verdict with the S2.N8 thresholds: >= 25 fps
// in every full second, no interval > 1 s. Both windows are shown without activation (no focus is taken).
//
// DwmStandins covered <outDir> <case> <seconds> [x=40 y=40 w=640 h=360 sync=1]
// DwmStandins coveredseq <outDir> <secondsPerCase> covered,margins1,uncovered [x=... sync=...]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using D3DDevice = SharpDX.Direct3D11.Device;
using DxgiDevice = SharpDX.DXGI.Device;

internal sealed class CoveredBelowForm : Form
{
    private D3DDevice dev;
    private SwapChain1 sc;
    private RenderTargetView rtv;
    public long Frames;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
            return cp;
        }
    }
    public CoveredBelowForm(Rectangle r)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = r;
        Text = "DE_STANDIN_COVERED_BELOW";
        ShowInTaskbar = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        dev = new D3DDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        using (var dx = dev.QueryInterface<DxgiDevice>())
        using (var ad = dx.Adapter)
        using (var f2 = ad.GetParent<Factory2>())
        {
            var d1 = new SwapChainDescription1
            {
                Width = ClientSize.Width, Height = ClientSize.Height, Format = Format.R8G8B8A8_UNorm, BufferCount = 2,
                SwapEffect = SwapEffect.FlipDiscard, Usage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0), AlphaMode = AlphaMode.Unspecified, Scaling = Scaling.Stretch,
            };
            sc = new SwapChain1(f2, dev, Handle, ref d1);
            f2.MakeWindowAssociation(Handle, WindowAssociationFlags.IgnoreAll);
        }
        using (var bb = sc.GetBackBuffer<Texture2D>(0)) rtv = new RenderTargetView(dev, bb);
    }
    public Result Render(int sync)
    {
        float k = (Frames % 120) / 120f;   // a slow colour ramp: live content
        dev.ImmediateContext.ClearRenderTargetView(rtv, new RawColor4(0.2f + 0.6f * k, 0.1f, 0.8f - 0.6f * k, 1));
        var res = sc.Present(sync, PresentFlags.None);
        Frames++;
        return res;
    }
    /// <summary>(PresentCount, PresentRefreshCount, SyncRefreshCount, LastPresentCount); -1 when unavailable (disjoint).</summary>
    public (long, long, long, long) Statistics()
    {
        long last = -1;
        try { last = sc.LastPresentCount; } catch (SharpDXException) { }
        try
        {
            var s = sc.FrameStatistics;
            return (s.PresentCount, s.PresentRefreshCount, s.SyncRefreshCount, last);
        }
        catch (SharpDXException) { return (-1, -1, -1, last); }
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        rtv?.Dispose(); sc?.Dispose(); dev?.Dispose();
        base.OnFormClosed(e);
    }
}

internal static class Covered
{
    private static readonly string[] Cases = { "covered", "margins1", "marginsHole", "uncovered" };
    private static readonly double QpcMs = 1000.0 / Stopwatch.Frequency;

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static Rectangle AreaOf(Opts o) => new Rectangle(o.I("x", 40), o.I("y", 40), o.I("w", 640), o.I("h", 360));

    public static int Run(string[] a)
    {
        string outDir = a[1], kase = a[2];
        double seconds = double.Parse(a[3], CultureInfo.InvariantCulture);
        var o = new Opts(a, 4);
        return RunCase(outDir, kase, seconds, o) != null ? 0 : 1;
    }

    public static int RunSequence(string[] a)
    {
        string outDir = a[1];
        double seconds = double.Parse(a[2], CultureInfo.InvariantCulture);
        var cases = a[3].Split(',');
        var o = new Opts(a, 4);
        Directory.CreateDirectory(outDir);
        var all = new StringBuilder("{\"cases\":[");
        bool first = true, ok = true;
        foreach (var c in cases)
        {
            string s = RunCase(Path.Combine(outDir, c), c, seconds, o);
            if (s == null) { ok = false; continue; }
            all.Append(first ? "" : ",").Append(s);
            first = false;
            Thread.Sleep(1500);   // let DWM settle between cases
        }
        all.Append("]}");
        File.WriteAllText(Path.Combine(outDir, "summary.json"), all.ToString());
        Console.WriteLine(all.ToString());
        return ok ? 0 : 1;
    }

    /// <summary>Starts the roles of one case and waits; returns the case summary JSON, or null.</summary>
    private static string RunCase(string dir, string kase, double seconds, Opts o)
    {
        if (Array.IndexOf(Cases, kase) < 0) { Console.Error.WriteLine("unknown case " + kase); return null; }
        Directory.CreateDirectory(dir);
        string self = Process.GetCurrentProcess().MainModule.FileName;
        Rectangle r = AreaOf(o);
        long t0 = DateTime.UtcNow.AddSeconds(3).Ticks;
        string common = $"\"{dir}\" {kase} {t0} {F(seconds)} {r.X} {r.Y} {r.Width} {r.Height}{o.Pass()}";
        var below = Process.Start(new ProcessStartInfo(self, "cbelow " + common) { UseShellExecute = false });
        Process top = null;
        Thread.Sleep(700);
        if (kase != "uncovered")
            top = Process.Start(new ProcessStartInfo(self, "ctop " + common) { UseShellExecute = false });
        int wait = (int)((seconds + 40) * 1000);
        bool ok = below.WaitForExit(wait);
        if (top != null) ok &= top.WaitForExit(15000);
        if (!below.HasExited) below.Kill();
        if (top != null && !top.HasExited) top.Kill();
        string sum = Path.Combine(dir, "summary.json");
        if (!ok || !File.Exists(sum)) { Console.Error.WriteLine("case " + kase + " did not finish"); return null; }
        string s = File.ReadAllText(sum).Trim();
        Console.WriteLine(s);
        return s;
    }

    private static void WaitUntil(long ticks)
    {
        while (DateTime.UtcNow.Ticks < ticks) { Application.DoEvents(); Thread.Sleep(1); }
    }

    private static double Pct(List<double> v, double p)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        int i = (int)Math.Min(s.Count - 1, Math.Max(0, Math.Ceiling(p * s.Count) - 1));
        return s[i];
    }

    private static string Dist(List<double> v) =>
        v.Count == 0 ? "null" :
        $"{{\"mean\":{F(v.Average())},\"p50\":{F(Pct(v, 0.5))},\"p99\":{F(Pct(v, 0.99))},\"max\":{F(v.Max())}}}";

    /// <summary>The below (Subnautica) stand-in: a flip-model render loop with 1 Hz statistics.</summary>
    public static int BelowRole(string[] a)
    {
        string dir = a[1], kase = a[2];
        long t0 = long.Parse(a[3]);
        double seconds = double.Parse(a[4], CultureInfo.InvariantCulture);
        var r = new Rectangle(int.Parse(a[5]), int.Parse(a[6]), int.Parse(a[7]), int.Parse(a[8]));
        var o = new Opts(a, 9);
        int sync = o.I("sync", 1);
        var log = new Log(Path.Combine(dir, "below.log"));
        string statsPath = Path.Combine(dir, "below-stats.jsonl");
        File.WriteAllText(statsPath, "");
        Native.timeBeginPeriod(1);
        var form = new CoveredBelowForm(r);
        form.Show();
        Application.DoEvents();
        IntPtr me = form.Handle;
        uint myPid = (uint)Process.GetCurrentProcess().Id;
        log.W($"shown hwnd=0x{me.ToString("X")} case={kase} rect={r} sync={sync} exstyle=0x{Native.GetWindowLongW(me, Native.GWL_EXSTYLE):X8} fgIsMe={Native.GetForegroundWindow() == me}");
        // warm-up until t0 (the top stand-in is shown by then)
        while (DateTime.UtcNow.Ticks < t0) { Application.DoEvents(); form.Render(sync); }
        long tEnd = t0 + (long)(seconds * TimeSpan.TicksPerSecond);
        var proc = Process.GetCurrentProcess();
        double cpu0 = proc.TotalProcessorTime.TotalMilliseconds;
        long q0 = Stopwatch.GetTimestamp(), secStart = q0, lastEnd = q0;
        var present = new List<double>();
        var interval = new List<double>();
        var allIntervals = new List<double>();
        var perSecFps = new List<double>();
        var displayedFrac = new List<double>();
        long frames = 0, occluded = 0, other = 0, totalFrames = 0, totalOccluded = 0, totalOther = 0, gaps = 0;
        double maxInterval = 0;
        int sec = 0, foreignSeconds = 0;
        var st0 = form.Statistics();
        while (DateTime.UtcNow.Ticks < tEnd)
        {
            Application.DoEvents();
            long qa = Stopwatch.GetTimestamp();
            Result res = form.Render(sync);
            long qb = Stopwatch.GetTimestamp();
            double pm = (qb - qa) * QpcMs, im = (qb - lastEnd) * QpcMs;
            lastEnd = qb;
            present.Add(pm); interval.Add(im); allIntervals.Add(im);
            if (im > maxInterval) maxInterval = im;
            if (im > 1000) gaps++;
            frames++; totalFrames++;
            if ((uint)res.Code == 0x087A0001u) { occluded++; totalOccluded++; }
            else if (res.Code != 0) { other++; totalOther++; }
            if ((qb - secStart) * QpcMs >= 1000.0)
            {
                double span = (qb - secStart) * QpcMs / 1000.0;
                var st = form.Statistics();
                long dDisp = st.Item1 >= 0 && st0.Item1 >= 0 ? st.Item1 - st0.Item1 : -1;
                long dCalls = st.Item4 >= 0 && st0.Item4 >= 0 ? st.Item4 - st0.Item4 : -1;
                st0 = st;
                double fps = frames / span;
                perSecFps.Add(fps);
                if (dDisp >= 0 && dCalls > 0) displayedFrac.Add((double)dDisp / dCalls);
                // 5x3 hit-test grid over the below rectangle: which root window is on top at each point
                int nTop = 0, nBelow = 0, nOther = 0;
                for (int gy = 0; gy < 3; gy++)
                    for (int gx = 0; gx < 5; gx++)
                    {
                        var p = new Native.POINT { X = r.X + (2 * gx + 1) * r.Width / 10, Y = r.Y + (2 * gy + 1) * r.Height / 6 };
                        IntPtr h = Native.GetAncestor(Native.WindowFromPoint(p), 2);
                        string t = Native.Title(h);
                        if (t == "DE_STANDIN_COVERED_TOP") nTop++;
                        else if (h == me) nBelow++;
                        else nOther++;
                    }
                if (nOther > 0) foreignSeconds++;
                IntPtr fg = Native.GetForegroundWindow();
                double cpu = proc.TotalProcessorTime.TotalMilliseconds;
                proc.Refresh();
                string line = "{" +
                    $"\"case\":\"{kase}\",\"sec\":{sec},\"qpc\":{qb},\"frames\":{frames},\"fps\":{F(fps)}," +
                    $"\"presentMs\":{Dist(present)},\"intervalMs\":{Dist(interval)},\"occluded\":{occluded},\"otherHr\":{other}," +
                    $"\"displayedDelta\":{dDisp},\"presentCallsDelta\":{dCalls},\"presentCount\":{st.Item1}," +
                    $"\"presentRefreshCount\":{st.Item2},\"syncRefreshCount\":{st.Item3},\"lastPresentCount\":{st.Item4}," +
                    $"\"cpuMsPerS\":{F((cpu - cpu0) / span)},\"grid\":{{\"top\":{nTop},\"below\":{nBelow},\"other\":{nOther}}}," +
                    $"\"fgIsStandin\":{(Native.Title(fg).StartsWith("DE_STANDIN_") ? "true" : "false")}}}";
                cpu0 = cpu;
                File.AppendAllText(statsPath, line + Environment.NewLine);
                present.Clear(); interval.Clear();
                frames = 0; occluded = 0; other = 0; sec++;
                secStart = qb;
            }
        }
        double total = (Stopwatch.GetTimestamp() - q0) * QpcMs / 1000.0;
        double minFps = perSecFps.Count > 0 ? perSecFps.Min() : 0;
        bool pass = perSecFps.Count > 0 && minFps >= 25.0 && gaps == 0;
        string summary = "{" +
            $"\"case\":\"{kase}\",\"seconds\":{F(total)},\"frames\":{totalFrames},\"fpsMean\":{F(totalFrames / Math.Max(1e-9, total))}," +
            $"\"fpsMinPerSecond\":{F(minFps)},\"secondsBelow25fps\":{perSecFps.Count(x => x < 25.0)},\"fullSeconds\":{perSecFps.Count}," +
            $"\"intervalMs\":{Dist(allIntervals)},\"maxIntervalMs\":{F(maxInterval)},\"gapsOver1s\":{gaps}," +
            $"\"occluded\":{totalOccluded},\"otherHr\":{totalOther}," +
            $"\"displayedFraction\":{(displayedFrac.Count > 0 ? Dist(displayedFrac) : "null")}," +
            $"\"secondsWithForeignWindowOverTest\":{foreignSeconds},\"sync\":{sync},\"rect\":[{r.X},{r.Y},{r.Width},{r.Height}]," +
            $"\"standinVerdict\":\"{(pass ? "PASS" : "FAIL")}\",\"thresholds\":\">= 25 fps every full second, no interval > 1 s (ROADMAP S2.N8, stand-in form)\"}}";
        File.WriteAllText(Path.Combine(dir, "summary.json"), summary);
        log.W("summary " + summary);
        form.Close();
        Native.timeEndPeriod(1);
        return 0;
    }

    /// <summary>The top (SE) stand-in: bit-blt swap chain, opaque, optionally with DWM margins (alpha 1).</summary>
    public static int TopRole(string[] a)
    {
        string dir = a[1], kase = a[2];
        long t0 = long.Parse(a[3]);
        double seconds = double.Parse(a[4], CultureInfo.InvariantCulture);
        var r = new Rectangle(int.Parse(a[5]), int.Parse(a[6]), int.Parse(a[7]), int.Parse(a[8]));
        var log = new Log(Path.Combine(dir, "top.log"));
        Native.timeBeginPeriod(1);
        bool margins = kase == "margins1" || kase == "marginsHole";
        var form = new TopForm(r, false, margins, log)
        {
            NoActivate = true,
            Pattern = kase == "marginsHole" ? TopPattern.Alpha1Hole : (margins ? TopPattern.OpaqueAlpha1 : TopPattern.Opaque),
        };
        form.Text = "DE_STANDIN_COVERED_TOP";
        form.Show();
        Application.DoEvents();
        IntPtr me = form.Handle;
        int dwmHr = form.DwmHr;   // a local copy: TopForm is a MarshalByRefObject (CS1690)
        log.W($"shown hwnd=0x{me.ToString("X")} case={kase} margins={margins} dwmHr={(dwmHr == int.MinValue ? "none (no DWM call)" : "0x" + dwmHr.ToString("X8"))} fgIsMe={Native.GetForegroundWindow() == me} below=0x{Native.FindWindowW(null, "DE_STANDIN_COVERED_BELOW").ToString("X")}");
        long tEnd = t0 + (long)((seconds + 0.5) * TimeSpan.TicksPerSecond);
        long next = DateTime.UtcNow.Ticks;
        // SE-like pacing: about 120 frames per second (its frame cap on this PC), Present(1) on a bit-blt chain
        while (DateTime.UtcNow.Ticks < tEnd)
        {
            Application.DoEvents();
            form.RenderFrame();
            next += TimeSpan.TicksPerSecond / 120;
            long d = next - DateTime.UtcNow.Ticks;
            if (d > 0) Thread.Sleep((int)(d / TimeSpan.TicksPerMillisecond));
            else next = DateTime.UtcNow.Ticks;
        }
        log.W($"exit frames={form.Frames}");
        form.Close();
        Native.timeEndPeriod(1);
        return 0;
    }
}
