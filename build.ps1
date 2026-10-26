param(
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Release',
    [switch]$NoDeploy,
    [string]$PulsarDir = (Join-Path $PSScriptRoot 'third_party\pulsar\extracted'),
    [string]$SubnauticaDir = 'C:\Program Files (x86)\Steam\steamapps\common\Subnautica',
    # S9: dev or release (DEEPENG_RELEASE: no mutants, no harness, no
 # dev/lab commands, only the user tuning commands; built into src\<plugin>\bin\ReleaseFlavor\<Configuration>\,
 # never deployed by this script: the release folder is staged from those outputs by the release tooling)
    [ValidateSet('dev', 'release')] [string]$Flavor = 'dev'
)
# Builds the three projects and deploys the two plugins to their loaders:
# SE -> <Pulsar>\Legacy\Local\DeepEngineers.SE\plugin.dll (+ DeepEngineers.Wire.dll); Pulsar scans Local\<dir>\plugin.dll
# and the profile enables it by id "DeepEngineers.SE.dll" (Legacy\Profiles\Current.xml <Local><string>).
# SN -> <Subnautica>\BepInEx\plugins\DeepEngineers\ (DeepEngineers.SN.dll + DeepEngineers.Wire.dll).
# Never run while a game is running: the loaders hold the DLLs open.
#.\build.ps1 dev flavour, build + deploy (unchanged)
#.\build.ps1 -Flavor release release flavour, build only; prints the four release DLLs and their SHA-256
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if ($Flavor -eq 'release') {
    # S9-release-flavor: separate outputs (bin\ReleaseFlavor, obj\ReleaseFlavor), so the dev build and deploy are untouched
    dotnet build DeepEngineers.sln -c $Configuration -nologo -v q -p:DeepEngFlavor=release
    if ($LASTEXITCODE -ne 0) { throw "release-flavour build failed" }
    $outs = @(
        (Join-Path $PSScriptRoot "src\DeepEngineers.SE\bin\ReleaseFlavor\$Configuration\net48\plugin.dll"),
        (Join-Path $PSScriptRoot "src\DeepEngineers.SE\bin\ReleaseFlavor\$Configuration\net48\DeepEngineers.Wire.dll"),
        (Join-Path $PSScriptRoot "src\DeepEngineers.SN\bin\ReleaseFlavor\$Configuration\net472\DeepEngineers.SN.dll"),
        (Join-Path $PSScriptRoot "src\DeepEngineers.SN\bin\ReleaseFlavor\$Configuration\net472\DeepEngineers.Wire.dll")
    )
    foreach ($o in $outs) {
        if (-not (Test-Path $o)) { throw "release output missing: $o" }
        Write-Host ("release  " + (Get-FileHash -Algorithm SHA256 $o).Hash.ToLowerInvariant() + "  " + $o)
    }
    Write-Host "release flavour built (not deployed; the dev deploy in the game folders is unchanged)"
    return
}

foreach ($p in @('Legacy', 'SpaceEngineers', 'Subnautica')) {
    if (Get-Process $p -ErrorAction SilentlyContinue) { throw "$p is running; close it before building/deploying." }
}

dotnet build DeepEngineers.sln -c $Configuration -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }

if ($NoDeploy) { return }

# --- Space Engineers (Pulsar Local plugin) ---
$seOut = Join-Path $PSScriptRoot "src\DeepEngineers.SE\bin\$Configuration\net48"
$seDst = Join-Path $PulsarDir 'Legacy\Local\DeepEngineers.SE'
New-Item -ItemType Directory -Force $seDst | Out-Null
Copy-Item (Join-Path $seOut 'plugin.dll') $seDst -Force
Copy-Item (Join-Path $seOut 'DeepEngineers.Wire.dll') $seDst -Force
if (Test-Path (Join-Path $seOut 'plugin.pdb')) { Copy-Item (Join-Path $seOut 'plugin.pdb') $seDst -Force }

$profile = Join-Path $PulsarDir 'Legacy\Profiles\Current.xml'
if (Test-Path $profile) {
    [xml]$xml = Get-Content $profile
    $local = $xml.Profile.SelectSingleNode('Local')
    $has = $false
    foreach ($n in $local.ChildNodes) { if ($n.InnerText -eq 'DeepEngineers.SE.dll') { $has = $true } }
    if (-not $has) {
        $e = $xml.CreateElement('string'); $e.InnerText = 'DeepEngineers.SE.dll'; [void]$local.AppendChild($e)
        $xml.Save($profile)
        Write-Host "Pulsar profile: enabled DeepEngineers.SE.dll"
    }
} else {
    Write-Warning "Pulsar profile not found ($profile): run Legacy.exe once, then re-run build.ps1"
}
Write-Host "SE plugin deployed -> $seDst"

# --- Subnautica (BepInEx plugin) ---
$snOut = Join-Path $PSScriptRoot "src\DeepEngineers.SN\bin\$Configuration\net472"
$snDst = Join-Path $SubnauticaDir 'BepInEx\plugins\DeepEngineers'
New-Item -ItemType Directory -Force $snDst | Out-Null
Copy-Item (Join-Path $snOut 'DeepEngineers.SN.dll') $snDst -Force
Copy-Item (Join-Path $snOut 'DeepEngineers.Wire.dll') $snDst -Force
if (Test-Path (Join-Path $snOut 'DeepEngineers.SN.pdb')) { Copy-Item (Join-Path $snOut 'DeepEngineers.SN.pdb') $snDst -Force }
Write-Host "SN plugin deployed -> $snDst"
