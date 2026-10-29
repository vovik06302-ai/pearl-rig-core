$ErrorActionPreference='Stop'
$appDir=Split-Path $PSScriptRoot -Parent
$stateDir=Join-Path $env:LOCALAPPDATA 'PearlRigControl'
New-Item -ItemType Directory -Path $stateDir -Force | Out-Null
$env:PEARL_STATE_DIR=$stateDir
$node=Join-Path $appDir 'node.exe'
if(-not (Test-Path -LiteralPath $node)) { $node=(Get-Command node.exe -ErrorAction Stop).Source }
$listening=Get-NetTCPConnection -State Listen -LocalPort 3000 -ErrorAction SilentlyContinue
if(-not $listening) {
    $server=Join-Path $PSScriptRoot 'serve-panel.mjs'
    Start-Process -FilePath $node -ArgumentList ('"'+$server+'"') -WindowStyle Hidden -RedirectStandardOutput (Join-Path $stateDir 'panel.log') -RedirectStandardError (Join-Path $stateDir 'panel-error.log')
}
$ready=$false
for($i=0;$i -lt 20;$i++) {
    try { $state=Invoke-RestMethod -Uri 'http://127.0.0.1:3000/api/session' -TimeoutSec 2; if($null -ne $state.configured){$ready=$true;break} } catch {}
    Start-Sleep -Milliseconds 300
}
if(-not $ready){throw 'Panel did not start. Check the local panel-error.log.'}
Start-Process 'http://127.0.0.1:3000/'
