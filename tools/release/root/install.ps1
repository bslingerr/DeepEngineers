<
.SYNOPSIS
  DeepEngineers installer and uninstaller (S9). Started by Install.cmd / Uninstall.cmd; Windows PowerShell 5.1.

.DESCRIPTION
  No Python needed on the PC (S9 one-click, 2026-10-24): the first run downloads the Python interpreter pinned in
  release.json (python.org's embeddable package; version, URL and SHA-256 from launcher\de_pins.py) into
  %LOCALAPPDATA%\DeepEngineers\python, checks its SHA-256 before unpacking and checks that it starts; Play.cmd and the
  desktop entry use the same interpreter. When it cannot be had (no network, a blocked file, a release without the
  pin) a Python 3.10 or newer already on the PC is used instead (the py launcher, python, python3; not the Microsoft
  Store stub). Then the "downloaded from the internet" mark is removed from this folder's files and
  launcher\de_install.py runs, printing one PASS / WARN / FAIL line per step:
    install    release folder check, games closed, Steam and both games, pinned builds, Subnautica terrain data,
               Workshop mods (opens their Steam pages and waits for the download), the Subnautica host save (waits
               while you make it), Pulsar, BepInEx, the two plugins, the Pulsar profile, the world mod, your template
               world, the desktop and Start-menu entries, the install manifest, then the launcher's checks (preflight).
               Run it again at any time: it repairs what is missing or changed and keeps everything else.
    -Check     the detection steps only; nothing is written (the interpreter is still fetched if needed).
    -Uninstall everything the install manifest recorded, backwards (see UNINSTALL.md), then the interpreter.
  No step needs administrator rights unless your Steam library folder is not writable for your user; the step then
  says so (right-click Install.cmd > Run as administrator).

.EXAMPLE
  Install.cmd
  Install.cmd -WorldName "Planet 4546B" -OfflineDir "D:\Downloads;E:\zips"   (folders holding the pinned zips)
  Uninstall.cmd -RemoveWorlds
#>
[CmdletBinding()]
param(
    [string]$WorldName = '',
    [string]$PulsarDir = '',
    [string]$OfflineDir = '',
    [string]$SteamId = '',
    [string]$SteamRoot = '',
    [switch]$Check,
    [switch]$Uninstall,
    [switch]$RemoveWorlds,
    [switch]$ForceWorldMod,
    [switch]$RemoveBackups,
    [switch]$KeepData,
    [switch]$NoPreflight,
    [switch]$DryRun,
    [switch]$NoPrompt,
    [switch]$NoPause,
    [switch]$NoWait,
    [switch]$NoShortcuts,
    [switch]$BootstrapOnly
)

$ErrorActionPreference = 'Stop'
$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$local = Join-Path $env:LOCALAPPDATA 'DeepEngineers'
$pyHome = Join-Path $local 'python'
$downloads = Join-Path $local 'downloads'
$markerName = 'deepengineers-python.json'
$script:why = ''

function Stop-Here([int]$code) {
    if (-not $NoPause) {
        Write-Host ''
        [void](Read-Host 'Press Enter to close this window')
    }
    exit $code
}

function Find-Python {
    # a Python >= 3.10 already on the PC: @(exe, extra args...), or $null
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

function Get-Pin {
    # release.json's pins.python (written by make_release.py from de_pins.PYTHON_EMBED), or $null
    $rj = Join-Path $root 'release.json'
    if (-not (Test-Path -LiteralPath $rj)) { return $null }
    try { $doc = Get-Content -LiteralPath $rj -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $null }
    if ($null -eq $doc.pins -or $null -eq $doc.pins.python) { return $null }
    return $doc.pins.python
}

function Test-DePython([string]$exe, [string]$version) {
    # the interpreter starts, is the pinned version and has every module the installer and the launcher import
    if (-not (Test-Path -LiteralPath $exe)) { return $false }
    $out = $null
    try {
        $out = & $exe -I -B -c "import sys, ssl, ctypes, hashlib, json, zipfile, winreg, urllib.request; print('%d.%d.%d' % sys.version_info[:3])" 2>$null
    } catch {
        return $false
    }
    if ($LASTEXITCODE -ne 0 -or -not $out) { return $false }
    return ([string]($out | Select-Object -First 1)).Trim() -eq [string]$version
}

function Get-PinnedZip($pin) {
    # the pinned zip, SHA-256 verified: an -OfflineDir folder, the download cache, else downloaded now; $null on failure
    $cache = Join-Path $downloads $pin.file
    $cands = @()
    foreach ($d in ($OfflineDir -split ';')) { if ($d.Trim() -ne '') { $cands += (Join-Path $d.Trim() $pin.file) } }
    $cands += $cache
    foreach ($c in $cands) {
        if (-not (Test-Path -LiteralPath $c)) { continue }
        if ((Get-FileHash -LiteralPath $c -Algorithm SHA256).Hash.ToLower() -eq $pin.sha256) { return $c }
        Write-Host "WARN python     $c has a different SHA-256 than the pinned $($pin.file): ignored"
        if ($c -eq $cache) { Remove-Item -LiteralPath $c -Force -ErrorAction SilentlyContinue }
    }
    New-Item -ItemType Directory -Force -Path $downloads | Out-Null
    $part = "$cache.part"
    Write-Host "INFO python     downloading $($pin.file) ($($pin.size) bytes) from $($pin.url)"
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $wc = New-Object System.Net.WebClient
        $wc.Headers.Add('User-Agent', 'DeepEngineers-installer')
        $wc.DownloadFile([string]$pin.url, $part)
    } catch {
        Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue
        $script:why = "the download of $($pin.url) failed ($($_.Exception.Message))"
        return $null
    }
    $len = (Get-Item -LiteralPath $part).Length
    $hash = (Get-FileHash -LiteralPath $part -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne $pin.sha256) {
        Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue
        $script:why = "the downloaded $($pin.file) ($len bytes) has SHA-256 $hash, not the pinned $($pin.sha256): deleted, nothing unpacked"
        return $null
    }
    Move-Item -LiteralPath $part -Destination $cache -Force
    Write-Host "INFO python     downloaded $($pin.file) ($len bytes, SHA-256 verified)"
    return $cache
}

function Install-DePython($pin) {
    # unpacks into python.new, checks it starts, then renames it to python (a broken unpack never becomes "python")
    $zip = Get-PinnedZip $pin
    if ($null -eq $zip) { return $null }
    $new = "$pyHome.new"
    try {
        if (Test-Path -LiteralPath $new) { Remove-Item -LiteralPath $new -Recurse -Force }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $new)
        Get-ChildItem -LiteralPath $new -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
    } catch {
        Remove-Item -LiteralPath $new -Recurse -Force -ErrorAction SilentlyContinue
        $script:why = "could not unpack $zip into $new ($($_.Exception.Message))"
        return $null
    }
    if (-not (Test-DePython (Join-Path $new 'python.exe') $pin.version)) {
        Remove-Item -LiteralPath $new -Recurse -Force -ErrorAction SilentlyContinue
        $script:why = "the unpacked Python $($pin.version) does not start (an antivirus may have blocked python.exe: allow it and run this again)"
        return $null
    }
    try {
        if (Test-Path -LiteralPath $pyHome) { Remove-Item -LiteralPath $pyHome -Recurse -Force }
        Move-Item -LiteralPath $new -Destination $pyHome
    } catch {
        Remove-Item -LiteralPath $new -Recurse -Force -ErrorAction SilentlyContinue
        $script:why = "could not replace $pyHome ($($_.Exception.Message)): close Play.cmd / the launcher and run this again"
        return $null
    }
    $m = '{"file": "' + $pin.file + '", "sha256": "' + $pin.sha256 + '", "version": "' + $pin.version + '"}'
    Set-Content -LiteralPath (Join-Path $pyHome $markerName) -Value $m -Encoding ASCII
    return (Join-Path $pyHome 'python.exe')
}

