// Synthetic window stand-ins for the DeepEngineers display probe (no game is launched).
// See DwmStandins.csproj for what each role imitates. Usage:
// DwmStandins run <outDir> <blt|flip> <alpha0|alpha1> [act|noact] [gdi|d3d] [key=value...] (T-D0; starts both roles)
// DwmStandins below <outDir> <t0Ticks> <x> <y> <w> <h> [gdi|d3d] [key=value...]
// DwmStandins top <outDir> <t0Ticks> <x> <y> <w> <h> <blt|flip> <alpha0|alpha1> [act|noact] [key=value...]
// DwmStandins covered <outDir> <covered|margins1|uncovered|marginsHole> <seconds> [key=value...] (S2.N8 (1))
// DwmStandins coveredseq <outDir> <secondsPerCase> <case>[,<case>...] [key=value...] (several cases)
// act = the SE stand-in takes the foreground when shown (steals focus for ~4 s; check the human is idle first)
// noact = nothing takes the foreground (z-order raises by background processes are then unreliable)
// d3d = the Subnautica stand-in uses a flip-model swap chain that alternates red/blue (proves live content)
// key=value options of run/below/top (S2.T-D0 additions, ROADMAP S2 tools/tests):
// pattern=thirds|quarters top drawing. thirds: opaque green | 50 % green | clear.
// quarters (translucent case, R2.5): opaque white | 50 % white as SE stores it (linear blend
// into an _SRGB target: 0.735, alpha 0.5) | 50 % white corrected to a*sRGB(c) = 0.5 | clear.
// colour=R,G,B the GDI below window's colour (default 255,0,255 magenta; the translucent case uses a dark
// colour, e.g. 16,32,64)
// restyle=1 runtime-restyle variant: the below window is created WITHOUT WS_EX_NOACTIVATE|TOOLWINDOW,
// shown, then restyled the way ARCHITECTURE 5.1 restyles Subnautica at ReadyIdle
// (SW_HIDE -> exstyle |= NOACTIVATE|TOOLWINDOW -> SetWindowPos(FRAMECHANGED) ->
// SW_SHOWNOACTIVATE) before the captures; logs the styles before/after
// x=,y=,w=,h= the stand-in rectangle (default 60,60 600x240)
// Every role writes <outDir>/<role>.log; "top" also writes PNG captures of the composed screen region, but only
// when every sample point lies on a stand-in window (privacy guard: never saves the user's other windows), and a
// machine-readable <outDir>/captures.
// Results of the runs are in docs/recon/probe-display-input.md section 3.3; the covered-render stand-in
// (Covered.cs) writes below-stats.jsonl (1 Hz) and summary.json per case.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using D3DDevice = SharpDX.Direct3D11.Device;
using DxgiDevice = SharpDX.DXGI.Device;

internal static class Native
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
    [DllImport("user32.dll")] public static extern int GetWindowLongW(IntPtr h, int idx);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowLongPtrW(IntPtr h, int idx, IntPtr v);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS m);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int Left, Right, Top, Bottom; }
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    public const uint GW_HWNDPREV = 3;
    public const int GWL_EXSTYLE = -20;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    public const int WS_EX_TOOLWINDOW = 0x00000080, WS_EX_APPWINDOW = 0x00040000, WS_EX_NOACTIVATE = 0x08000000;
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;

    public static string Title(IntPtr h)
    {
        if (h == IntPtr.Zero) return "<none>";
        var sb = new StringBuilder(256);
        GetWindowTextW(h, sb, 256);
        return sb.Length == 0 ? "0x" + h.ToString("X") : sb.ToString();
    }
}

internal sealed class Log
{
    private readonly string path;
    public Log(string path) { this.path = path; File.WriteAllText(path, ""); }
    public void W(string s)
    {
        string line = DateTime.UtcNow.ToString("HH:mm:ss.fff") + " " + s;
        File.AppendAllText(path, line + Environment.NewLine);
    }
}

