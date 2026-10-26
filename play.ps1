# DeepEngineers free play without the orchestrator: backups, preflight, both games.
#.\play.ps1 Subnautica first (hidden renderer), then Space Engineers at the main menu
#.\play.ps1 -Continue the same, SE loads the last world directly (Pulsar's -continue)
# Needs Steam running and both games closed. The launcher's preflight refuses to start when the three backups are older
# than the last DE session; this script refreshes them first (SE appdata, Subnautica LocalLow; the saves one by the
# launcher itself). Logs: %LOCALAPPDATA%\DeepEngineers\logs. Stop: close SE normally; Subnautica follows (CONTRACT 7).
param([switch]$Continue)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$um = "C:\Users\user\.claude\plugins\cache\universal-modder\universal-modder\2c2201437ca6"

if (-not (Get-Process steam -ErrorAction SilentlyContinue)) { Write-Host "Start Steam first, then run play.ps1 again."; exit 1 }
foreach ($n in @("SpaceEngineers", "Legacy", "Subnautica")) {
  if (Get-Process $n -ErrorAction SilentlyContinue) { Write-Host "$n is still running: close it first (the plugins need a clean start)."; exit 1 }
}

$sets = @(
  @{ src = "C:/Users/user/AppData/Roaming/SpaceEngineers"; name = "se-appdata" },
  @{ src = "C:/Users/user/AppData/LocalLow/Unknown Worlds/Subnautica"; name = "subnautica-locallow" }
)
# S9-installer: this script is the dev twin of the release's Play.cmd (tools/release/root/play.ps1). When the
# universal-modder CLI is not at $um (a plugin update moves its cache folder), the launcher's own zip step makes the
# same two sets in the same folder (launcher.py backup --scope full).
if (-not (Test-Path -LiteralPath $um)) {
  Write-Host "universal-modder CLI not found at ${um}: launcher.py backup makes the backups"
  & python -I (Join-Path $root "src\DeepEngineers.Launcher\launcher.py") backup --sets "se-appdata,subnautica-locallow" --scope full
  if ($LASTEXITCODE -ne 0) { Write-Host ("backup failed (exit " + $LASTEXITCODE + ")"); exit $LASTEXITCODE }
  $sets = @()
}
foreach ($s in $sets) {
  Write-Host ("backup " + $s.name + " ...")
  & uv run --project $um um backup create $s.src --name $s.name
  if ($LASTEXITCODE -ne 0) { Write-Host ("backup " + $s.name + " failed (exit " + $LASTEXITCODE + ")"); exit $LASTEXITCODE }
}

$launcherArgs = @("-I", (Join-Path $root "src\DeepEngineers.Launcher\launcher.py"), "run", "--refresh-saves-backup")
if ($Continue) { $launcherArgs += "--continue" }
Write-Host "launcher: python $($launcherArgs -join ' ')"
& python @launcherArgs
exit $LASTEXITCODE
