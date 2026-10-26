<
.SYNOPSIS
  GDI capture of the composed desktop (a region or a monitor) to a PNG, plus a JSON sidecar with sampled pixels.

.DESCRIPTION
  Port of the Day-0 scratchpad shot.ps1 (MODLOG 2026-10-11): Graphics.CopyFromScreen reads the DWM-composed desktop,
  so it sees what the player sees, including per-pixel DWM transparency (S2.T-D0 proved it on this PC). Used by the
  S2 session (launcher.py s2: menus over Subnautica, calibration colour, composed pixels; ROADMAP S2 tools/tests).
  - The region is in virtual-screen coordinates (-Region x,y,w,h), or a whole monitor (-Screen; -1 = primary).
  - Samples are read at full resolution from the captured bitmap (the PNG may be downscaled with -Scale): by default the
    centre and the four corners inset by 8 px (the T-D1 probe set of docs/recon/probe-display-input.md 3.7), or the
    points of -Samples "x,y;x,y" (absolute screen coordinates; a label may precede a point: "centre=1280,720").
  - Each sample records the top-level window that owns the point (WindowFromPoint -> GetAncestor(GA_ROOT)): PID and
    process name; its title only for the games and our stand-ins (titles starting DE_).
  - Privacy guard (-OnlyOwners "Legacy,Subnautica,DwmStandins"): when any sample point lies on a window of another
    process, no PNG is written and the foreign points' pixel values are withheld (the sidecar says so).
  - The sidecar (<Out>.json, or -Json) carries the QPC of the capture ([Stopwatch]::GetTimestamp(), the same clock as
    the games and the launcher), the region, the samples and the guard result.
  Read-only: it never moves, activates or changes a window.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture\desktop_shot.ps1 -Out tests\out\s2\x\menu-inventory.png -Screen -1 -Scale 0.5
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture\desktop_shot.ps1 -Out c.png -Region 0,0,2560,1440 -Samples "centre=1280,720;hud=1280,1400" -OnlyOwners Legacy,Subnautica
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture\desktop_shot.ps1 -SelfTest
#>
param(
    [string]$Out = '',
    [string]$Json = '',
    [string]$Region = '',
    [int]$Screen = -1,
    [double]$Scale = 1.0,
    [string]$Samples = '',
    [string]$OnlyOwners = '',
    [switch]$NoPng,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
[System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
if (-not ('DeShot' -as [type])) {
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing -TypeDefinition @"
using System; using System.Runtime.InteropServices; using System.Text; using System.Windows.Forms; using System.Drawing;
public static class DeShot {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    public static IntPtr RootAt(int x, int y) { var p = new POINT { X = x, Y = y }; return GetAncestor(WindowFromPoint(p), 2); }
    public static uint Pid(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }
    public static string Title(IntPtr h) { var sb = new StringBuilder(256); GetWindowTextW(h, sb, 256); return sb.ToString(); }
}
public class DeShotTestForm : Form {
    // a no-activate, topmost, borderless window of a known colour (self-test only)
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; return cp; } }
    public DeShotTestForm(Rectangle r, Color c) {
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; Bounds = r; BackColor = c;
        ShowInTaskbar = false; TopMost = true; Text = "DE_SHOT_SELFTEST";
    }
}
"@
}

function Get-Region {
    if ($Region) {
        $p = $Region.Split(',') | ForEach-Object { [int]$_ }
        return New-Object System.Drawing.Rectangle $p[0], $p[1], $p[2], $p[3]
    }
    $scr = if ($Screen -lt 0) { [System.Windows.Forms.Screen]::PrimaryScreen } else { [System.Windows.Forms.Screen]::AllScreens[$Screen] }
    return $scr.Bounds
}

function Get-SamplePoints([System.Drawing.Rectangle]$r, [string]$spec) {
    $pts = @()
    if ($spec) {
        $i = 0
        foreach ($tok in $spec.Split(';')) {
            $t = $tok.Trim(); if (-not $t) { continue }
            $label = "p$i"
            if ($t.Contains('=')) { $label, $t = $t.Split('=', 2) }
            $xy = $t.Split(',')
            $pts += [pscustomobject]@{ label = $label; x = [int]$xy[0]; y = [int]$xy[1] }
            $i++
        }
        return $pts
    }
    $in = 8
    return @(
        [pscustomobject]@{ label = 'centre'; x = $r.X + [int]($r.Width / 2); y = $r.Y + [int]($r.Height / 2) },
        [pscustomobject]@{ label = 'topLeft'; x = $r.X + $in; y = $r.Y + $in },
        [pscustomobject]@{ label = 'topRight'; x = $r.Right - 1 - $in; y = $r.Y + $in },
        [pscustomobject]@{ label = 'bottomLeft'; x = $r.X + $in; y = $r.Bottom - 1 - $in },
        [pscustomobject]@{ label = 'bottomRight'; x = $r.Right - 1 - $in; y = $r.Bottom - 1 - $in }
    )
}

