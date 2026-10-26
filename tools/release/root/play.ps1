<
.SYNOPSIS
  DeepEngineers: play (S9). Started by Play.cmd; Windows PowerShell 5.1. The release twin of the repository's
  play.ps1 (the owner's launcher), without its development parts.

.DESCRIPTION
  1. Steam must run and both games must be closed (the plugins need a clean start).
  2. launcher.py backup: zip backups of what a DeepEngineers session can touch (your DeepEngineers worlds,
     SpaceEngineers.cfg, the Water Mod's client settings, Subnautica's saves and logs) into
     %LOCALAPPDATA%\DeepEngineers\backups (the newest 3 per set are kept).
  3. launcher.py run: the checks (preflight), Subnautica first as the hidden renderer, then Space Engineers through
     Pulsar. This window waits until both games have closed; closing Space Engineers normally closes Subnautica too.
  -Continue loads the last world directly (Pulsar's -continue).
#>
[CmdletBinding()]
param([switch]$Continue, [switch]$NoPause, [switch]$AutoUpdate, [switch]$RefreshBackups, [switch]$Preset)

$ErrorActionPreference = 'Stop'
$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

function Find-Python {
    $candidates = @(@('py', '-3'), @('python'), @('python3'))
    foreach ($c in $candidates) {
        $exe = $c[0]
        if (-not (Get-Command $exe -ErrorAction SilentlyContinue)) { continue }
        $extra = @()
        if ($c.Count -gt 1) { $extra = @($c[1..($c.Count - 1)]) }
        $out = $null
        try {
            $out = & $exe @extra -c "import sys; print('%d.%d' % sys.version_info[:2])" 2>$null
        } catch {
            continue
        }
        if ($LASTEXITCODE -ne 0 -or -not $out) { continue }
        $v = [string]($out | Select-Object -First 1)
        if ($v -match '^(\d+)\.(\d+)$') {
            if ([int]$Matches[1] -eq 3 -and [int]$Matches[2] -ge 10) { return , (@($exe) + $extra) }
        }
    }
    return $null
}

function Stop-Here([int]$code) {
    if ($code -ne 0 -and -not $NoPause) {
        Write-Host ''
        [void](Read-Host 'Press Enter to close this window')
    }
    exit $code
}

$manifest = Join-Path $env:LOCALAPPDATA 'DeepEngineers\install\install-manifest.json'
if (-not (Test-Path -LiteralPath $manifest)) { Write-Host 'DeepEngineers is not installed: run Install.cmd first.'; Stop-Here 1 }
if (-not $Preset) {
    if (-not (Get-Process steam -ErrorAction SilentlyContinue)) { Write-Host 'Start Steam first, then run Play.cmd again.'; Stop-Here 1 }
    foreach ($n in @('SpaceEngineers', 'Legacy', 'Subnautica')) {
        if (Get-Process $n -ErrorAction SilentlyContinue) {
            Write-Host "$n is still running: close it first (the plugins need a clean start)."
            Stop-Here 1
        }
    }
}
# S9 one-click: the interpreter Install.cmd brought (%LOCALAPPDATA%\DeepEngineers\python), else a Python 3.10+ on the PC
$py = $null
$embedded = Join-Path $env:LOCALAPPDATA 'DeepEngineers\python\python.exe'
if ((Test-Path -LiteralPath $embedded) -and (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'DeepEngineers\python\deepengineers-python.json'))) {
    $py = @($embedded)
} else {
    $py = Find-Python
}
if ($null -eq $py) { Write-Host 'No Python found: run Install.cmd again (it brings its own Python).'; Stop-Here 1 }
$pyExe = $py[0]
$pyArgs = @()
if ($py.Count -gt 1) { $pyArgs = @($py[1..($py.Count - 1)]) }
$launcher = Join-Path $root 'launcher\launcher.py'
# Preset.cmd / Play.cmd -Preset: the performance preset question again, no game started
if ($Preset) {
    & $pyExe @pyArgs @('-I', '-B', $launcher, 's10', '--preset', 'ask')
    Stop-Here $LASTEXITCODE
}
# S9 round 2: the updater. launcher.py update asks GitHub for a newer DeepEngineers release and, on your yes
# (or -AutoUpdate), installs it and repairs the install; exit 3 = updated: this script starts the new launcher.
# DEEPENGINEERS_UPDATE=off skips the check. A private repository or no network: "check unavailable", never a stop.
$updateArgs = @('-I', '-B', $launcher, 'update')
if ($AutoUpdate) { $updateArgs += '--yes' }
& $pyExe @pyArgs @updateArgs
if ($LASTEXITCODE -eq 3) {
    Write-Host 'DeepEngineers was updated: starting the new version.'
    $again = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'play.ps1'))
    if ($Continue) { $again += '-Continue' }
    if ($NoPause) { $again += '-NoPause' }
    & powershell.exe @again
    exit $LASTEXITCODE
}

# S9 round 2: a backup set is renewed only when its newest zip is older than 7 days (2 kept); -RefreshBackups forces all three
$backupArgs = @('-I', '-B', $launcher, 'backup', '--keep', '2')
if (-not $RefreshBackups) { $backupArgs += @('--max-age-days', '7') }
& $pyExe @pyArgs @backupArgs
if ($LASTEXITCODE -ne 0) { Write-Host "backup failed (exit $LASTEXITCODE): nothing started"; Stop-Here $LASTEXITCODE }

$runArgs = @('-I', '-B', $launcher, 'run')
if ($RefreshBackups) { $runArgs += '--refresh-saves-backup' }
if ($Continue) { $runArgs += '--continue' }
& $pyExe @pyArgs @runArgs
Stop-Here $LASTEXITCODE
