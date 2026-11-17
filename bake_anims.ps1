# Retargets the zombie animation pack onto the game's skeleton and writes data\zombie_anims.json (ZombieAnimBake).
# Requires an activated Unity licence (Unity Hub signed in). Doesn't touch zombies.bundle.
$ErrorActionPreference = 'Stop'

$unity   = 'E:\Unity\2022.3.43f1\Editor\Unity.exe'
$here    = $PSScriptRoot
$project = Join-Path $here 'UnityProject'
$logDir  = Join-Path $here 'logs'
New-Item -ItemType Directory -Force $logDir | Out-Null

Write-Host 'Syncing UnityAssets -> project Assets...'
Copy-Item (Join-Path $here 'UnityAssets\Zombies') (Join-Path $project 'Assets') -Recurse -Force

$log = Join-Path $logDir 'bake_anims.log'
$p = Start-Process -FilePath $unity -ArgumentList @('-batchmode', '-quit', '-projectPath', "`"$project`"", '-executeMethod', 'ZombieAnimBake.BakeBatch', '-logFile', "`"$log`"") -Wait -PassThru -NoNewWindow
Select-String -Path $log -Pattern '\[Zombies\]|error CS|Exception' | ForEach-Object { $_.Line }
if ($p.ExitCode -ne 0) { throw "Unity exited with $($p.ExitCode). Full log: $log" }
Write-Host 'Done.'