function Invoke-Shot([string]$outPng, [string]$jsonPath, [System.Drawing.Rectangle]$r, [string]$spec, [string]$owners, [bool]$noPng, [double]$scale) {
    $allowed = @($owners.Split(',') | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
    $pts = Get-SamplePoints $r $spec
    $samples = @()
    $foreign = 0
    foreach ($p in $pts) {
        $h = [DeShot]::RootAt($p.x, $p.y)
        $wpid = [DeShot]::Pid($h)
        $pname = ''
        try { $pname = (Get-Process -Id $wpid -ErrorAction Stop).ProcessName } catch { $pname = '?' }
        $t = [DeShot]::Title($h)
        $isOurs = $pname -match '^(Legacy|SpaceEngineers|Subnautica|DwmStandins)$' -or $t.StartsWith('DE_') -or $wpid -eq $PID
        $isAllowed = ($allowed.Count -eq 0) -or ($allowed -contains $pname.ToLowerInvariant()) -or $t.StartsWith('DE_')
        if (-not $isAllowed) { $foreign++ }
        $samples += [pscustomobject]@{ label = $p.label; x = $p.x; y = $p.y; rgb = $null; inRegion = $r.Contains($p.x, $p.y)
            owner = [pscustomobject]@{ hwnd = ('0x{0:x}' -f $h.ToInt64()); pid = $wpid; process = $pname; title = $(if ($isOurs) { $t } else { '<redacted>' }) }
            foreign = (-not $isAllowed) }
    }
    $q = [System.Diagnostics.Stopwatch]::GetTimestamp()
    $bmp = New-Object System.Drawing.Bitmap $r.Width, $r.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Location, [System.Drawing.Point]::Empty, $r.Size)
    $g.Dispose()
    $q2 = [System.Diagnostics.Stopwatch]::GetTimestamp()
    foreach ($s in $samples) {
        if ($s.inRegion -and -not $s.foreign) {
            $c = $bmp.GetPixel($s.x - $r.X, $s.y - $r.Y)
            $s.rgb = @([int]$c.R, [int]$c.G, [int]$c.B)
        }
    }
    $written = $null
    $skipped = $false
    if (-not $noPng -and $outPng) {
        if ($foreign -gt 0) { $skipped = $true }
        else {
            $dir = Split-Path -Parent $outPng
            if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
            if ($scale -ne 1.0) {
                $w = [Math]::Max(1, [int]($r.Width * $scale)); $hh = [Math]::Max(1, [int]($r.Height * $scale))
                $small = New-Object System.Drawing.Bitmap $w, $hh
                $g2 = [System.Drawing.Graphics]::FromImage($small)
                $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g2.DrawImage($bmp, 0, 0, $w, $hh)
                $g2.Dispose()
                $small.Save($outPng, [System.Drawing.Imaging.ImageFormat]::Png)
                $small.Dispose()
            } else { $bmp.Save($outPng, [System.Drawing.Imaging.ImageFormat]::Png) }
            $written = (Resolve-Path $outPng).Path
        }
    }
    $bmp.Dispose()
    $fg = [DeShot]::GetForegroundWindow()
    $doc = [pscustomobject]@{
        tool = 'desktop_shot'; utc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
        qpc = $q; qpcAfter = $q2; qpcFrequency = [System.Diagnostics.Stopwatch]::Frequency
        region = @($r.X, $r.Y, $r.Width, $r.Height); scale = $scale; png = $written; pngSkippedByGuard = $skipped
        guard = $(if ($allowed.Count) { $allowed } else { $null }); foreignSamples = $foreign
        foregroundPid = [DeShot]::Pid($fg); samples = $samples
    }
    $js = $doc | ConvertTo-Json -Depth 6 -Compress
    if ($jsonPath) {
        $dir = Split-Path -Parent $jsonPath
        if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
        [System.IO.File]::WriteAllText($jsonPath, $js, (New-Object System.Text.UTF8Encoding $false))
    }
    return $doc
}

