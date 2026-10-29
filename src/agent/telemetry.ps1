# Pure conversion functions; neither starts processes nor changes GPU settings.
function ConvertTo-PearlNumber($Value) {
    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) { return $null }
    $number = 0.0
    if ([double]::TryParse([string]$Value, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -and
        -not [double]::IsNaN($number) -and -not [double]::IsInfinity($number) -and $number -ge 0) { return $number }
    return $null
}

function Convert-PearlTelemetry($ApiResponse, $NvidiaLines, [double]$NowUnix = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) {
    $updated = ConvertTo-PearlNumber $ApiResponse.last_update
    $fresh = $null -ne $updated -and [Math]::Abs($NowUnix - $updated) -le 20
    $algo = $null
    $devices = @()
    if ($fresh) {
        $algo = @($ApiResponse.algos | Where-Object { $_.name -eq 'pearlhash' } | Select-Object -First 1)
        $algo = if ($algo.Count) { $algo[0] } else { $null }
        $devices = @($ApiResponse.devices)
    }
    $poolConnected = $null
    if ($null -ne $algo.pool_connected -and $algo.pool_connected -is [bool]) { $poolConnected = $algo.pool_connected }
    elseif ($algo.pool_status) {
        switch -Regex (([string]$algo.pool_status).Trim()) {
            '^(Connected|Online|Active)$' { $poolConnected = $true }
            '^(Disconnected|Offline|Inactive|Connecting|Reconnecting)$' { $poolConnected = $false }
        }
    }
    $nv = @{}
    foreach ($line in $NvidiaLines) {
        $parts = $line -split ','
        if ($parts.Count -ne 8) { continue }
        $index = ConvertTo-PearlNumber $parts[0].Trim()
        if ($null -eq $index -or $index -notin @(0,1)) { continue }
        $bus = $null
        if ($parts[1].Trim() -match '^[0-9A-Fa-f]+:([0-9A-Fa-f]+):[0-9A-Fa-f]+\.[0-9]+$') {
            $bus = [Convert]::ToInt32($Matches[1], 16)
        }
        $nv[[int]$index] = @{
            bus = $bus
            tempC = ConvertTo-PearlNumber $parts[2].Trim()
            powerWatts = ConvertTo-PearlNumber $parts[3].Trim()
            fanPercent = ConvertTo-PearlNumber $parts[4].Trim()
            cclkMHz = ConvertTo-PearlNumber $parts[5].Trim()
            mclkMHz = ConvertTo-PearlNumber $parts[6].Trim()
            vramTempC = ConvertTo-PearlNumber $parts[7].Trim()
        }
    }
    $gpus = foreach ($id in @(0,1)) {
        $position = -1
        for ($i=0; $i -lt $devices.Count; $i++) {
            $matchesDevice = if ($nv.ContainsKey($id) -and $null -ne $nv[$id].bus) {
                $null -ne $devices[$i].bus_id -and [int]$devices[$i].bus_id -eq $nv[$id].bus
            } else { $devices[$i].id -eq $id }
            if ($matchesDevice) { $position = $i; break }
        }
        $device = if ($position -ge 0) { $devices[$position] } else { $null }
        $rate = $null; $accepted = $null; $rejected = $null
        if ($null -ne $algo -and $position -ge 0) {
            if ($null -ne $algo.hashrates -and $position -lt @($algo.hashrates).Count) { $rate = ConvertTo-PearlNumber $algo.hashrates[$position] }
            if ($null -ne $algo.devices_accepted_shares -and $position -lt @($algo.devices_accepted_shares).Count) { $accepted = ConvertTo-PearlNumber $algo.devices_accepted_shares[$position] }
            if ($null -ne $algo.devices_rejected_shares -and $position -lt @($algo.devices_rejected_shares).Count) { $rejected = ConvertTo-PearlNumber $algo.devices_rejected_shares[$position] }
        }
        $sensors = if ($nv.ContainsKey($id)) { $nv[$id] } else { @{
            tempC = ConvertTo-PearlNumber $device.temp
            powerWatts = ConvertTo-PearlNumber $device.power
            fanPercent = ConvertTo-PearlNumber $device.fan
            cclkMHz = ConvertTo-PearlNumber $device.cclk
            mclkMHz = ConvertTo-PearlNumber $device.mclk
            vramTempC = ConvertTo-PearlNumber $device.mem_temp
        } }
        @{
            id = $id; name = if ($device.name) { $device.name } else { "GPU $id" }
            hashrateTHs = if ($null -ne $rate) { $rate / 1e12 } else { $null }
            acceptedShares = $accepted; rejectedShares = $rejected
            tempC = $sensors.tempC; powerWatts = $sensors.powerWatts; fanPercent = $sensors.fanPercent
            cclkMHz = $sensors.cclkMHz; mclkMHz = $sensors.mclkMHz; vramTempC = $sensors.vramTempC
            sensorSource = if ($nv.ContainsKey($id)) { 'nvidia-smi' } elseif ($device) { 'miner-api' } else { $null }
        }
    }
    $totalRate = ConvertTo-PearlNumber $algo.total_hashrate
    return @{
        ok = $true; schemaVersion = 4; sampledAt = [DateTimeOffset]::FromUnixTimeSeconds([long]$NowUnix).ToString('o')
        apiAvailable = $fresh -and $null -ne $algo; nvmlAvailable = $nv.Count -gt 0
        poolConnected = $poolConnected; poolUrl = $algo.pool
        uptimeSec = if ($fresh) { ConvertTo-PearlNumber $ApiResponse.uptime_seconds } else { $null }
        totalHashrateTHs = if ($null -ne $totalRate) { $totalRate / 1e12 } else { $null }
        acceptedShares = ConvertTo-PearlNumber $algo.total_accepted_shares
        rejectedShares = ConvertTo-PearlNumber $algo.total_rejected_shares
        invalidShares = ConvertTo-PearlNumber $algo.total_invalid_shares
        gpus = @($gpus)
    }
}