function Get-DePython {
    # @(exe, args...): the pinned interpreter (fetched now if needed), else a Python 3.10+ on the PC, else $null
    $pin = Get-Pin
    if ($null -ne $pin -and $pin.sha256) {
        $exe = Join-Path $pyHome 'python.exe'
        $marker = Join-Path $pyHome $markerName
        $m = $null
        if (Test-Path -LiteralPath $marker) { try { $m = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json } catch { $m = $null } }
        if ($null -ne $m -and $m.sha256 -eq $pin.sha256 -and (Test-DePython $exe $pin.version)) {
            Write-Host "PASS python     $exe (Python $($pin.version), pinned)"
            return , @($exe)
        }
        $got = Install-DePython $pin
        if ($null -ne $got) {
            Write-Host "PASS python     $got (Python $($pin.version) from python.org, SHA-256 verified)"
            return , @($got)
        }
        Write-Host "WARN python     $($script:why)"
    } elseif ($null -ne $pin) {
        $script:why = 'this release names no interpreter SHA-256 yet'
        Write-Host "INFO python     $($script:why): looking for a Python already on this PC"
    } else {
        $script:why = 'release.json has no interpreter pin'
    }
    if ($env:DEEPENG_TEST_NO_SYSTEM_PYTHON -ne '1') {
        $sys = Find-Python
        if ($null -ne $sys) {
            $rest = @($sys | Select-Object -Skip 1)
            $ver = & $sys[0] @rest -c "import sys; print(sys.version.split()[0])"
            Write-Host "WARN python     using the Python already on this PC: $($sys -join ' ') (Python $ver)"
            return , $sys
        }
    }
    return $null
}