/// <summary>key=value options after the positional arguments (S2 additions; absent keys keep the T-D0 behaviour).</summary>
internal sealed class Opts
{
    private readonly Dictionary<string, string> kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Positional = new List<string>();
    public Opts(string[] a, int from)
    {
        for (int i = from; i < a.Length; i++)
        {
            int eq = a[i].IndexOf('=');
            if (eq > 0) kv[a[i].Substring(0, eq)] = a[i].Substring(eq + 1);
            else Positional.Add(a[i]);
        }
    }
    public string S(string k, string d) => kv.TryGetValue(k, out var v) ? v : d;
    public int I(string k, int d) => kv.TryGetValue(k, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : d;
    public double D(string k, double d) => kv.TryGetValue(k, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : d;
    public bool B(string k) => kv.TryGetValue(k, out var v) && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
    public bool Has(string k) => kv.ContainsKey(k);
    public Color Colour(string k, Color d)
    {
        if (!kv.TryGetValue(k, out var v)) return d;
        var p = v.Split(',');
        return Color.FromArgb(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]));
    }
    /// <summary>The options again as "k=v" tokens (passed through to the child roles).</summary>
    public string Pass()
    {
        var sb = new StringBuilder();
        foreach (var e in kv) sb.Append(' ').Append(e.Key).Append('=').Append(e.Value);
        return sb.ToString();
    }
}

internal sealed class BelowForm : Form
{
    // Subnautica stand-in. With d3d=true it owns a flip-model swap chain (Unity 2019.4 player setting
 // useFlipModelSwapchain=true in Subnautica) and animates its colour so captures can prove it is live.
    private readonly bool d3d;
    private readonly bool startPlain;    // restyle variant: created without NOACTIVATE|TOOLWINDOW, restyled later
    private D3DDevice dev;
    private SwapChain1 sc;
    private RenderTargetView rtv;
    public long Frames, PresentOk, PresentOccluded, PresentOther;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            if (!startPlain) cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
            return cp;
        }
    }
    public BelowForm(Rectangle r, bool d3d, Color colour, bool startPlain = false)
    {
        this.d3d = d3d;
        this.startPlain = startPlain;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = r;
        BackColor = colour;
        Text = "DE_STANDIN_BELOW";
        ShowInTaskbar = startPlain;   // the restyle variant starts as an ordinary window with a taskbar button
        if (d3d) SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!d3d) return;
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

    /// <summary>The ARCHITECTURE 5.1 ReadyIdle sequence applied to our own window at run time (S2.T-D0 restyle).</summary>
    public string Restyle()
    {
        IntPtr h = Handle;
        int before = Native.GetWindowLongW(h, Native.GWL_EXSTYLE);
        Native.ShowWindow(h, Native.SW_HIDE);
        int ex = (before | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtrW(h, Native.GWL_EXSTYLE, new IntPtr(ex));
        bool pos = Native.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        Native.ShowWindow(h, Native.SW_SHOWNOACTIVATE);
        int after = Native.GetWindowLongW(h, Native.GWL_EXSTYLE);
        return $"restyle exstyle before=0x{before:X8} after=0x{after:X8} noactivate={(after & Native.WS_EX_NOACTIVATE) != 0} " +
               $"toolwindow={(after & Native.WS_EX_TOOLWINDOW) != 0} framechanged={pos} visible={Native.IsWindowVisible(h)}";
    }

    public void RenderFrame()
    {
        if (rtv == null) return;
        // Alternates between pure red and pure blue every 8 frames.
        bool red = (Frames / 8) % 2 == 0;
        dev.ImmediateContext.ClearRenderTargetView(rtv, red ? new RawColor4(1, 0, 0, 1) : new RawColor4(0, 0, 1, 1));
        var res = sc.Present(1, PresentFlags.None);
        if (res.Code == 0) PresentOk++;
        else if ((uint)res.Code == 0x087A0001u) PresentOccluded++; // DXGI_STATUS_OCCLUDED
        else PresentOther++;
        Frames++;
    }
    public string Stats() => $"frames={Frames} presentOk={PresentOk} occluded={PresentOccluded} other={PresentOther}";
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        rtv?.Dispose(); sc?.Dispose(); dev?.Dispose();
        base.OnFormClosed(e);
    }
}

internal enum TopPattern { Thirds, Quarters, Opaque, OpaqueAlpha1, Alpha1Hole }

internal sealed class TopForm : Form
{
    private readonly bool flip, alpha;
    public bool NoActivate;
    public TopPattern Pattern = TopPattern.Thirds;
    protected override bool ShowWithoutActivation => NoActivate;
    private readonly Log log;
    private D3DDevice dev;
    private SwapChain sc;
    private RenderTargetView rtv;
    private DeviceContext1 ctx1;
    public long Frames;
    public int DwmHr = int.MinValue;

