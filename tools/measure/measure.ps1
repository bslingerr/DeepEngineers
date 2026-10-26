<
.SYNOPSIS
  Samples the two game processes: CPU %, working set, GPU 3D-engine %, Responding, minimised, foreground.

.DESCRIPTION
  Port of the scratchpad sampler used for the 2026-10-11 measurements (MODLOG "Day 0 smoke test"), with the
  process names of the bridged session (ROADMAP §6): 'Legacy' (SE under Pulsar) and 'Subnautica'.
  Every sample is one row per process instance:
    time, t_s, name, pid, cpu_pct (of all logical cores), ws_mb, gpu3d_pct (sum of the pid's 3D engines),
    responding, iconic (main window minimised), foreground (this pid owns the foreground window), fg_name.
  CPU % is the TotalProcessorTime delta over the measured wall time of the interval. GPU % comes from the
  "\GPU Engine(*engtype_3D)\Utilization Percentage" counter, whose sampling also paces the loop; when that counter
  is not available the loop sleeps instead and gpu3d_pct is empty. Read-only: it never touches the processes.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\measure\measure.ps1 -Seconds 120 -Interval 1 -Csv tests\out\measure.csv
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\measure\measure.ps1 -Names "Legacy,Subnautica,SpaceEngineers"
#>
param(
    [int]$Seconds = 60,
    [ValidateRange(1, 60)] [int]$Interval = 5,
    [string]$Csv = '',
    # comma-separated (powershell -File cannot pass an array)
    [string]$Names = 'Legacy,Subnautica'
)
$ErrorActionPreference = 'Stop'
[System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture
$NameList = @($Names -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })

Add-Type @"
using System; using System.Runtime.InteropServices;
public static class DeMeasureUser32 {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
}
"@

$cores = (Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors
$gpuCounter = '\GPU Engine(*engtype_3D)\Utilization Percentage'
$gpuOk = $true
try { [void](Get-Counter -ListSet 'GPU Engine' -ErrorAction Stop) } catch { $gpuOk = $false }

$header = 'time,t_s,name,pid,cpu_pct,ws_mb,gpu3d_pct,responding,iconic,foreground,fg_name'
if ($Csv) {
    $dir = Split-Path -Parent $Csv
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    Set-Content -Path $Csv -Value $header -Encoding UTF8
}
Write-Host ("measure: {0} s, every {1} s, processes {2}, {3} logical cores, GPU counter {4}{5}" -f $Seconds, $Interval,
    ($NameList -join ', '), $cores, $(if ($gpuOk) { 'yes' } else { 'NO' }), $(if ($Csv) { ", CSV $Csv" } else { '' }))
Write-Host ("{0,-6} {1,-12} {2,-7} {3,8} {4,8} {5,7} {6,-6} {7,-6} {8,-3} {9}" -f 't_s', 'name', 'pid', 'cpu%', 'ws_mb', 'gpu%', 'resp', 'iconic', 'fg', 'fg_name')

$prevCpu = @{}
foreach ($n in $NameList) {
    foreach ($p in @(Get-Process -Name $n -ErrorAction SilentlyContinue)) { $prevCpu[$p.Id] = $p.TotalProcessorTime.TotalSeconds }
}
$sw = [Diagnostics.Stopwatch]::StartNew()
$last = $sw.Elapsed.TotalSeconds
while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    $gpu = @{}
    $gpuSampled = $false
    if ($gpuOk) {
        try {
            $c = Get-Counter -Counter $gpuCounter -SampleInterval $Interval -MaxSamples 1 -ErrorAction Stop
            foreach ($s in $c.CounterSamples) {
                if ($s.InstanceName -match 'pid_(\d+)_') {
                    $id = [int]$Matches[1]
                    if (-not $gpu.ContainsKey($id)) { $gpu[$id] = 0.0 }
                    $gpu[$id] += $s.CookedValue
                }
            }
            $gpuSampled = $true
        } catch { Start-Sleep -Seconds $Interval }
    } else { Start-Sleep -Seconds $Interval }

    $now = $sw.Elapsed.TotalSeconds
    $dt = [math]::Max(0.001, $now - $last)
    $last = $now
    $fgPid = [uint32]0
    [void][DeMeasureUser32]::GetWindowThreadProcessId([DeMeasureUser32]::GetForegroundWindow(), [ref]$fgPid)
    $fgName = (Get-Process -Id $fgPid -ErrorAction SilentlyContinue).ProcessName
    $stamp = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss.fff')
    foreach ($n in $NameList) {
        $procs = @(Get-Process -Name $n -ErrorAction SilentlyContinue)
        if ($procs.Count -eq 0) {
            $row = @($stamp, [math]::Round($now, 1), $n, '', '', '', '', 'gone', '', '', $fgName)
        }
        foreach ($p in $procs) {
            $cpu = ''
            try {
                $t = $p.TotalProcessorTime.TotalSeconds
                if ($prevCpu.ContainsKey($p.Id)) { $cpu = [math]::Round(100.0 * ($t - $prevCpu[$p.Id]) / $dt / $cores, 1) }
                $prevCpu[$p.Id] = $t
            } catch { $cpu = '' }
            $g = if ($gpuSampled) { if ($gpu.ContainsKey($p.Id)) { [math]::Round($gpu[$p.Id], 1) } else { 0 } } else { '' }
            $iconic = $false
            if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $iconic = [DeMeasureUser32]::IsIconic($p.MainWindowHandle) }
            $row = @($stamp, [math]::Round($now, 1), $n, $p.Id, $cpu, [int]($p.WorkingSet64 / 1MB), $g, $p.Responding,
                $iconic, ($p.Id -eq $fgPid), $fgName)
            Write-Host ("{0,-6} {1,-12} {2,-7} {3,8} {4,8} {5,7} {6,-6} {7,-6} {8,-3} {9}" -f $row[1], $row[2], $row[3],
                $row[4], $row[5], $row[6], $row[7], $row[8], $(if ($row[9]) { 'FG' } else { '' }), $row[10])
            if ($Csv) { Add-Content -Path $Csv -Value (($row | ForEach-Object { "$_" }) -join ',') -Encoding UTF8 }
        }
        if ($procs.Count -eq 0) {
            Write-Host ("{0,-6} {1,-12} {2,-7} {3,8} {4,8} {5,7} {6,-6} {7,-6} {8,-3} {9}" -f $row[1], $row[2], '-', '-', '-', '-', 'gone', '-', '', $row[10])
            if ($Csv) { Add-Content -Path $Csv -Value (($row | ForEach-Object { "$_" }) -join ',') -Encoding UTF8 }
        }
    }
}
