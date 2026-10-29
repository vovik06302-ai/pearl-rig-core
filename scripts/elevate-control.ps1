param([Parameter(Mandatory=$true)][string]$RequestPath,[Parameter(Mandatory=$true)][string]$ResultPath,[Parameter(Mandatory=$true)][string]$ConfigPath)
$ErrorActionPreference = 'Stop'
try {
    $worker = Join-Path $PSScriptRoot 'invoke-control.ps1'
    $argsList = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$worker+'"'),'-RequestPath',('"'+$RequestPath+'"'),'-ResultPath',('"'+$ResultPath+'"'),'-ConfigPath',('"'+$ConfigPath+'"'))
    $child = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList $argsList -Verb RunAs -WindowStyle Hidden -Wait -PassThru
    if (-not (Test-Path -LiteralPath $ResultPath)) { throw "Процесс управления завершился без результата (код $($child.ExitCode))" }
} catch {
    @{status='failed'; message=$_.Exception.Message} | ConvertTo-Json | Set-Content -LiteralPath $ResultPath -Encoding UTF8
    exit 1
}
