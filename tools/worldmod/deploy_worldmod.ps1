<
.SYNOPSIS
  Deploys the DeepEngineers.World mod folder (DE_4546B, site v1) to %APPDATA%\SpaceEngineers\Mods\DeepEngineers.World\.

.DESCRIPTION
  Orchestrator or owner only (ROADMAP section 3 rule 3; S1 owner action "approve creating
  %APPDATA%\SpaceEngineers\Mods\DeepEngineers.World\", a write outside the repository).

  1. Refuses while Space Engineers runs (SpaceEngineers.exe, or Pulsar's Legacy.exe): SE reads mod definitions at
     world load, and a half-copied folder must never be loaded. -Force skips this guard.
  2. Runs  python -I tools/worldmod/check_planet.py  on the source folder; any FAIL aborts (-SkipCheck skips it).
  3. Copies every file of the source folder to the destination (creating it), overwriting older copies.
  4. Verifies every copied file by SHA-256 against the source.
  5. Lists files in the destination that the source does not have. They are left in place unless -Prune is given,
     which deletes only those extra files inside the destination folder.
  Writes nothing else: no world save, no game or Water Mod file. The Water Mod reads our Data\WaterConfig.xml only
  when a DE_4546B planet is spawned (docs/worldmod/DE_LAB_WORLD.md).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\worldmod\deploy_worldmod.ps1 -WhatIf
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\worldmod\deploy_worldmod.ps1
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    # Defaults resolved below: $PSScriptRoot is empty inside param defaults under Windows PowerShell 5.1 when the
 # script is started with -File.
    [string]$Source = '',
    [string]$Destination = (Join-Path $env:APPDATA 'SpaceEngineers\Mods\DeepEngineers.World'),
    [string]$Python = 'python',
    [switch]$SkipCheck,
    [switch]$Prune,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrEmpty($Source)) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $Source = Join-Path $scriptDir '..\..\src\DeepEngineers.WorldMod'
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$Source = (Resolve-Path -LiteralPath $Source).Path
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
Write-Host "DE.WorldMod deploy: source      $Source"
Write-Host "DE.WorldMod deploy: destination $Destination"

if ([string]::IsNullOrWhiteSpace($env:APPDATA) -and -not $PSBoundParameters.ContainsKey('Destination')) {
    throw 'APPDATA is not set; pass -Destination explicitly.'
}
if ((Split-Path -Leaf $Destination) -ne 'DeepEngineers.World') {
    throw "Destination folder must be named DeepEngineers.World (the local mod name the worlds list), got '$Destination'."
}

# 1. never while SE runs
$running = @(Get-Process -Name 'SpaceEngineers', 'Legacy' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0 -and -not $Force) {
    $list = ($running | ForEach-Object { '{0} (pid {1})' -f $_.ProcessName, $_.Id }) -join ', '
    throw "Space Engineers is running: $list. Close it first (or pass -Force)."
}

# 2. checker
if (-not $SkipCheck) {
    $checker = Join-Path $repoRoot 'tools\worldmod\check_planet.py'
    Write-Host "DE.WorldMod deploy: $Python -I $checker --mod `"$Source`""
    & $Python -I $checker --mod $Source
    if ($LASTEXITCODE -ne 0) {
        throw "check_planet.py failed (exit $LASTEXITCODE): nothing deployed."
    }
}

# 3. copy
$sourceFiles = @(Get-ChildItem -LiteralPath $Source -Recurse -File)
if ($sourceFiles.Count -eq 0) { throw "Source folder is empty: $Source" }
$copied = 0
foreach ($file in $sourceFiles) {
    $rel = $file.FullName.Substring($Source.Length).TrimStart('\', '/')
    $target = Join-Path $Destination $rel
    $targetDir = Split-Path -Parent $target
    if ($PSCmdlet.ShouldProcess($target, "copy $rel")) {
        if (-not (Test-Path -LiteralPath $targetDir)) {
            New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        }
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        $copied++
    }
}

# 4. verify
$bad = 0
if (-not $WhatIfPreference) {
    foreach ($file in $sourceFiles) {
        $rel = $file.FullName.Substring($Source.Length).TrimStart('\', '/')
        $target = Join-Path $Destination $rel
        $want = Get-Sha256 $file.FullName
        $have = if (Test-Path -LiteralPath $target) { Get-Sha256 $target } else { '(missing)' }
        $status = if ($want -eq $have) { 'OK ' } else { $bad++; 'BAD' }
        Write-Host ('  {0} {1,-48} {2}' -f $status, ($rel -replace '\\', '/'), $want.Substring(0, 16))
    }
}

# 5. extra files in the destination
$extra = @()
if (Test-Path -LiteralPath $Destination) {
    $wanted = @{}
    foreach ($file in $sourceFiles) {
        $wanted[$file.FullName.Substring($Source.Length).TrimStart('\', '/').ToLowerInvariant()] = $true
    }
    $destRoot = (Resolve-Path -LiteralPath $Destination).Path
    foreach ($file in @(Get-ChildItem -LiteralPath $destRoot -Recurse -File)) {
        $rel = $file.FullName.Substring($destRoot.Length).TrimStart('\', '/')
        if (-not $wanted.ContainsKey($rel.ToLowerInvariant())) { $extra += $file }
    }
}
foreach ($file in $extra) {
    if ($Prune) {
        if ($PSCmdlet.ShouldProcess($file.FullName, 'remove extra file')) {
            Remove-Item -LiteralPath $file.FullName -Force
            Write-Host "  removed extra file $($file.FullName)"
        }
    } else {
        Write-Warning "extra file in the deployed mod (not in the source; use -Prune to remove): $($file.FullName)"
    }
}

if ($bad -gt 0) { throw "$bad file(s) differ after the copy." }
Write-Host ("DE.WorldMod deploy: {0} file(s) copied, {1} extra, verify {2}" -f $copied, $extra.Count,
    $(if ($WhatIfPreference) { 'skipped (WhatIf)' } else { 'PASS' }))