    public TopForm(Rectangle r, bool flip, bool alpha, Log log)
    {
        this.flip = flip; this.alpha = alpha; this.log = log;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = r;
        Text = "DE_STANDIN_TOP";
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int w = ClientSize.Width, h = ClientSize.Height;
        if (!flip)
        {
            // Same description as SE 1.210.014 MyPlatformRender.CreateSwapChain (DXGI 1.0, bitblt model).
            var desc = new SwapChainDescription
            {
                BufferCount = 2,
                Flags = SwapChainFlags.AllowModeSwitch,
                IsWindowed = true,
                ModeDescription = new ModeDescription(w, h, new Rational(60, 1), Format.R8G8B8A8_UNorm_SRgb),
                SampleDescription = new SampleDescription(1, 0),
                OutputHandle = Handle,
                Usage = Usage.RenderTargetOutput | Usage.ShaderInput,
                SwapEffect = SwapEffect.Discard,
            };
            D3DDevice.CreateWithSwapChain(DriverType.Hardware, DeviceCreationFlags.BgraSupport, desc, out dev, out sc);
        }
        else
        {
            dev = new D3DDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            using (var dx = dev.QueryInterface<DxgiDevice>())
            using (var ad = dx.Adapter)
            using (var f2 = ad.GetParent<Factory2>())
            {
                var d1 = new SwapChainDescription1
                {
                    Width = w, Height = h, Format = Format.R8G8B8A8_UNorm, BufferCount = 2,
                    SwapEffect = SwapEffect.FlipDiscard, Usage = Usage.RenderTargetOutput,
                    SampleDescription = new SampleDescription(1, 0), AlphaMode = AlphaMode.Unspecified,
                    Scaling = Scaling.Stretch,
                };
                sc = new SwapChain1(f2, dev, Handle, ref d1);
            }
        }
        using (var f = sc.GetParent<Factory>()) f.MakeWindowAssociation(Handle, WindowAssociationFlags.IgnoreAll);
        using (var bb = sc.GetBackBuffer<Texture2D>(0)) rtv = new RenderTargetView(dev, bb);
        ctx1 = dev.ImmediateContext.QueryInterface<DeviceContext1>();
        log.W($"d3d ready swap={(flip ? "FlipDiscard" : "Discard(bitblt)")} fmt={(flip ? "R8G8B8A8_UNorm" : "R8G8B8A8_UNorm_SRgb")} size={w}x{h} adapter=\"{AdapterName()}\" pattern={Pattern}");
        if (alpha)
        {
            var m = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            int hr = Native.DwmExtendFrameIntoClientArea(Handle, ref m);
            DwmHr = hr;
            log.W($"DwmExtendFrameIntoClientArea(-1) hr=0x{hr:X8}");
        }
    }

    private string AdapterName()
    {
        using (var dx = dev.QueryInterface<DxgiDevice>())
        using (var ad = dx.Adapter) return ad.Description.Description.Trim();
    }