if ($SelfTest) {
    # A small topmost no-activate window of a known colour; capture it; check the centre pixel and the sidecar.
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("de_shot_selftest_{0}" -f $PID)
    New-Item -ItemType Directory -Force $tmp | Out-Null
    $rect = New-Object System.Drawing.Rectangle 120, 120, 160, 100
    $colour = [System.Drawing.Color]::FromArgb(12, 200, 34)
    $f = New-Object DeShotTestForm $rect, $colour
    $fgBefore = [DeShot]::GetForegroundWindow()
    $f.Show()
    for ($i = 0; $i -lt 30; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 20 }
    $png = Join-Path $tmp 'shot.png'
    $doc = Invoke-Shot $png ($png + '.json') $rect "centre=200,170;outside=119,119" '' $false 1.0
    $fgAfter = [DeShot]::GetForegroundWindow()
    $f.Close(); $f.Dispose()
    $ok = $true
    $c = ($doc.samples | Where-Object { $_.label -eq 'centre' })
    $pass1 = $c.rgb -ne $null -and [Math]::Abs($c.rgb[0] - 12) -le 2 -and [Math]::Abs($c.rgb[1] - 200) -le 2 -and [Math]::Abs($c.rgb[2] - 34) -le 2
    Write-Host ("SELFTEST centre pixel = window colour (12,200,34)          {0} {1}" -f $(if ($pass1) { 'PASS' } else { 'FAIL' }), ($c.rgb -join ','))
    $o = ($doc.samples | Where-Object { $_.label -eq 'outside' })
    $pass2 = (-not $o.inRegion) -and ($o.rgb -eq $null)
    Write-Host ("SELFTEST point outside the region: no pixel value           {0}" -f $(if ($pass2) { 'PASS' } else { 'FAIL' }))
    $pass3 = (Test-Path $png) -and (Test-Path ($png + '.json')) -and ($doc.qpc -gt 0)
    Write-Host ("SELFTEST PNG + sidecar written, QPC stamped                 {0}" -f $(if ($pass3) { 'PASS' } else { 'FAIL' }))
    $pass4 = $c.owner.title -eq 'DE_SHOT_SELFTEST'
    Write-Host ("SELFTEST owner of the centre = the test window              {0} {1}" -f $(if ($pass4) { 'PASS' } else { 'FAIL' }), $c.owner.title)
    # guard: the test window (title DE_...) is accepted; a point on any other process's window (just outside the test
 # window) makes the PNG skipped and its pixel withheld
    $f2 = New-Object DeShotTestForm $rect, $colour
    $f2.Show()
    for ($i = 0; $i -lt 15; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 20 }
    $doc2 = Invoke-Shot (Join-Path $tmp 'ok.png') '' $rect "centre=200,170" 'NoSuchProcess' $false 1.0
    $doc3 = Invoke-Shot (Join-Path $tmp 'guarded.png') '' $rect "centre=200,170;outside=119,119" 'NoSuchProcess' $false 1.0
    $f2.Close(); $f2.Dispose()
    $out3 = ($doc3.samples | Where-Object { $_.label -eq 'outside' })
    $pass5 = (-not $doc2.pngSkippedByGuard) -and (Test-Path (Join-Path $tmp 'ok.png')) -and
             ((-not $out3.foreign) -or ($doc3.pngSkippedByGuard -and -not (Test-Path (Join-Path $tmp 'guarded.png')) -and $out3.rgb -eq $null))
    Write-Host ("SELFTEST guard: DE_ window accepted; a foreign point skips the PNG {0} (foreign={1})" -f $(if ($pass5) { 'PASS' } else { 'FAIL' }), $out3.foreign)
    $pass6 = $fgBefore -eq $fgAfter
    Write-Host ("SELFTEST foreground unchanged by the test window            {0}" -f $(if ($pass6) { 'PASS' } else { 'FAIL' }))
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    $ok = $pass1 -and $pass2 -and $pass3 -and $pass4 -and $pass5 -and $pass6
    Write-Host ("DESKTOP_SHOT SELFTEST {0}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }))
    if ($ok) { exit 0 } else { exit 1 }
}

if (-not $Out -and -not $NoPng) { Write-Error 'usage: -Out <png> [-Region x,y,w,h | -Screen n] [-Samples ...] [-OnlyOwners a,b] [-Scale s] [-NoPng] | -SelfTest'; exit 4 }
$jsonPath = if ($Json) { $Json } elseif ($Out) { $Out + '.json' } else { '' }
$r = Get-Region
$doc = Invoke-Shot $Out $jsonPath $r $Samples $OnlyOwners ([bool]$NoPng) $Scale
$doc | ConvertTo-Json -Depth 6 -Compress
if ($doc.pngSkippedByGuard) { exit 3 }
exit 0
