$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\src\agent\telemetry.ps1"
function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -ne $Expected -or ($null -eq $Expected -and $null -ne $Actual)) {
        throw "$Label expected '$Expected', got '$Actual'"
    }
}
$api = Get-Content -LiteralPath "$PSScriptRoot\fixtures\telemetry.json" -Raw | ConvertFrom-Json
$now = $api.last_update
$lines = @('0, 00000000:01:00.0, 74, 149.67, 65, 2325, 810, N/A', '1, 00000000:05:00.0, 66, 150.05, 68, 2310, 810, N/A')
$r = Convert-PearlTelemetry $api $lines $now
Assert-Equal $r.totalHashrateTHs 275.7621422328 'TH/s'
Assert-Equal $r.gpus[0].acceptedShares 29 'GPU 0 shares'
Assert-Equal $r.gpus[1].acceptedShares 28 'GPU 1 shares'
Assert-Equal $r.gpus[1].rejectedShares 0 'GPU 1 rejected'
Assert-Equal $r.gpus[0].powerWatts 149.67 'Driver power'
Assert-Equal $r.gpus[1].mclkMHz 810 'Actual memory clock'
Assert-Equal $r.gpus[0].vramTempC $null 'Unsupported memory sensor'
Assert-Equal $r.gpus[0].sensorSource 'nvidia-smi' 'Driver source'
$api.algos[0].pool_status = 'Disconnected'
Assert-Equal (Convert-PearlTelemetry $api $lines $now).poolConnected $false 'Disconnected'
$api.algos[0].pool_status = 'SomethingUnknown'
Assert-Equal (Convert-PearlTelemetry $api $lines $now).poolConnected $null 'Unknown connection'
$r = Convert-PearlTelemetry $api @() $now
Assert-Equal $r.gpus[0].sensorSource 'miner-api' 'API fallback'
Assert-Equal $r.gpus[0].tempC 74 'API temperature'
$r = Convert-PearlTelemetry $api $lines ($now+21)
Assert-Equal $r.apiAvailable $false 'Stale API'
Assert-Equal $r.gpus[0].hashrateTHs $null 'Stale hashrate'
Assert-Equal $r.gpus[0].tempC 74 'Fresh driver reading despite stale API'
$r = Convert-PearlTelemetry $null @() $now
Assert-Equal $r.gpus[0].tempC $null 'Missing API and driver'
Assert-Equal $r.gpus[0].powerWatts $null 'Missing power'
Assert-Equal $r.totalHashrateTHs $null 'Missing hashrate'
$lines[0] = '0, 00000000:01:00.0, N/A, N/A, 0, 2325, 810, N/A'
$r = Convert-PearlTelemetry $api $lines $now
Assert-Equal $r.gpus[0].tempC $null 'N/A is not zero'
Assert-Equal $r.gpus[0].fanPercent 0 'Zero fan is valid'
# PCI identity wins over array position when IDs differ from NVIDIA indexing.
$api.devices = @($api.devices[1], $api.devices[0])
$api.algos[0].hashrates = @($api.algos[0].hashrates[1], $api.algos[0].hashrates[0])
$api.algos[0].devices_accepted_shares = @(28,29)
$r = Convert-PearlTelemetry $api $lines $now
Assert-Equal $r.gpus[0].acceptedShares 29 'PCI mapping'
[Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('ru-RU')
$r = Convert-PearlTelemetry $api $lines $now
Assert-Equal $r.gpus[1].powerWatts 150.05 'Invariant decimal parsing'
$api.algos[0].PSObject.Properties.Remove('devices_accepted_shares')
$r = Convert-PearlTelemetry $api $lines $now
Assert-Equal $r.gpus[0].acceptedShares $null 'Missing per-device counter'
Write-Output 'PowerShell telemetry regression checks passed.'