    /// <summary>sRGB transfer function inverse: the linear value a clear must carry so that an _SRGB target stores `s`.</summary>
    public static float SrgbToLinear(double s) =>
        (float)(s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4));

    public SharpDX.Result RenderFrame()
    {
        if (rtv == null) return SharpDX.Result.Ok;
        int w = ClientSize.Width, h = ClientSize.Height;
        switch (Pattern)
        {
            case TopPattern.Thirds:
                // Everything starts fully transparent black (what SE's FullDraw clears to when it skips the scene).
                ctx1.ClearRenderTargetView(rtv, new RawColor4(0, 0, 0, 0));
                // Left third: opaque green "HUD". Middle third: 50 % green, premultiplied. Right third: untouched (alpha 0).
                ctx1.ClearView(rtv, new RawColor4(0, 1, 0, 1), new[] { new RawRectangle(0, 0, w / 3, h) }, 1);
                ctx1.ClearView(rtv, new RawColor4(0, 0.5f, 0, 0.5f), new[] { new RawRectangle(w / 3, 0, 2 * w / 3, h) }, 1);
                break;
            case TopPattern.Quarters:
                // S2.T-D0 translucent case (R2.5). A clear of an _SRGB view converts the linear colour to sRGB, exactly as
 // SE's premultiplied sprite blend over the (0,0,0,0) see-through clear stores it: 0.5 white -> 0.735, a 0.5.
                ctx1.ClearRenderTargetView(rtv, new RawColor4(0, 0, 0, 0));
                ctx1.ClearView(rtv, new RawColor4(1, 1, 1, 1), new[] { new RawRectangle(0, 0, w / 4, h) }, 1);
                ctx1.ClearView(rtv, new RawColor4(0.5f, 0.5f, 0.5f, 0.5f), new[] { new RawRectangle(w / 4, 0, w / 2, h) }, 1);
                float c = SrgbToLinear(0.5);   // stored 0.5 = a * sRGB(1): the R2.5 "a*sRGB(c)" correction
                ctx1.ClearView(rtv, new RawColor4(c, c, c, 0.5f), new[] { new RawRectangle(w / 2, 0, 3 * w / 4, h) }, 1);
                break;
            case TopPattern.Opaque:
            case TopPattern.OpaqueAlpha1:
                // S2.N8 covered-render top: an opaque scene (what SE draws above water), alpha 1 everywhere.
                ctx1.ClearRenderTargetView(rtv, new RawColor4(0.1f, 0.3f, 0.12f, 1));
                break;
            case TopPattern.Alpha1Hole:
                // ARCHITECTURE 4.4 alternative (a): alpha 1 everywhere except a 1x1 px alpha-0 region in a corner.
                ctx1.ClearRenderTargetView(rtv, new RawColor4(0.1f, 0.3f, 0.12f, 1));
                ctx1.ClearView(rtv, new RawColor4(0, 0, 0, 0), new[] { new RawRectangle(w - 1, h - 1, w, h) }, 1);
                break;
        }
        var res = sc.Present(1, PresentFlags.None);
        Frames++;
        return res;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        ctx1?.Dispose(); rtv?.Dispose(); sc?.Dispose(); dev?.Dispose();
        base.OnFormClosed(e);
    }
}