$what = if ($Uninstall) { 'uninstall' } elseif ($Check) { 'check' } elseif ($BootstrapOnly) { 'interpreter' } else { 'install' }
Write-Host "DeepEngineers $what - $root"

$script = Join-Path $root 'launcher\de_install.py'
if (-not (Test-Path -LiteralPath $script) -or -not (Test-Path -LiteralPath (Join-Path $root 'release.json'))) {
    Write-Host "FAIL release    this is not a whole DeepEngineers folder (no launcher\de_install.py or release.json next to $($MyInvocation.MyCommand.Name))."
    Write-Host '                Extract the whole DeepEngineers zip first (right-click it > Extract All), then double-click'
    Write-Host '                Install.cmd in the extracted folder. Running it from inside the zip does not work.'
    Stop-Here 1
}

$py = Get-DePython
if ($null -eq $py) {
    $pin = Get-Pin
    Write-Host "FAIL python     no Python to run the installer: $($script:why)."
    if ($null -ne $pin -and $pin.sha256) {
        Write-Host "                Connect to the internet and run this again (it downloads Python $($pin.version) from python.org,"
        Write-Host "                $($pin.size) bytes, into $pyHome), or put $($pin.file) from"
        Write-Host "                $($pin.page) in a folder and run Install.cmd -OfflineDir `"that folder`","
        Write-Host '                or install Python 3.10 or newer from https://www.python.org/downloads/.'
    } else {
        Write-Host '                Install Python 3.10 or newer from https://www.python.org/downloads/ (tick "Add python.exe to PATH"),'
        Write-Host '                then run this again.'
    }
    Stop-Here 1
}
$pyExe = $py[0]
$pyArgs = @()
if ($py.Count -gt 1) { $pyArgs = @($py[1..($py.Count - 1)]) }
if ($BootstrapOnly) { Stop-Here 0 }

# Explorer's "Extract all" copies the internet mark onto every file;.NET refuses to load marked DLLs
try {
    Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction Stop | Unblock-File -ErrorAction SilentlyContinue
} catch {
    Write-Host "WARN unblock    could not clear the download mark on $root ($($_.Exception.Message))"
}

$manifest = Join-Path $local 'install\install-manifest.json'

$a = @('-I', '-B', $script)
if ($Uninstall) {
    $a += 'uninstall'
    if ($RemoveWorlds) { $a += '--remove-worlds' }
    if ($ForceWorldMod) { $a += '--force-worldmod' }
    if ($RemoveBackups) { $a += '--remove-backups' }
    if ($KeepData) { $a += '--keep-data' }
    if ($DryRun) { $a += '--dry-run' }
} else {
    if ($Check) { $a += 'check' } else { $a += 'install' }
    if (-not $Check -and $WorldName -eq '' -and -not $NoPrompt -and -not (Test-Path -LiteralPath $manifest)) {
        $WorldName = Read-Host 'Name of your DeepEngineers world in Space Engineers [DeepEngineers]'
        if ([string]::IsNullOrWhiteSpace($WorldName)) { $WorldName = 'DeepEngineers' }
    }
    if ($WorldName -ne '') { $a += @('--world-name', $WorldName) }
    if ($PulsarDir -ne '') { $a += @('--pulsar-dir', $PulsarDir) }
    foreach ($d in ($OfflineDir -split ';')) { if ($d.Trim() -ne '') { $a += @('--offline-dir', $d.Trim()) } }
    if ($SteamId -ne '') { $a += @('--steam-id', $SteamId) }
    if ($SteamRoot -ne '') { $a += @('--steam-root', $SteamRoot) }
    if ($NoPreflight) { $a += '--no-preflight' }
    if ($DryRun) { $a += '--dry-run' }
    if (-not $Check -and -not $DryRun) {
        if (-not $NoWait) { $a += '--wait' }
        if (-not $NoShortcuts) { $a += '--shortcuts' }
    }
}

& $pyExe @pyArgs @a
$rc = $LASTEXITCODE

if ($Uninstall -and $rc -eq 0 -and -not $KeepData -and -not $DryRun -and -not (Test-Path -LiteralPath $manifest)) {
    # the interpreter ran the uninstall above, so it can only be removed now that it has exited
    if (Test-Path -LiteralPath $pyHome) {
        try {
            Remove-Item -LiteralPath $pyHome -Recurse -Force
            Write-Host "PASS python     removed $pyHome"
        } catch {
            Write-Host "WARN python     could not remove $pyHome ($($_.Exception.Message)): delete it by hand"
        }
    }
    if ((Test-Path -LiteralPath $local) -and -not (Get-ChildItem -LiteralPath $local -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $local -Force -ErrorAction SilentlyContinue
    }
}
Stop-Here $rc