internal static class Program
{
    private static readonly Rectangle DefaultArea = new Rectangle(60, 60, 600, 240);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: run|below|top|covered|coveredseq|cbelow|ctop ..."); return 2; }
        switch (args[0])
        {
            case "run":
            {
                var o = new Opts(args, 4);
                string act = o.Positional.Count > 0 ? o.Positional[0] : "act";
                string belowMode = o.Positional.Count > 1 ? o.Positional[1] : "gdi";
                return Run(args[1], args[2] == "flip", args[3] == "alpha1", act, belowMode, o);
            }
            case "below": return Below(args);
            case "top": return Top(args);
            case "covered": return Covered.Run(args);
            case "coveredseq": return Covered.RunSequence(args);
            case "cbelow": return Covered.BelowRole(args);
            case "ctop": return Covered.TopRole(args);
        }
        return 2;
    }

    private static Rectangle AreaOf(Opts o) =>
        new Rectangle(o.I("x", DefaultArea.X), o.I("y", DefaultArea.Y), o.I("w", DefaultArea.Width), o.I("h", DefaultArea.Height));

    private static int Run(string outDir, bool flip, bool alpha, string act, string belowMode, Opts o)
    {
        Directory.CreateDirectory(outDir);
        string self = Process.GetCurrentProcess().MainModule.FileName;
        long t0 = DateTime.UtcNow.AddSeconds(3).Ticks;
        Rectangle area = AreaOf(o);
        string rect = $"{area.X} {area.Y} {area.Width} {area.Height}";
        string pass = o.Pass();
        var below = Process.Start(new ProcessStartInfo(self, $"below \"{outDir}\" {t0} {rect} {belowMode}{pass}") { UseShellExecute = false });
        Thread.Sleep(700);
        var top = Process.Start(new ProcessStartInfo(self, $"top \"{outDir}\" {t0} {rect} {(flip ? "flip" : "blt")} {(alpha ? "alpha1" : "alpha0")} {act}{pass}") { UseShellExecute = false });
        bool ok = top.WaitForExit(20000) & below.WaitForExit(20000);
        if (!top.HasExited) top.Kill();
        if (!below.HasExited) below.Kill();
        File.WriteAllText(Path.Combine(outDir, "run.done"), ok ? "ok" : "timeout");
        return ok ? 0 : 1;
    }

    private static Rectangle ParseRect(string[] a, int i) =>
        new Rectangle(int.Parse(a[i]), int.Parse(a[i + 1]), int.Parse(a[i + 2]), int.Parse(a[i + 3]));

    private static void At(long t0, double seconds)
    {
        long target = t0 + (long)(seconds * TimeSpan.TicksPerSecond);
        while (DateTime.UtcNow.Ticks < target) { Application.DoEvents(); Thread.Sleep(1); }
    }

    private static int Below(string[] a)
    {
        var log = new Log(Path.Combine(a[1], "below.log"));
        long t0 = long.Parse(a[2]);
        var o = new Opts(a, 7);
        bool d3d = o.Positional.Count > 0 && o.Positional[0] == "d3d";
        bool restyle = o.B("restyle");
        var form = new BelowForm(ParseRect(a, 3), d3d, o.Colour("colour", Color.FromArgb(255, 0, 255)), restyle);
        var timer = new System.Windows.Forms.Timer { Interval = 10 };
        timer.Tick += (s, e) => form.RenderFrame();
        form.Show();
        timer.Start();
        Application.DoEvents();
        IntPtr me = form.Handle;
        log.W($"shown hwnd=0x{me.ToString("X")} exstyle=0x{Native.GetWindowLongW(me, Native.GWL_EXSTYLE):X8} fg={Native.Title(Native.GetForegroundWindow())} colour={form.BackColor.R},{form.BackColor.G},{form.BackColor.B}");
        if (restyle)
        {
            // S2.T-D0 runtime-restyle variant: styles applied after the window was shown (T-D0 itself had them at
 // creation). Done 0.25 s after the show, before the top stand-in exists (it starts 0.7 s after this process),
 // so the top is created above the restyled window as in the real start order (Subnautica restyles at
 // ReadyIdle, before SE starts).
            long until = DateTime.UtcNow.AddMilliseconds(250).Ticks;
            while (DateTime.UtcNow.Ticks < until) { Application.DoEvents(); Thread.Sleep(1); }
            log.W("restyle(+0.25 s after show) " + form.Restyle() + $" fgIsMe={Native.GetForegroundWindow() == me}");
        }
        At(t0, 0.0); log.W($"t=0.0 fgIsMe={Native.GetForegroundWindow() == me} {form.Stats()}");
        At(t0, 0.75); log.W($"t=0.75 (top over me, transparent right part) {form.Stats()}");
        At(t0, 1.3); log.W($"t=1.3 fgIsMe={Native.GetForegroundWindow() == me} (me on top) {form.Stats()}");
        At(t0, 2.75); log.W($"t=2.75 (top over me again) {form.Stats()}");
        At(t0, 2.8);
        bool r = Native.SetWindowPos(me, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        log.W($"t=2.8 self-raise HWND_TOP|NOACTIVATE ret={r} err={Marshal.GetLastWin32Error()} fg={Native.Title(Native.GetForegroundWindow())} above={Native.Title(Native.GetWindow(me, Native.GW_HWNDPREV))}");
        At(t0, 3.9);
        log.W($"t=3.9 exit fgIsMe={Native.GetForegroundWindow() == me} exstyle=0x{Native.GetWindowLongW(me, Native.GWL_EXSTYLE):X8} {form.Stats()}");
        timer.Stop();
        form.Close();
        return 0;
    }

    private static int Top(string[] a)
    {
        string outDir = a[1];
        var log = new Log(Path.Combine(outDir, "top.log"));
        long t0 = long.Parse(a[2]);
        Rectangle r = ParseRect(a, 3);
        bool flip = a[7] == "flip", alpha = a[8] == "alpha1";
        var o = new Opts(a, 9);
        bool noact = o.Positional.Count > 0 && o.Positional[0] == "noact";
        var pattern = o.S("pattern", "thirds") == "quarters" ? TopPattern.Quarters : TopPattern.Thirds;
        var form = new TopForm(r, flip, alpha, log) { NoActivate = noact, Pattern = pattern };
        var timer = new System.Windows.Forms.Timer { Interval = 10 };
        timer.Tick += (s, e) => form.RenderFrame();
        form.Show();
        timer.Start();
        Application.DoEvents();
        IntPtr me = form.Handle;
        IntPtr below = Native.FindWindowW(null, "DE_STANDIN_BELOW");
        log.W($"shown hwnd=0x{me.ToString("X")} below=0x{below.ToString("X")} fgIsMe={Native.GetForegroundWindow() == me} fg={Native.Title(Native.GetForegroundWindow())}");
        var pts = pattern == TopPattern.Quarters
            ? new[] { ("q1_opaque_white", r.Width / 8), ("q2_half_white_as_stored", 3 * r.Width / 8),
                      ("q3_half_white_corrected", 5 * r.Width / 8), ("q4_clear", 7 * r.Width / 8) }
            : new[] { ("left_opaque", r.Width / 6), ("mid_half", r.Width / 2), ("right_clear", 5 * r.Width / 6) };

        At(t0, 0.0);
        log.W($"t=0.0 frames={form.Frames} fgIsMe={Native.GetForegroundWindow() == me} above={Native.Title(Native.GetWindow(me, Native.GW_HWNDPREV))}");
        At(t0, 0.3); Capture(outDir, "C1_top_over_below", r, log, pts);
        At(t0, 0.42); Capture(outDir, "C1b_top_over_below_120ms_later", r, log, pts);

        At(t0, 0.8);
        bool lower = Native.SetWindowPos(me, below, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        log.W($"t=0.8 lower-self-below ret={lower} err={Marshal.GetLastWin32Error()}");
        At(t0, 1.3);
        log.W($"t=1.3 fgIsMe={Native.GetForegroundWindow() == me} above={Native.Title(Native.GetWindow(me, Native.GW_HWNDPREV))}");
        Capture(outDir, "C2_after_lower", r, log, pts);

        At(t0, 1.8);
        bool raise = Native.SetWindowPos(me, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        log.W($"t=1.8 raise-self HWND_TOP ret={raise} err={Marshal.GetLastWin32Error()}");
        At(t0, 2.3);
        log.W($"t=2.3 fgIsMe={Native.GetForegroundWindow() == me} above={Native.Title(Native.GetWindow(me, Native.GW_HWNDPREV))}");
        Capture(outDir, "C3_after_raise", r, log, pts);

        At(t0, 3.3);
        log.W($"t=3.3 after below self-raise: fgIsMe={Native.GetForegroundWindow() == me} above={Native.Title(Native.GetWindow(me, Native.GW_HWNDPREV))}");
        Capture(outDir, "C4_after_below_selfraise", r, log, pts);

        At(t0, 3.8);
        log.W($"t=3.8 exit frames={form.Frames}");
        timer.Stop();
        form.Close();
        return 0;
    }

    internal static void Capture(string outDir, string name, Rectangle r, Log log, (string, int)[] pts)
    {
        // Privacy guard: only sample pixels that lie on one of our own stand-in windows; never save
 // an image that could contain the user's other windows.
        bool allOurs = true;
        var owners = new StringBuilder();
        var ownerList = new List<string>();
        foreach (var (label, x) in pts)
        {
            IntPtr h = Native.GetAncestor(Native.WindowFromPoint(new Native.POINT { X = r.X + x, Y = r.Y + r.Height / 2 }), 2 /*GA_ROOT*/);
            string t = Native.Title(h);
            owners.Append($" {label}@{t}");
            bool ours = t.StartsWith("DE_STANDIN_");
            ownerList.Add(ours ? t : "<other>");
            if (!ours) allOurs = false;
        }
        string jsonl = Path.Combine(outDir, "captures.jsonl");
        if (!allOurs)
        {
            log.W($"capture {name}: SKIPPED (a sample point is not on a stand-in window):{owners}");
            File.AppendAllText(jsonl, "{\"capture\":\"" + name + "\",\"skipped\":true}" + Environment.NewLine);
            return;
        }
        using (var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            bmp.Save(Path.Combine(outDir, name + ".png"), ImageFormat.Png);
            var sb = new StringBuilder();
            var js = new StringBuilder("{\"capture\":\"" + name + "\",\"skipped\":false,\"samples\":[");
            for (int i = 0; i < pts.Length; i++)
            {
                var (label, x) = pts[i];
                Color c = bmp.GetPixel(x, r.Height / 2);
                sb.Append($" {label}=({c.R},{c.G},{c.B})");
                js.Append(i == 0 ? "" : ",").Append("{\"label\":\"").Append(label).Append("\",\"x\":").Append(r.X + x)
                  .Append(",\"y\":").Append(r.Y + r.Height / 2).Append(",\"rgb\":[").Append(c.R).Append(',').Append(c.G)
                  .Append(',').Append(c.B).Append("],\"owner\":\"").Append(ownerList[i]).Append("\"}");
            }
            js.Append("]}");
            File.AppendAllText(jsonl, js + Environment.NewLine);
            log.W($"capture {name}:{sb} hit-test:{owners}");
        }
    }
}
