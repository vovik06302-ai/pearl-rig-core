@echo off
chcp 65001 >nul
title Pearl Rig Control [dual4070ti]

set "SELF_BAT=%~f0"
set "PS_AGENT=%TEMP%\pearl_rig_gui_supervisor.ps1"

powershell -NoProfile -ExecutionPolicy Bypass -Command "$raw = [System.IO.File]::ReadAllText($env:SELF_BAT, [System.Text.Encoding]::UTF8); $marker = ':: ___PS_' + 'START___'; $idx = $raw.IndexOf($marker); if ($idx -ge 0) { $psCode = $raw.Substring($idx + $marker.Length).TrimStart(); $utf8Bom = New-Object System.Text.UTF8Encoding($true); [System.IO.File]::WriteAllText($env:PS_AGENT, $psCode, $utf8Bom) }"

start "" powershell -NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File "%PS_AGENT%" %*
exit /b

:: ___PS_START___
param([switch]$ReadOnly, [switch]$Startup, [switch]$AutoMine)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
$instanceCreated = $false
$instanceName = "Local\PearlRigControl-$([Environment]::UserName)"
$script:InstanceMutex = [System.Threading.Mutex]::new($true, $instanceName, [ref]$instanceCreated)
if (-not $instanceCreated) {
    try {
        $showExisting = [System.Threading.EventWaitHandle]::OpenExisting("Local\PearlRigControl-Show-$([Environment]::UserName)")
        $showExisting.Set() | Out-Null
        $showExisting.Dispose()
    } catch {}
    $script:InstanceMutex.Dispose()
    exit
}
$script:ShowPanelEvent = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::AutoReset, "Local\PearlRigControl-Show-$([Environment]::UserName)")
$ErrorActionPreference = "SilentlyContinue"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$InstalledAppDir = if ($env:SELF_BAT) { Split-Path -Parent $env:SELF_BAT } else { $PSScriptRoot }
$MinerDir = Join-Path $InstalledAppDir 'miner'
if (-not (Test-Path -LiteralPath (Join-Path $MinerDir 'onezerominer.exe'))) { $MinerDir = "$env:LOCALAPPDATA\PearlRigControl\miner" }
$LogFile = "$env:LOCALAPPDATA\PearlRigControl\pearl-miner.log"
$Worker = "dual4070ti"
$Pool1 = "stratum+tcp://prl-ru.kryptex.network:7048"
$Pool2 = "stratum+tcp://prl-eu.kryptex.network:7048"
$ApiPort = 3001
$BridgePort = 3002
$CheckInterval = 15
$MaxTemp = 85
$CriticalStopTemp = 88
$EmergencyPL = 125
$ZeroHashLimitSec = 90
$MaxRestartAttempts = 3

# Текущая версия агента и канал проверки обновлений (GitHub Releases)
$AppVersion = "3.3.10"
$UpdateRepo = "vovik06302-ai/bit"
$script:LatestVersion = $null
$script:LatestReleaseUrl = $null
$script:UpdateAvailable = $false
$script:UpdateCheckActive = $false
$script:UpdateCheckWork = $null
$script:DownloadActive = $false
$script:DownloadProgress = 0
$script:DownloadProgressState = $null
$script:UpdateDownloadWork = $null
$script:AutoStartTaskName = 'PearlRigControl-AutoStart'
$script:AutoStartMining = [bool]$AutoMine
$script:IsStartupTaskLaunch = [bool]$Startup
$script:TrayExitRequested = $false
$script:InstanceMutex = $null
$script:ShowPanelEvent = $null

# Профиль для двух RTX 4070 Ti (без повышения напряжения):
# PL 150 W, память 810 MHz, ядро 2400 MHz, kernel 2, кулер GPU0=65%, GPU1=* (авто)
$CurrentCClk = "2400,2400"
$CurrentMClk = "810,810"
$CurrentPL = "150,150"
$script:GpuPowerMin = 100
$script:GpuPowerMax = 285
$script:GpuPowerLimit = 150
$script:GpuPowerRangeVerified = $false
$script:NvidiaSmiPath = $null
$CurrentKernel = "2,2"
$CurrentFan = "65,*"

# Локальное хранилище публичного адреса кошелька (без GitHub и без обрезанного адреса)
$LocalStateDir = "$env:LOCALAPPDATA\PearlRigControl"
if (-not (Test-Path $LocalStateDir)) { New-Item -ItemType Directory -Path $LocalStateDir -Force | Out-Null }
$LocalWalletFile = "$LocalStateDir\wallet-local.json"
$LocalSettingsFile = "$LocalStateDir\rig-settings.json"
$script:ElectricityTariff = 0.0
$script:TotalGpuKWh = 0.0
$script:LastPowerSampleAt = $null
$script:LastPowerSampleWatts = $null
$script:LastEnergySaveAt = $null
$script:PoolConfirmed = $null
$script:PoolPending = $null
$script:PoolBalance = $null
$script:PayoutThreshold = $null
$script:PoolBalanceError = $null
$script:PoolBalanceFetchedAt = $null
$script:PrlUsd = $null
$script:UsdRub = $null
$script:PrlRub = $null
$script:MarketRatesFetchedAt = $null
if (Test-Path -LiteralPath $LocalSettingsFile) {
    try {
        $localSettings = Get-Content -LiteralPath $LocalSettingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($localSettings.electricityTariff -is [ValueType] -and [double]$localSettings.electricityTariff -ge 0) { $script:ElectricityTariff = [double]$localSettings.electricityTariff }
        if ($localSettings.totalGpuKWh -is [ValueType] -and [double]$localSettings.totalGpuKWh -ge 0) { $script:TotalGpuKWh = [double]$localSettings.totalGpuKWh }
        if ($localSettings.gpuPowerLimit -is [ValueType]) { $script:GpuPowerLimit = [int]$localSettings.gpuPowerLimit }
        if ($localSettings.autoStartMining -is [bool]) { $script:AutoStartMining = [bool]$localSettings.autoStartMining }
        if ($localSettings.minerDir -and (Test-Path -LiteralPath (Join-Path ([string]$localSettings.minerDir) 'onezerominer.exe'))) { $MinerDir = [string]$localSettings.minerDir }
    } catch {}
}
try {
    Import-Module ScheduledTasks -ErrorAction Stop
    $script:AutoStartMining = $null -ne (Get-ScheduledTask -TaskName $script:AutoStartTaskName -ErrorAction SilentlyContinue)
} catch { if ($Startup -or $AutoMine) { $script:AutoStartMining = $true } }
if ($AutoMine) { $script:AutoStartMining = $true }
$nvidiaCommand = Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $nvidiaCommand) {
    $nvidiaCandidate = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
    if (Test-Path -LiteralPath $nvidiaCandidate) { $script:NvidiaSmiPath = $nvidiaCandidate }
} else { $script:NvidiaSmiPath = $nvidiaCommand.Source }
if ($script:NvidiaSmiPath) {
    try {
        $powerBounds = @(& $script:NvidiaSmiPath --query-gpu=power.min_limit,power.max_limit --format=csv,noheader,nounits 2>$null)
        $mins = @(); $maxs = @()
        foreach ($boundLine in $powerBounds) {
            $parts = ([string]$boundLine).Split(',')
            if ($parts.Count -ge 2) {
                $minParsed = 0.0; $maxParsed = 0.0
                if ([double]::TryParse($parts[0].Trim(), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$minParsed) -and
                    [double]::TryParse($parts[1].Trim(), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$maxParsed)) {
                    $mins += [int][Math]::Ceiling($minParsed); $maxs += [int][Math]::Floor($maxParsed)
                }
            }
        }
        if ($mins.Count -ge 2 -and $maxs.Count -ge 2) {
            $script:GpuPowerMin = ($mins | Measure-Object -Maximum).Maximum
            $script:GpuPowerMax = ($maxs | Measure-Object -Minimum).Minimum
            $script:GpuPowerRangeVerified = ($script:GpuPowerMax -ge $script:GpuPowerMin)
        }
    } catch {}
}
$script:GpuPowerLimit = [Math]::Max($script:GpuPowerMin, [Math]::Min($script:GpuPowerMax, $script:GpuPowerLimit))
$CurrentPL = "$($script:GpuPowerLimit),$($script:GpuPowerLimit)"

# Адрес не вшивается в программу; он читается только из локального файла пользователя.
$Wallet = ""
$WalletSaved = $false
$WalletEditMode = $true

# Состояние процесса майнера: НЕ запускаем при установке или первом открытии
$MiningDesired = $false
$UserStoppedManually = $false
$IsMiningRunning = $false
$MinerPid = $null

# Счётчики восстановления связи с пулом
$DisconnectElapsedSec = 0
$RestartCount = 0
$ReconnectStage = "idle"
$SharesAtRestart = 0
$RecoveryConfirmed = $false
$ThermalThrottled = $false

# Реальные показатели телеметрии ($null = нет данных, никаких случайных чисел)
$ApiAvailable = $false
$script:ApiRetryAfter = [DateTime]::MinValue
$NvmlAvailable = $false
$PoolConnected = $null
$ActivePoolUrl = $null
$UptimeSec = $null
$Gpu0Hash = $null
$Gpu1Hash = $null
$TotalHash = $null
$SharesAcc = $null
$SharesRej = $null
$Gpu0Temp = $null
$Gpu1Temp = $null
$Gpu0Power = $null
$Gpu1Power = $null
$Gpu0Fan = $null
$Gpu1Fan = $null
$Gpu0CClkNow = $null
$Gpu1CClkNow = $null
$Gpu0MClkNow = $null
$Gpu1MClkNow = $null

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
        if ($parts.Count -ne 7) { continue }
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
            tempC = $sensors.tempC; vramTempC = ConvertTo-PearlNumber $device.mem_temp; powerWatts = $sensors.powerWatts; fanPercent = $sensors.fanPercent
            cclkMHz = $sensors.cclkMHz; mclkMHz = $sensors.mclkMHz
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

function Test-PearlWalletFormat([string]$Addr) {
    if ([string]::IsNullOrWhiteSpace($Addr)) {
        return @{ Valid = $false; Reason = "Адрес пуст. Введите публичный адрес кошелька Pearl (prl1p... 63 символа)." }
    }
    $clean = $Addr.Trim()
    if ($clean -match "\s" -or $clean -match "^[0-9a-fA-F]{64}$" -or $clean.StartsWith("0x") -or $clean.StartsWith("xprv")) {
        return @{ Valid = $false; Reason = "ОШИБКА: Не вводите seed-фразу или приватный ключ! Нужен только публичный адрес prl1p..." }
    }
    $lower = $clean.ToLowerInvariant()
    if ($lower.StartsWith("prl1p")) {
        $body = $lower.Substring(5)
        if ($lower.Length -eq 63 -and ($body -match "^[qpzry9x8gf2tvdw0s3jn54khce6mua7l]+$")) {
            return @{ Valid = $true; Reason = "Корректный публичный адрес Pearl (Bech32m, 63 символа)." }
        }
        return @{ Valid = $false; Reason = "Неверный формат prl1p...: длина $($lower.Length) из 63 символов или недопустимые символы." }
    }
    if ($lower.StartsWith("prl1q")) {
        $body = $lower.Substring(5)
        if ($lower.Length -ge 42 -and $lower.Length -le 44 -and ($body -match "^[qpzry9x8gf2tvdw0s3jn54khce6mua7l]+$")) {
            return @{ Valid = $true; Reason = "Корректный публичный адрес Pearl (Bech32, $($lower.Length) симв.)." }
        }
        return @{ Valid = $false; Reason = "Неверный формат prl1q...: длина $($lower.Length) (ожидается 42-44 символа)." }
    }
    return @{ Valid = $false; Reason = "Адрес должен начинаться с prl1p... (63 симв.) или prl1q... (42-44 симв.)." }
}

# Загрузка ранее сохранённого локального адреса
if (Test-Path $LocalWalletFile) {
    try {
        $savedObj = Get-Content -LiteralPath $LocalWalletFile -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($savedObj -and $savedObj.walletAddress) {
            $chk = Test-PearlWalletFormat $savedObj.walletAddress
            if ($chk.Valid) {
                $Wallet = $savedObj.walletAddress.Trim()
                $WalletSaved = $true
                $WalletEditMode = $false
            }
        }
    } catch {}
}

# ============================================================================
# ОКНО УПРАВЛЕНИЯ PEARL RIG CONTROL (БЕЗ АВТОЗАПУСКА И СИМУЛЯЦИИ)
# ============================================================================
$form = New-Object System.Windows.Forms.Form
$form.Text = "Pearl Rig Control v$AppVersion [$Worker] - Dual RTX 4070 Ti"
$form.Size = New-Object System.Drawing.Size(1200, 900)
$form.StartPosition = "CenterScreen"
$form.BackColor = [System.Drawing.Color]::FromArgb(11, 15, 23)
$form.ForeColor = [System.Drawing.Color]::FromArgb(241, 245, 249)
$form.FormBorderStyle = "Sizable"
$form.MaximizeBox = $true
$form.KeyPreview = $true
$script:IsFullscreen = $false
$script:SavedFormBounds = $null
$script:SavedFormBorderStyle = $null
$script:SavedWindowState = $null

$lblTitle = New-Object System.Windows.Forms.Label
$lblTitle.Text = "PEARL RIG CONTROL · DUAL RTX 4070 Ti (PL 150W | CClk 2400 | MClk 810 | Kernel 2)"
$lblTitle.Font = New-Object System.Drawing.Font("Segoe UI", 12.5, [System.Drawing.FontStyle]::Bold)
$lblTitle.ForeColor = [System.Drawing.Color]::FromArgb(248, 250, 252)
$lblTitle.Location = New-Object System.Drawing.Point(20, 14)
$lblTitle.Size = New-Object System.Drawing.Size(1060, 24)
$form.Controls.Add($lblTitle)

$lblStatus = New-Object System.Windows.Forms.Label
$lblStatus.Text = "Статус: Ожидание (майнер не запущен)"
$lblStatus.Font = New-Object System.Drawing.Font("Consolas", 9.5, [System.Drawing.FontStyle]::Bold)
$lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(251, 191, 36)
$lblStatus.Location = New-Object System.Drawing.Point(20, 42)
$lblStatus.Size = New-Object System.Drawing.Size(1060, 20)
$form.Controls.Add($lblStatus)

# БЛОК 1: Публичный адрес кошелька Pearl (обязателен ДО первого запуска)
$grpWallet = New-Object System.Windows.Forms.Panel
$grpWallet.Location = New-Object System.Drawing.Point(20, 68)
$grpWallet.Size = New-Object System.Drawing.Size(1065, 92)
$grpWallet.BackColor = [System.Drawing.Color]::FromArgb(17, 24, 39)
$grpWallet.BorderStyle = "FixedSingle"
$form.Controls.Add($grpWallet)

$lblWalletHeader = New-Object System.Windows.Forms.Label
$lblWalletHeader.Text = "ПУБЛИЧНЫЙ АДРЕС КОШЕЛЬКА PEARL (-w) · Никогда не вводите seed-фразу или приватный ключ!"
$lblWalletHeader.Font = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
$lblWalletHeader.ForeColor = [System.Drawing.Color]::FromArgb(52, 211, 153)
$lblWalletHeader.Location = New-Object System.Drawing.Point(12, 8)
$lblWalletHeader.Size = New-Object System.Drawing.Size(1030, 18)
$grpWallet.Controls.Add($lblWalletHeader)

$txtWallet = New-Object System.Windows.Forms.TextBox
$txtWallet.Text = $Wallet
$txtWallet.ReadOnly = (-not $WalletEditMode)
$txtWallet.BackColor = [System.Drawing.Color]::FromArgb(11, 15, 23)
$txtWallet.ForeColor = [System.Drawing.Color]::FromArgb(241, 245, 249)
$txtWallet.Font = New-Object System.Drawing.Font("Consolas", 10, [System.Drawing.FontStyle]::Bold)
$txtWallet.Location = New-Object System.Drawing.Point(12, 32)
$txtWallet.Size = New-Object System.Drawing.Size(680, 26)
$grpWallet.Controls.Add($txtWallet)

$btnCheckWallet = New-Object System.Windows.Forms.Button
$btnCheckWallet.Text = "Проверить формат"
$btnCheckWallet.Font = New-Object System.Drawing.Font("Segoe UI", 8.5, [System.Drawing.FontStyle]::Bold)
$btnCheckWallet.BackColor = [System.Drawing.Color]::FromArgb(30, 41, 59)
$btnCheckWallet.ForeColor = [System.Drawing.Color]::White
$btnCheckWallet.FlatStyle = "Flat"
$btnCheckWallet.Location = New-Object System.Drawing.Point(702, 30)
$btnCheckWallet.Size = New-Object System.Drawing.Size(128, 28)
$grpWallet.Controls.Add($btnCheckWallet)

$btnSaveWallet = New-Object System.Windows.Forms.Button
$btnSaveWallet.Text = "Сохранить локально"
$btnSaveWallet.Font = New-Object System.Drawing.Font("Segoe UI", 8.5, [System.Drawing.FontStyle]::Bold)
$btnSaveWallet.BackColor = [System.Drawing.Color]::FromArgb(16, 185, 129)
$btnSaveWallet.ForeColor = [System.Drawing.Color]::FromArgb(11, 15, 23)
$btnSaveWallet.FlatStyle = "Flat"
$btnSaveWallet.Location = New-Object System.Drawing.Point(838, 30)
$btnSaveWallet.Size = New-Object System.Drawing.Size(132, 28)
$grpWallet.Controls.Add($btnSaveWallet)

$btnEditWallet = New-Object System.Windows.Forms.Button
$btnEditWallet.Text = "Изменить"
$btnEditWallet.Font = New-Object System.Drawing.Font("Segoe UI", 8.5, [System.Drawing.FontStyle]::Bold)
$btnEditWallet.BackColor = [System.Drawing.Color]::FromArgb(51, 65, 85)
$btnEditWallet.ForeColor = [System.Drawing.Color]::White
$btnEditWallet.FlatStyle = "Flat"
$btnEditWallet.Location = New-Object System.Drawing.Point(976, 30)
$btnEditWallet.Size = New-Object System.Drawing.Size(78, 28)
$grpWallet.Controls.Add($btnEditWallet)

$lblWalletHint = New-Object System.Windows.Forms.Label
$lblWalletHint.Font = New-Object System.Drawing.Font("Consolas", 8.5)
$lblWalletHint.Location = New-Object System.Drawing.Point(12, 64)
$lblWalletHint.Size = New-Object System.Drawing.Size(1035, 20)
$grpWallet.Controls.Add($lblWalletHint)

# БЛОК 2: Кнопки управления майнером
$btnStart = New-Object System.Windows.Forms.Button
$btnStart.Text = "▶ ЗАПУСТИТЬ"
$btnStart.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
$btnStart.BackColor = [System.Drawing.Color]::FromArgb(16, 185, 129)
$btnStart.ForeColor = [System.Drawing.Color]::FromArgb(11, 15, 23)
$btnStart.FlatStyle = "Flat"
$btnStart.Location = New-Object System.Drawing.Point(20, 170)
$btnStart.Size = New-Object System.Drawing.Size(210, 38)
$form.Controls.Add($btnStart)

$btnStop = New-Object System.Windows.Forms.Button
$btnStop.Text = "■ СТОП"
$btnStop.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
$btnStop.BackColor = [System.Drawing.Color]::FromArgb(220, 38, 38)
$btnStop.ForeColor = [System.Drawing.Color]::White
$btnStop.FlatStyle = "Flat"
$btnStop.Location = New-Object System.Drawing.Point(240, 170)
$btnStop.Size = New-Object System.Drawing.Size(160, 38)
$form.Controls.Add($btnStop)

$btnRefreshNow = New-Object System.Windows.Forms.Button
$btnRefreshNow.Text = "⟳ ОБНОВИТЬ ДАННЫЕ"
$btnRefreshNow.Font = New-Object System.Drawing.Font("Segoe UI", 9.5, [System.Drawing.FontStyle]::Bold)
$btnRefreshNow.BackColor = [System.Drawing.Color]::FromArgb(8, 145, 178)
$btnRefreshNow.ForeColor = [System.Drawing.Color]::White
$btnRefreshNow.FlatStyle = "Flat"
$btnRefreshNow.Location = New-Object System.Drawing.Point(410, 170)
$btnRefreshNow.Size = New-Object System.Drawing.Size(280, 38)
$form.Controls.Add($btnRefreshNow)

# БЛОК 2b: Обновление агента (версия, проверка, огненная шкала загрузки)
$lblVersion = New-Object System.Windows.Forms.Label
$lblVersion.Text = "Установлена: v$AppVersion"
$lblVersion.Font = New-Object System.Drawing.Font("Consolas", 9, [System.Drawing.FontStyle]::Bold)
$lblVersion.ForeColor = [System.Drawing.Color]::FromArgb(148, 163, 184)
$lblVersion.Location = New-Object System.Drawing.Point(345, 214)
$lblVersion.Size = New-Object System.Drawing.Size(165, 22)
$form.Controls.Add($lblVersion)

$btnCheckUpdate = New-Object System.Windows.Forms.Button
$btnCheckUpdate.Text = "ПРОВЕРИТЬ v$AppVersion"
$btnCheckUpdate.Font = New-Object System.Drawing.Font("Segoe UI", 8.5, [System.Drawing.FontStyle]::Bold)
$btnCheckUpdate.BackColor = [System.Drawing.Color]::FromArgb(30, 41, 59)
$btnCheckUpdate.ForeColor = [System.Drawing.Color]::FromArgb(125, 211, 252)
$btnCheckUpdate.FlatStyle = "Flat"
$btnCheckUpdate.Location = New-Object System.Drawing.Point(905, 174)
$btnCheckUpdate.Size = New-Object System.Drawing.Size(180, 30)
$form.Controls.Add($btnCheckUpdate)

$btnFullscreen = New-Object System.Windows.Forms.Button
$btnFullscreen.Text = "ПОЛНЫЙ ЭКРАН"
$btnFullscreen.Font = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
$btnFullscreen.BackColor = [System.Drawing.Color]::FromArgb(51, 65, 85)
$btnFullscreen.ForeColor = [System.Drawing.Color]::White
$btnFullscreen.FlatStyle = "Flat"
$btnFullscreen.Location = New-Object System.Drawing.Point(700, 170)
$btnFullscreen.Size = New-Object System.Drawing.Size(190, 38)
$form.Controls.Add($btnFullscreen)

$lblUpdateInfo = New-Object System.Windows.Forms.Label
$lblUpdateInfo.Text = ""
$lblUpdateInfo.Font = New-Object System.Drawing.Font("Consolas", 9, [System.Drawing.FontStyle]::Bold)
$lblUpdateInfo.ForeColor = [System.Drawing.Color]::FromArgb(251, 191, 36)
$lblUpdateInfo.Location = New-Object System.Drawing.Point(520, 214)
$lblUpdateInfo.Size = New-Object System.Drawing.Size(565, 20)
$form.Controls.Add($lblUpdateInfo)

$btnAutoMode = New-Object System.Windows.Forms.Button
$btnAutoMode.Font = New-Object System.Drawing.Font("Segoe UI", 8.5, [System.Drawing.FontStyle]::Bold)
$btnAutoMode.FlatStyle = "Flat"
$btnAutoMode.Location = New-Object System.Drawing.Point(905, 210)
$btnAutoMode.Size = New-Object System.Drawing.Size(180, 28)
$btnAutoMode.Enabled = (-not $ReadOnly)
$form.Controls.Add($btnAutoMode)
$tipAutoMode = New-Object System.Windows.Forms.ToolTip
$tipAutoMode.SetToolTip($btnAutoMode, "Автозапуск майнера при входе в Windows. Окно будет скрыто в область уведомлений.")

function Update-AutoModeButton {
    if ($script:AutoStartMining) {
        $btnAutoMode.Text = "АВТОЗАПУСК: ВКЛ"
        $btnAutoMode.BackColor = [System.Drawing.Color]::FromArgb(16, 185, 129)
        $btnAutoMode.ForeColor = [System.Drawing.Color]::FromArgb(11, 15, 23)
    } else {
        $btnAutoMode.Text = "РУЧНОЙ РЕЖИМ"
        $btnAutoMode.BackColor = [System.Drawing.Color]::FromArgb(51, 65, 85)
        $btnAutoMode.ForeColor = [System.Drawing.Color]::White
    }
}
Update-AutoModeButton

$lblTariff = New-Object System.Windows.Forms.Label
$lblTariff.Text = "Тариф ₽/кВт·ч"
$lblTariff.Font = New-Object System.Drawing.Font("Segoe UI", 8.5, [System.Drawing.FontStyle]::Bold)
$lblTariff.ForeColor = [System.Drawing.Color]::FromArgb(203, 213, 225)
$lblTariff.Location = New-Object System.Drawing.Point(20, 214)
$lblTariff.Size = New-Object System.Drawing.Size(105, 24)
$form.Controls.Add($lblTariff)

$txtTariff = New-Object System.Windows.Forms.TextBox
$txtTariff.Text = if ($script:ElectricityTariff -gt 0) { $script:ElectricityTariff.ToString([Globalization.CultureInfo]::InvariantCulture) } else { "" }
$txtTariff.Font = New-Object System.Drawing.Font("Consolas", 9, [System.Drawing.FontStyle]::Bold)
$txtTariff.Location = New-Object System.Drawing.Point(126, 212)
$txtTariff.Size = New-Object System.Drawing.Size(80, 26)
$txtTariff.BackColor = [System.Drawing.Color]::FromArgb(11, 15, 23)
$txtTariff.ForeColor = [System.Drawing.Color]::White
$form.Controls.Add($txtTariff)

$btnSaveTariff = New-Object System.Windows.Forms.Button
$btnSaveTariff.Text = "Сохранить тариф"
$btnSaveTariff.Font = New-Object System.Drawing.Font("Segoe UI", 8.5, [System.Drawing.FontStyle]::Bold)
$btnSaveTariff.BackColor = [System.Drawing.Color]::FromArgb(30, 41, 59)
$btnSaveTariff.ForeColor = [System.Drawing.Color]::White
$btnSaveTariff.FlatStyle = "Flat"
$btnSaveTariff.Location = New-Object System.Drawing.Point(214, 211)
$btnSaveTariff.Size = New-Object System.Drawing.Size(120, 28)
$form.Controls.Add($btnSaveTariff)

$lblGpuPower = New-Object System.Windows.Forms.Label
$lblGpuPower.Text = "Лимит мощности GPU (Вт на карту)"
$lblGpuPower.Font = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
$lblGpuPower.ForeColor = [System.Drawing.Color]::FromArgb(203, 213, 225)
$lblGpuPower.Location = New-Object System.Drawing.Point(20, 246)
$lblGpuPower.Size = New-Object System.Drawing.Size(230, 26)
$form.Controls.Add($lblGpuPower)

$trackGpuPower = New-Object System.Windows.Forms.TrackBar
$trackGpuPower.Minimum = $script:GpuPowerMin
$trackGpuPower.Maximum = [Math]::Max($script:GpuPowerMin + 1, $script:GpuPowerMax)
$trackGpuPower.Value = [Math]::Max($trackGpuPower.Minimum, [Math]::Min($trackGpuPower.Maximum, $script:GpuPowerLimit))
$trackGpuPower.TickFrequency = 10
$trackGpuPower.SmallChange = 1
$trackGpuPower.LargeChange = 10
$trackGpuPower.AutoSize = $false
$trackGpuPower.Location = New-Object System.Drawing.Point(250, 240)
$trackGpuPower.Size = New-Object System.Drawing.Size(550, 38)
$trackGpuPower.Enabled = $script:GpuPowerRangeVerified -and (-not $ReadOnly)
$form.Controls.Add($trackGpuPower)

$lblGpuPowerValue = New-Object System.Windows.Forms.Label
$lblGpuPowerValue.Text = if ($script:GpuPowerRangeVerified) { "$($script:GpuPowerLimit) Вт на карту" } else { "нет диапазона" }
$lblGpuPowerValue.Font = New-Object System.Drawing.Font("Consolas", 9, [System.Drawing.FontStyle]::Bold)
$lblGpuPowerValue.ForeColor = [System.Drawing.Color]::FromArgb(251, 191, 36)
$lblGpuPowerValue.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
$lblGpuPowerValue.Location = New-Object System.Drawing.Point(805, 246)
$lblGpuPowerValue.Size = New-Object System.Drawing.Size(135, 26)
$form.Controls.Add($lblGpuPowerValue)

$btnApplyGpuPower = New-Object System.Windows.Forms.Button
$btnApplyGpuPower.Text = "ПРИМЕНИТЬ"
$btnApplyGpuPower.Font = New-Object System.Drawing.Font("Segoe UI", 8.5, [System.Drawing.FontStyle]::Bold)
$btnApplyGpuPower.BackColor = [System.Drawing.Color]::FromArgb(30, 41, 59)
$btnApplyGpuPower.ForeColor = [System.Drawing.Color]::White
$btnApplyGpuPower.FlatStyle = "Flat"
$btnApplyGpuPower.Location = New-Object System.Drawing.Point(950, 244)
$btnApplyGpuPower.Size = New-Object System.Drawing.Size(135, 30)
$btnApplyGpuPower.Enabled = $script:GpuPowerRangeVerified -and (-not $ReadOnly)
$form.Controls.Add($btnApplyGpuPower)
$trackGpuPower.Add_ValueChanged({ $lblGpuPowerValue.Text = "$($trackGpuPower.Value) Вт на карту" })

$pbarDownload = New-Object System.Windows.Forms.ProgressBar
$pbarDownload.Style = "Continuous"
$pbarDownload.Minimum = 0
$pbarDownload.Maximum = 100
$pbarDownload.Value = 0
$pbarDownload.Visible = $false
$pbarDownload.Location = New-Object System.Drawing.Point(20, 280)
$pbarDownload.Size = New-Object System.Drawing.Size(1065, 18)
$form.Controls.Add($pbarDownload)

$script:updateLayer = $null
$script:updateTimer = $null

function Start-FireProgressBar {
    if ($script:updateLayer -and -not $script:updateLayer.IsDisposed) { return }
    $layer = New-Object System.Windows.Forms.Panel
    $layer.Location = $pbarDownload.Location
    $layer.Size = $pbarDownload.Size
    $layer.BackColor = [System.Drawing.Color]::Transparent
    $pbarDownload.Parent.Controls.Add($layer)
    $layer.BringToFront()
    $script:updateLayer = $layer

    $tm = New-Object System.Windows.Forms.Timer
    $tm.Interval = 80
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $tm.Add_Tick({
        if (-not $script:updateLayer -or $script:updateLayer.IsDisposed) { $tm.Stop(); return }
        $g = $script:updateLayer.CreateGraphics()
        try {
            $g.Clear([System.Drawing.Color]::Transparent)
            $w = $script:updateLayer.Width
            $h = $script:updateLayer.Height
            $frac = $pbarDownload.Value / 100.0
            $fillW = [int]($w * $frac)
            if ($fillW -gt 2) {
                $cols = @([System.Drawing.Color]::FromArgb(255, 220, 38, 38), [System.Drawing.Color]::FromArgb(255, 249, 115, 22), [System.Drawing.Color]::FromArgb(255, 251, 191, 36))
                $segW = [Math]::Max(6, [int]($fillW / 24))
                $x = 0
                $ci = 0
                while ($x -lt $fillW) {
                    $brush = New-Object System.Drawing.SolidBrush($cols[$ci % 3])
                    $g.FillRectangle($brush, $x, 0, [Math]::Min($segW, $fillW - $x), $h)
                    $brush.Dispose()
                    $x += $segW
                    $ci++
                }
                $flicker = [System.Drawing.Color]::FromArgb(255, 254, 240, 138)
                if (((Get-Date).Millisecond -band 1) -eq 0) {
                    $fb = New-Object System.Drawing.SolidBrush($flicker)
                    $g.FillRectangle($fb, [Math]::Max(0, $fillW - 6), 0, 6, $h)
                    $fb.Dispose()
                }
            }
            $txt = "$($pbarDownload.Value)% · v$AppVersion -> v$($script:LatestVersion)"
            $fmt = New-Object System.Drawing.StringFormat
            $fmt.Alignment = "Center"
            $rect = New-Object System.Drawing.RectangleF(0, 0, $w, $h)
            $g.DrawString($txt, $script:updateLayer.Font, [System.Drawing.Brushes]::White, $rect, $fmt)
            $fmt.Dispose()
        } finally {
            $g.Dispose()
        }
        if ($sw.Elapsed.TotalSeconds -gt 1.5) {
            $script:updateLayer.Invalidate()
            $pbarDownload.Invalidate()
            $sw.Restart()
        }
    })
    $script:updateTimer = $tm
    $tm.Start()
}

function Stop-FireProgressBar {
    if ($script:updateTimer) { $script:updateTimer.Stop(); $script:updateTimer.Dispose(); $script:updateTimer = $null }
    if ($script:updateLayer -and -not $script:updateLayer.IsDisposed) {
        $script:updateLayer.Visible = $false
        $script:updateLayer.Dispose()
    }
    $script:updateLayer = $null
}

function Start-BackgroundPowerShell([string]$ScriptText, [object[]]$Arguments) {
    $runspace = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace()
    $runspace.ApartmentState = [System.Threading.ApartmentState]::MTA
    $runspace.ThreadOptions = [System.Management.Automation.Runspaces.PSThreadOptions]::ReuseThread
    $runspace.Open()
    $powerShell = [System.Management.Automation.PowerShell]::Create()
    $powerShell.Runspace = $runspace
    [void]$powerShell.AddScript($ScriptText)
    foreach ($argument in $Arguments) { [void]$powerShell.AddArgument($argument) }
    try {
        $async = $powerShell.BeginInvoke()
        return @{ PowerShell = $powerShell; Runspace = $runspace; Async = $async }
    } catch {
        $powerShell.Dispose()
        $runspace.Dispose()
        throw
    }
}

function Dispose-BackgroundPowerShell($Work) {
    if (-not $Work) { return }
    try { $Work.PowerShell.Dispose() } catch {}
    try { $Work.Runspace.Dispose() } catch {}
}

function Get-GitHubLatestRelease {
    try {
        $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$UpdateRepo/releases/latest" -Method Get -TimeoutSec 8 -Headers @{ "User-Agent" = "PearlRigControl"; "Accept" = "application/vnd.github+json" } -ErrorAction Stop
        $tag = [string]$rel.tag_name
        $ver = $tag -replace "^[vV]", ""
        $assetUrl = $null
        foreach ($a in $rel.assets) {
            if ($a.name -like "*PearlRigControl-Setup*") { $assetUrl = [string]$a.browser_download_url; break }
        }
        return @{ Version = $ver; Url = $assetUrl }
    } catch {
        return $null
    }
}

function Complete-UpdateCheck {
    $work = $script:UpdateCheckWork
    if (-not $work) { return }
    $script:UpdateCheckWork = $null
    $errorMessage = $null
    $latest = $null
    try {
        $results = @($work.PowerShell.EndInvoke($work.Async))
        if ($results.Count -gt 0) { $latest = $results[$results.Count - 1] }
        if (-not $latest) { throw "GitHub lookup returned no release data." }
    } catch {
        $errorMessage = [string]$_.Exception.Message
        if ([string]::IsNullOrWhiteSpace($errorMessage)) { $errorMessage = [string]$_.Exception.ToString() }
        if ([string]::IsNullOrWhiteSpace($errorMessage)) { $errorMessage = "Не удалось получить результат проверки GitHub." }
    } finally {
        Dispose-BackgroundPowerShell $work
    }

    $script:UpdateCheckActive = $false
    $btnCheckUpdate.Enabled = $true
    $btnCheckUpdate.Text = "ПРОВЕРИТЬ v$AppVersion"
    $btnCheckUpdate.BackColor = [System.Drawing.Color]::FromArgb(30, 41, 59)
    $btnCheckUpdate.ForeColor = [System.Drawing.Color]::FromArgb(125, 211, 252)
    if ($errorMessage) {
        $script:UpdateAvailable = $false
        $script:LatestReleaseUrl = $null
        $lblVersion.Text = "Установлена: v$AppVersion"
        $lblUpdateInfo.ForeColor = [System.Drawing.Color]::FromArgb(248, 113, 113)
        $lblUpdateInfo.Text = "Проверка GitHub не удалась. Подробности в журнале."
        $tipAutoMode.SetToolTip($lblUpdateInfo, $errorMessage)
        Write-AgentLog "ERROR" "Проверка обновлений не удалась: $errorMessage"
        return
    }

    $script:LatestVersion = [string]$latest.Version
    $script:LatestReleaseUrl = [string]$latest.Url
    try { $script:UpdateAvailable = ([System.Version]$latest.Version -gt [System.Version]$AppVersion) }
    catch { $script:UpdateAvailable = ($latest.Version -ne $AppVersion) }
    $lblVersion.Text = "Установлена: v$AppVersion"
    if ($script:UpdateAvailable -and $latest.Url) {
        $lblVersion.ForeColor = [System.Drawing.Color]::FromArgb(251, 191, 36)
        $lblUpdateInfo.ForeColor = [System.Drawing.Color]::FromArgb(251, 146, 60)
        $lblUpdateInfo.Text = "ВЫШЛА НОВАЯ ВЕРСИЯ v$($latest.Version). Нажмите «СКАЧАТЬ»."
        $btnCheckUpdate.Text = "СКАЧАТЬ v$($latest.Version)"
        $btnCheckUpdate.BackColor = [System.Drawing.Color]::FromArgb(234, 88, 12)
        $btnCheckUpdate.ForeColor = [System.Drawing.Color]::White
        Write-AgentLog "OK" "Обнаружена новая версия агента: v$($latest.Version) (установлена v$AppVersion)."
    } elseif ($script:UpdateAvailable) {
        $script:UpdateAvailable = $false
        $lblUpdateInfo.ForeColor = [System.Drawing.Color]::FromArgb(248, 113, 113)
        $lblUpdateInfo.Text = "v$($latest.Version) вышла, но установщик не найден в релизе."
        Write-AgentLog "ERROR" "В релизе v$($latest.Version) отсутствует PearlRigControl-Setup.exe."
    } else {
        $lblVersion.ForeColor = [System.Drawing.Color]::FromArgb(148, 163, 184)
        $lblUpdateInfo.ForeColor = [System.Drawing.Color]::FromArgb(52, 211, 153)
        $lblUpdateInfo.Text = "Установлена последняя версия v$AppVersion."
        Write-AgentLog "INFO" "Проверка обновлений: установлена актуальная версия v$AppVersion."
    }
}

function Test-UpdateAvailable {
    if ($script:UpdateCheckActive -or $script:DownloadActive) { return }
    $script:UpdateCheckActive = $true
    $lblUpdateInfo.ForeColor = [System.Drawing.Color]::FromArgb(56, 189, 248)
    $lblUpdateInfo.Text = "Проверяю последнюю версию на GitHub..."
    $btnCheckUpdate.Text = "ПРОВЕРКА..."
    $btnCheckUpdate.Enabled = $false
    $checkScript = @'
param($repo)
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
if ([string]::IsNullOrWhiteSpace([string]$repo)) { throw "GitHub repository is not configured." }
$ver = $null
$assetUrl = $null
$apiError = $null
try {
    $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Method Get -TimeoutSec 10 -Headers @{ "User-Agent" = "PearlRigControl"; "Accept" = "application/vnd.github+json" } -ErrorAction Stop
    $ver = ([string]$rel.tag_name) -replace "^[vV]", ""
    foreach ($asset in $rel.assets) { if ($asset.name -eq "PearlRigControl-Setup.exe") { $assetUrl = [string]$asset.browser_download_url; break } }
} catch { $apiError = $_.Exception.Message }
if (-not $ver -or -not $assetUrl) {
    $webResponse = $null
    try {
        $request = [System.Net.HttpWebRequest]::Create("https://github.com/$repo/releases/latest")
        $request.UserAgent = "PearlRigControl"
        $request.Timeout = 10000
        $request.AllowAutoRedirect = $true
        $webResponse = $request.GetResponse()
        $tag = [Uri]::UnescapeDataString($webResponse.ResponseUri.Segments[-1].Trim('/'))
        if ($tag -notmatch '^[vV]?\d+(\.\d+){1,3}$') { throw "GitHub latest release redirected to an unexpected address." }
        $ver = $tag -replace "^[vV]", ""
        if (-not $assetUrl) { $assetUrl = "https://github.com/$repo/releases/download/$tag/PearlRigControl-Setup.exe" }
    } catch {
        $pageError = $_.Exception.Message
        if ($apiError) { throw "GitHub API failed ($apiError); latest release page failed ($pageError)." }
        throw "Latest release lookup failed ($pageError)."
    } finally { if ($webResponse) { $webResponse.Close() } }
}
if (-not $ver -or -not $assetUrl) { throw "Latest release has no PearlRigControl-Setup.exe asset." }
return @{ Version = $ver; Url = $assetUrl }
'@
    try {
        $script:UpdateCheckWork = Start-BackgroundPowerShell -ScriptText $checkScript -Arguments @($UpdateRepo)
    } catch {
        $script:UpdateCheckActive = $false
        $btnCheckUpdate.Enabled = $true
        $btnCheckUpdate.Text = "ПРОВЕРИТЬ v$AppVersion"
        $lblUpdateInfo.ForeColor = [System.Drawing.Color]::FromArgb(248, 113, 113)
        $lblUpdateInfo.Text = "Не удалось запустить проверку GitHub."
        Write-AgentLog "ERROR" "Не удалось создать пространство проверки обновлений: $($_.Exception.Message)"
    }
}

function Complete-UpdateDownload {
    $work = $script:UpdateDownloadWork
    if (-not $work) { return }
    $script:UpdateDownloadWork = $null
    $result = $null
    $errorMessage = $null
    try {
        $results = @($work.PowerShell.EndInvoke($work.Async))
        if ($results.Count -gt 0) { $result = $results[$results.Count - 1] }
        if (-not $result) { throw "Download worker returned no result." }
    } catch {
        $errorMessage = [string]$_.Exception.Message
        if ([string]::IsNullOrWhiteSpace($errorMessage)) { $errorMessage = [string]$_.Exception.ToString() }
        if ([string]::IsNullOrWhiteSpace($errorMessage)) { $errorMessage = "Не удалось получить результат загрузки." }
    } finally {
        Dispose-BackgroundPowerShell $work
        $script:DownloadProgressState = $null
        $script:DownloadActive = $false
        Stop-FireProgressBar
        $pbarDownload.Visible = $false
        $btnCheckUpdate.Enabled = $true
    }
    if ($errorMessage) {
        $lblUpdateInfo.ForeColor = [System.Drawing.Color]::FromArgb(248, 113, 113)
        $lblUpdateInfo.Text = "Загрузка обновления не удалась. Подробности в журнале."
        $tipAutoMode.SetToolTip($lblUpdateInfo, $errorMessage)
        Write-AgentLog "ERROR" "Сбой при загрузке обновления: $errorMessage"
        return
    }
    $pbarDownload.Value = 100
    $lblUpdateInfo.ForeColor = [System.Drawing.Color]::FromArgb(52, 211, 153)
    $lblUpdateInfo.Text = "v$($result.Version) загружена. Установщик готов."
    Write-AgentLog "OK" "Новая версия v$($result.Version) загружена в $($result.Path), $($result.Bytes) байт."
    $confirm = [System.Windows.Forms.MessageBox]::Show("Установщик v$($result.Version) загружен.`n`nЗапустить установку сейчас?", "Обновление готово", [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question)
    if ($confirm -eq [System.Windows.Forms.DialogResult]::Yes) { Start-Process -FilePath $result.Path }
}

function Start-AgentUpdateDownload {
    if ($script:DownloadActive) { return }
    $latest = @{ Version = $script:LatestVersion; Url = $script:LatestReleaseUrl }
    if (-not $latest.Version -or -not $latest.Url) {
        Write-AgentLog "ERROR" "Не найден файл установки для скачивания в последнем релизе."
        $lblUpdateInfo.ForeColor = [System.Drawing.Color]::FromArgb(248, 113, 113)
        $lblUpdateInfo.Text = "В релизе нет файла PearlRigControl-Setup.exe."
        return
    }
    $script:DownloadActive = $true
    $script:DownloadProgress = 0
    $script:DownloadProgressState = [hashtable]::Synchronized(@{ Done = [long]0; Total = [long]0; Version = [string]$latest.Version })
    $pbarDownload.Value = 1
    $pbarDownload.Visible = $true
    Start-FireProgressBar
    $lblUpdateInfo.ForeColor = [System.Drawing.Color]::FromArgb(251, 146, 60)
    $lblUpdateInfo.Text = "Загрузка v$($latest.Version)... 0%"
    $btnCheckUpdate.Enabled = $false
    Write-AgentLog "INFO" "Начата загрузка новой версии агента v$($latest.Version): $($latest.Url)"

    $tmpFile = Join-Path ([System.IO.Path]::GetTempPath()) "PearlRigControl-Setup-$($latest.Version).exe"
    $job = @{ Url = [string]$latest.Url; Path = [string]$tmpFile; Version = [string]$latest.Version }
    $downloadScript = @'
param($job, $progress)
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$response = $null; $inputStream = $null; $outputStream = $null
try {
    $req = [System.Net.HttpWebRequest]::Create($job.Url)
    $req.UserAgent = "PearlRigControl"
    $req.AllowAutoRedirect = $true
    $req.Timeout = 30000
    $req.ReadWriteTimeout = 30000
    $response = $req.GetResponse()
    $total = [long]$response.ContentLength
    $progress.Total = $total
    $inputStream = $response.GetResponseStream()
    $outputStream = [System.IO.File]::Create($job.Path)
    $buffer = New-Object byte[] 65536
    [long]$done = 0
    while ($true) {
        $read = $inputStream.Read($buffer, 0, $buffer.Length)
        if ($read -le 0) { break }
        $outputStream.Write($buffer, 0, $read)
        $done += $read
        $progress.Done = $done
    }
    if ($total -gt 0 -and $done -ne $total) { throw "Downloaded $done bytes of $total; file is incomplete." }
    if ($done -lt 1000000) { throw "Server returned an unexpectedly small file ($done bytes)." }
    return @{ Path = $job.Path; Version = $job.Version; Bytes = $done }
} catch {
    if (Test-Path -LiteralPath $job.Path) { Remove-Item -LiteralPath $job.Path -Force -ErrorAction SilentlyContinue }
    throw
} finally {
    if ($outputStream) { $outputStream.Dispose() }
    if ($inputStream) { $inputStream.Dispose() }
    if ($response) { $response.Dispose() }
}
'@
    try {
        $script:UpdateDownloadWork = Start-BackgroundPowerShell -ScriptText $downloadScript -Arguments @($job, $script:DownloadProgressState)
    } catch {
        $script:DownloadActive = $false
        $script:DownloadProgressState = $null
        Stop-FireProgressBar
        $pbarDownload.Visible = $false
        $btnCheckUpdate.Enabled = $true
        $lblUpdateInfo.ForeColor = [System.Drawing.Color]::FromArgb(248, 113, 113)
        $lblUpdateInfo.Text = "Не удалось запустить загрузку обновления."
        Write-AgentLog "ERROR" "Не удалось создать пространство загрузки обновления: $($_.Exception.Message)"
    }
}

# БЛОК 3: Карточки реальной телеметрии (без выдуманных чисел)
$lblTelemetryBox = New-Object System.Windows.Forms.Panel
$lblTelemetryBox.Font = New-Object System.Drawing.Font("Consolas", 9)
$lblTelemetryBox.BackColor = [System.Drawing.Color]::FromArgb(15, 23, 42)
$lblTelemetryBox.ForeColor = [System.Drawing.Color]::FromArgb(226, 232, 240)
$lblTelemetryBox.BorderStyle = "FixedSingle"
$lblTelemetryBox.Location = New-Object System.Drawing.Point(20, 270)
$lblTelemetryBox.Size = New-Object System.Drawing.Size(1160, 250)
$lblTelemetryBox.Padding = New-Object System.Windows.Forms.Padding(12)
$form.Controls.Add($lblTelemetryBox)

$lblEarningsPanel = New-Object System.Windows.Forms.Label
$lblEarningsPanel.Font = New-Object System.Drawing.Font("Consolas", 9, [System.Drawing.FontStyle]::Bold)
$lblEarningsPanel.BackColor = [System.Drawing.Color]::FromArgb(15, 23, 42)
$lblEarningsPanel.ForeColor = [System.Drawing.Color]::FromArgb(52, 211, 153)
$lblEarningsPanel.BorderStyle = "FixedSingle"
$lblEarningsPanel.Location = New-Object System.Drawing.Point(20, 532)
$lblEarningsPanel.Size = New-Object System.Drawing.Size(570, 115)
$lblEarningsPanel.Padding = New-Object System.Windows.Forms.Padding(10)
$form.Controls.Add($lblEarningsPanel)

$lblPowerPanel = New-Object System.Windows.Forms.Label
$lblPowerPanel.Font = New-Object System.Drawing.Font("Consolas", 9, [System.Drawing.FontStyle]::Bold)
$lblPowerPanel.BackColor = [System.Drawing.Color]::FromArgb(24, 24, 27)
$lblPowerPanel.ForeColor = [System.Drawing.Color]::FromArgb(251, 191, 36)
$lblPowerPanel.BorderStyle = "FixedSingle"
$lblPowerPanel.Location = New-Object System.Drawing.Point(600, 532)
$lblPowerPanel.Size = New-Object System.Drawing.Size(580, 115)
$lblPowerPanel.Padding = New-Object System.Windows.Forms.Padding(10)
$form.Controls.Add($lblPowerPanel)

# БЛОК 4: Журнал событий и ошибок
$txtLog = New-Object System.Windows.Forms.TextBox
$txtLog.Multiline = $true
$txtLog.ReadOnly = $true
$txtLog.ScrollBars = "Vertical"
$txtLog.BackColor = [System.Drawing.Color]::FromArgb(5, 8, 14)
$txtLog.ForeColor = [System.Drawing.Color]::FromArgb(203, 213, 225)
$txtLog.Font = New-Object System.Drawing.Font("Consolas", 9)
$txtLog.Location = New-Object System.Drawing.Point(20, 660)
$txtLog.Size = New-Object System.Drawing.Size(1160, 190)
$form.Controls.Add($txtLog)

$script:TrayIcon = New-Object System.Windows.Forms.NotifyIcon
$script:TrayIcon.Text = "Pearl Rig Control — майнер остановлен"
$trayIconPath = Join-Path $InstalledAppDir 'pearl-icon.ico'
try {
    if (Test-Path -LiteralPath $trayIconPath) { $script:TrayIcon.Icon = New-Object System.Drawing.Icon($trayIconPath) }
    else { $script:TrayIcon.Icon = [System.Drawing.SystemIcons]::Application }
} catch { $script:TrayIcon.Icon = [System.Drawing.SystemIcons]::Application }
$script:TrayMenu = New-Object System.Windows.Forms.ContextMenuStrip
$script:TrayStatusItem = $script:TrayMenu.Items.Add("Состояние: майнер остановлен")
$script:TrayStatusItem.Enabled = $false
[void]$script:TrayMenu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
$script:TrayOpenItem = $script:TrayMenu.Items.Add("Открыть панель")
$script:TrayStartItem = $script:TrayMenu.Items.Add("Запустить майнер")
$script:TrayStopItem = $script:TrayMenu.Items.Add("Остановить майнер")
[void]$script:TrayMenu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
$script:TrayExitItem = $script:TrayMenu.Items.Add("Остановить майнер и закрыть")
$script:TrayIcon.ContextMenuStrip = $script:TrayMenu
$script:TrayIcon.Visible = [bool]$Startup

if ($Startup) {
    $form.ShowInTaskbar = $false
    $form.Opacity = 0
}

function Update-WindowLayout {
    $w = $form.ClientSize.Width
    $h = $form.ClientSize.Height
    if ($w -lt 900 -or $h -lt 650) { return }
    $right = $w - 20
    $lblTitle.Width = $w - 40
    $lblStatus.Width = $w - 40
    $grpWallet.Width = $w - 40
    $lblWalletHeader.Width = $grpWallet.ClientSize.Width - 24
    $btnEditWallet.Left = $grpWallet.ClientSize.Width - $btnEditWallet.Width - 12
    $btnSaveWallet.Left = $btnEditWallet.Left - $btnSaveWallet.Width - 8
    $btnCheckWallet.Left = $btnSaveWallet.Left - $btnCheckWallet.Width - 8
    $txtWallet.Width = [Math]::Max(240, $btnCheckWallet.Left - $txtWallet.Left - 10)
    $lblWalletHint.Width = $grpWallet.ClientSize.Width - 24
    $btnCheckUpdate.Left = $right - $btnCheckUpdate.Width
    $btnFullscreen.Left = $btnCheckUpdate.Left - $btnFullscreen.Width - 12
    $btnAutoMode.Left = $right - $btnAutoMode.Width
    $lblUpdateInfo.Left = 520
    $lblUpdateInfo.Width = [Math]::Max(180, $btnAutoMode.Left - $lblUpdateInfo.Left - 10)
    $lblVersion.Left = 345
    $btnAutoMode.Top = 210
    $pbarDownload.Width = $w - 40
    $btnApplyGpuPower.Left = $right - $btnApplyGpuPower.Width
    $lblGpuPowerValue.Left = $btnApplyGpuPower.Left - $lblGpuPowerValue.Width - 8
    $trackGpuPower.Left = $lblGpuPower.Left + $lblGpuPower.Width
    $trackGpuPower.Width = [Math]::Max(180, $lblGpuPowerValue.Left - $trackGpuPower.Left - 8)
    $lblGpuPowerValue.Location = New-Object System.Drawing.Point($lblGpuPowerValue.Left, 246)
    $btnApplyGpuPower.Top = 244
    $trackGpuPower.Top = 240
    $lblGpuPower.Top = 246
    $telemetryHeight = 250
    $lblTelemetryBox.Top = 310
    $lblTelemetryBox.Width = $w - 40
    $lblTelemetryBox.Height = $telemetryHeight
    $gap = 10
    $half = [int](($w - 50 - $gap) / 2)
    $lblEarningsPanel.Left = 20
    $lblEarningsPanel.Top = $lblTelemetryBox.Bottom + 12
    $lblEarningsPanel.Width = $half
    $lblEarningsPanel.Height = 115
    $lblPowerPanel.Left = $lblEarningsPanel.Right + $gap
    $lblPowerPanel.Top = $lblEarningsPanel.Top
    $lblPowerPanel.Width = $half
    $lblPowerPanel.Height = 115
    $txtLog.Left = 20
    $txtLog.Top = $lblEarningsPanel.Bottom + 12
    $txtLog.Width = $w - 40
    $txtLog.Height = [Math]::Max(100, $h - $txtLog.Top - 20)
}

$script:DashboardData = $null
$lblTelemetryBox.Add_Paint({
    $g = $_.Graphics
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $data = $script:DashboardData
    if (-not $data) { return }
    $gap = 10
    $tileW = [int](($lblTelemetryBox.ClientSize.Width - 20 - 3 * $gap) / 4)
    $tileH = $lblTelemetryBox.ClientSize.Height - 16
    $tiles = @(
        @{ x = 8; title = "ОБЩИЙ ХЕШРЕЙТ РИГА"; accent = [System.Drawing.Color]::FromArgb(16,185,129); value = $data.totalHashText; line1 = "PearlHash · $($data.processText)"; line2 = "Принято: $($data.acceptedText)   Отклонено: $($data.rejectedText)"; line3 = "Пул: $($data.poolText)" },
        @{ x = 8 + ($tileW + $gap); title = "GPU #0 · RTX 4070 Ti"; accent = [System.Drawing.Color]::FromArgb(34,211,238); value = $data.gpu0HashText; line1 = "Ядро: $($data.gpu0TempText)   VRAM: $($data.gpu0VramTempText)"; line2 = "Вентилятор: $($data.gpu0FanText)   Частоты: $($data.gpu0ClocksText)"; line3 = "Мощность: $($data.gpu0PowerText) · Шары: $($data.gpu0SharesText)" },
        @{ x = 8 + 2 * ($tileW + $gap); title = "GPU #1 · RTX 4070 Ti"; accent = [System.Drawing.Color]::FromArgb(56,189,248); value = $data.gpu1HashText; line1 = "Ядро: $($data.gpu1TempText)   VRAM: $($data.gpu1VramTempText)"; line2 = "Вентилятор: $($data.gpu1FanText)   Частоты: $($data.gpu1ClocksText)"; line3 = "Мощность: $($data.gpu1PowerText) · Шары: $($data.gpu1SharesText)" },
        @{ x = 8 + 3 * ($tileW + $gap); title = "GPU ОБЩАЯ МОЩНОСТЬ"; accent = [System.Drawing.Color]::FromArgb(251,191,36); value = $data.totalPowerText; line1 = "Только измерения nvidia-smi"; line2 = "Накоплено GPU: $($data.energyText)"; line3 = "Энергия по тарифу: $($data.energyCostText)" }
    )
    $fontTitle = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
    $fontValue = New-Object System.Drawing.Font("Consolas", 16, [System.Drawing.FontStyle]::Bold)
    $fontLine = New-Object System.Drawing.Font("Consolas", 8)
    $fontGauge = New-Object System.Drawing.Font("Consolas", 8, [System.Drawing.FontStyle]::Bold)
    $brushWhite = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(241,245,249))
    $brushMuted = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(148,163,184))
    $brushGreen = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(52,211,153))
    $penBack = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(51,65,85), 8)
    try {
        foreach ($tile in $tiles) {
            $rect = New-Object System.Drawing.Rectangle([int]$tile.x, 8, $tileW, $tileH)
            $bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(15,23,42))
            $g.FillRectangle($bg, $rect); $bg.Dispose()
            $accentPen = New-Object System.Drawing.Pen($tile.accent, 3)
            $g.DrawLine($accentPen, $rect.Left, $rect.Top, $rect.Right, $rect.Top); $accentPen.Dispose()
            $g.DrawString([string]$tile.title, $fontTitle, $brushWhite, [single]($rect.Left+10), [single]($rect.Top+10))
            $valueX = $rect.Left + 12
            $valueY = $rect.Top + 54
            $g.DrawString([string]$tile.value, $fontValue, $brushWhite, [single]$valueX, [single]$valueY)
            $lineY = $rect.Bottom - 68
            $g.DrawString([string]$tile.line1, $fontLine, $brushMuted, [single]($rect.Left+10), [single]$lineY)
            $g.DrawString([string]$tile.line2, $fontLine, $brushMuted, [single]($rect.Left+10), [single]($lineY+19))
            $g.DrawString([string]$tile.line3, $fontLine, $brushGreen, [single]($rect.Left+10), [single]($lineY+38))
            if ($tile.title -like 'GPU #*') {
                $gpuId = if ($tile.title.StartsWith('GPU #0')) { 0 } else { 1 }
                $hash = if ($gpuId -eq 0) { $data.gpu0HashRaw } else { $data.gpu1HashRaw }
                if ($null -ne $hash -and $hash -ge 0) {
                    $gx = $rect.Right - 104; $gy = $rect.Top + 43
                    $gauge = New-Object System.Drawing.Rectangle($gx, $gy, 78, 78)
                    $g.DrawArc($penBack, $gauge, 135, 270)
                    $arc = [Math]::Max(0,[Math]::Min(1,[double]$hash / 100.0)) * 270
                    $penGauge = New-Object System.Drawing.Pen($tile.accent, 8)
                    $penGauge.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
                    $penGauge.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                    $g.DrawArc($penGauge, $gauge, 135, [single]$arc); $penGauge.Dispose()
                    $g.DrawString("TH/s", $fontGauge, $brushMuted, [single]($gx+20), [single]($gy+31))
                }
            }
        }
    } finally {
        $fontTitle.Dispose(); $fontValue.Dispose(); $fontLine.Dispose(); $fontGauge.Dispose()
        $brushWhite.Dispose(); $brushMuted.Dispose(); $brushGreen.Dispose(); $penBack.Dispose()
    }
})

function Toggle-Fullscreen {
    if (-not $script:IsFullscreen) {
        $script:SavedFormBounds = $form.Bounds
        $script:SavedFormBorderStyle = $form.FormBorderStyle
        $script:SavedWindowState = $form.WindowState
        $form.WindowState = [System.Windows.Forms.FormWindowState]::Normal
        $form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
        $form.WindowState = [System.Windows.Forms.FormWindowState]::Maximized
        $script:IsFullscreen = $true
        $btnFullscreen.Text = "ВЫЙТИ ИЗ ПОЛНОГО ЭКРАНА (ESC)"
    } else {
        $form.WindowState = [System.Windows.Forms.FormWindowState]::Normal
        $form.FormBorderStyle = $script:SavedFormBorderStyle
        $form.Bounds = $script:SavedFormBounds
        $form.WindowState = $script:SavedWindowState
        $script:IsFullscreen = $false
        $btnFullscreen.Text = "ПОЛНЫЙ ЭКРАН"
    }
    Update-WindowLayout
}
$form.Add_Resize({ Update-WindowLayout })
$form.Add_Resize({
    if ($script:IsStartupTaskLaunch -and $form.WindowState -eq [System.Windows.Forms.FormWindowState]::Minimized) {
        $form.Hide()
        $form.ShowInTaskbar = $false
    }
})
$form.Add_KeyDown({ if ($_.KeyCode -eq [System.Windows.Forms.Keys]::Escape -and $script:IsFullscreen) { Toggle-Fullscreen } })
$btnFullscreen.Add_Click({ Toggle-Fullscreen })

function Write-AgentLog([string]$Level, [string]$Msg) {
    $ts = (Get-Date).ToString("HH:mm:ss")
    $line = "[$ts][$Level] $Msg`r`n"
    if ($txtLog -and -not $txtLog.IsDisposed) {
        $txtLog.AppendText($line)
    }
}

function Sync-WalletUiState {
    $chk = Test-PearlWalletFormat $script:Wallet
    if ($script:WalletSaved -and $chk.Valid -and (-not $script:WalletEditMode)) {
        $txtWallet.ReadOnly = $true
        $btnSaveWallet.Enabled = $false
        $btnEditWallet.Enabled = $true
        $btnStart.Enabled = $true
        $lblWalletHint.ForeColor = [System.Drawing.Color]::FromArgb(52, 211, 153)
        $lblWalletHint.Text = "Сохранённый адрес активен ($($script:Wallet.Length) симв.). Для смены нажмите «Изменить». Можете нажать «▶ ЗАПУСТИТЬ»."
    } else {
        $txtWallet.ReadOnly = $false
        $btnSaveWallet.Enabled = $true
        $btnEditWallet.Enabled = $script:WalletSaved
        $btnStart.Enabled = ($script:WalletSaved -and $chk.Valid)
        $draftChk = Test-PearlWalletFormat $txtWallet.Text
        if ($draftChk.Valid) {
            $lblWalletHint.ForeColor = [System.Drawing.Color]::FromArgb(56, 189, 248)
            $lblWalletHint.Text = "Формат верный. Нажмите «Сохранить локально», чтобы разблокировать кнопку «▶ ЗАПУСТИТЬ»."
        } else {
            $lblWalletHint.ForeColor = [System.Drawing.Color]::FromArgb(251, 191, 36)
            $lblWalletHint.Text = "$($draftChk.Reason) (Запуск заблокирован)"
        }
    }
}

function Format-Metric($val, [string]$suffix = "") {
    if ($null -eq $val) { return "нет данных" }
    if ($val -is [double] -or $val -is [single] -or $val -is [decimal]) { $val = [Math]::Round($val, 2) }
    return "$val$suffix"
}

function Update-PoolBalance {
    $walletCheck = Test-PearlWalletFormat $script:Wallet
    if (-not $walletCheck.Valid) { return }
    try {
        $balance = Invoke-RestMethod -Uri "https://pool.kryptex.com/prl/api/v1/miner/balance/$($script:Wallet)" -TimeoutSec 4 -ErrorAction Stop
        $script:PoolConfirmed = ConvertTo-PearlNumber $balance.confirmed
        $script:PoolPending = ConvertTo-PearlNumber $balance.unconfirmed
        $script:PoolBalance = ConvertTo-PearlNumber $balance.total
        $script:PayoutThreshold = ConvertTo-PearlNumber $balance.threshold
        $script:PoolBalanceFetchedAt = Get-Date
        $script:PoolBalanceError = $null
        $script:LastPoolErrorLogged = $null
        try {
            $stats = Invoke-RestMethod -Uri "https://pool.kryptex.com/prl/api/v1/miner/payouts/$($script:Wallet)/stats" -TimeoutSec 4 -ErrorAction Stop
            $script:PoolPaid = ConvertTo-PearlNumber $stats.paid
        } catch { $script:PoolPaid = $null }
    } catch {
        $script:PoolBalanceError = $_.Exception.Message
        if ($script:LastPoolErrorLogged -ne $script:PoolBalanceError) {
            Write-AgentLog "ERROR" "Kryptex API balance: $($script:PoolBalanceError)"
            $script:LastPoolErrorLogged = $script:PoolBalanceError
        }
    }
}

function Update-OfficialRates {
    try {
        $chart = Invoke-RestMethod -Uri "https://pool.kryptex.com/api/v1/coin/prl/price/chart" -TimeoutSec 4 -ErrorAction Stop
        $nowMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        $point = @($chart | Where-Object { $_.timestamp -le $nowMs -and $_.price -gt 0 } | Sort-Object timestamp | Select-Object -Last 1)
        if ($point.Count -eq 0) { throw "Kryptex returned no current PRL price" }
        $usdResponse = Invoke-WebRequest -Uri "https://www.cbr.ru/scripts/XML_daily.asp" -TimeoutSec 4 -UseBasicParsing -ErrorAction Stop
        [xml]$cbr = $usdResponse.Content
        $usd = @($cbr.ValCurs.Valute | Where-Object { $_.CharCode -eq "USD" } | Select-Object -First 1)
        if ($usd.Count -eq 0) { throw "Банк России не вернул USD/RUB" }
        $usdRub = [double]::Parse(([string]$usd[0].Value).Replace(',', '.'), [Globalization.CultureInfo]::InvariantCulture)
        $script:PrlUsd = [double]$point[0].price
        $script:UsdRub = $usdRub
        $script:PrlRub = $script:PrlUsd * $script:UsdRub
        $script:MarketRatesFetchedAt = Get-Date
        $script:MarketRatesError = $null
        $script:LastRatesErrorLogged = $null
    } catch {
        $script:MarketRatesError = $_.Exception.Message
        if ($script:LastRatesErrorLogged -ne $script:MarketRatesError) {
            Write-AgentLog "ERROR" "Не удалось получить PRL/USD с Kryptex и USD/RUB с Банка России: $($script:MarketRatesError)"
            $script:LastRatesErrorLogged = $script:MarketRatesError
        }
    }
}

function Save-LocalRigSettings {
    $payload = @{ electricityTariff = $script:ElectricityTariff; totalGpuKWh = $script:TotalGpuKWh; gpuPowerLimit = $script:GpuPowerLimit; autoStartMining = $script:AutoStartMining; minerDir = $MinerDir; updatedAt = (Get-Date).ToString("o") } | ConvertTo-Json
    [System.IO.File]::WriteAllText($LocalSettingsFile, $payload, [System.Text.Encoding]::UTF8)
}

function Set-AutoStartMining([bool]$Enable) {
    if ($ReadOnly) { throw "Режим мониторинга не может менять автозапуск." }
    $verb = if ($Enable) { "включить" } else { "отключить" }
    $detail = if ($Enable) { "При следующем входе в Windows майнер запустится автоматически в фоне. Панель будет доступна из значка в области уведомлений. Сейчас майнер запускаться не будет." } else { "При следующих входах в Windows автозапуск майнера будет выключен. Уже работающий майнер продолжит работу." }
    $answer = [System.Windows.Forms.MessageBox]::Show("Вы хотите $verb автозапуск майнера?`r`n`r`n$detail`r`n`r`nДля сохранения режима Windows один раз запросит права администратора.", "Режим запуска Pearl Rig Control", [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question)
    if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }

    $launcher = Join-Path $InstalledAppDir 'pearl_watchdog_agent.bat'
    if (-not (Test-Path -LiteralPath $launcher)) { throw "Не найден установленный файл запуска: $launcher" }
    $escapedLauncher = $launcher.Replace("'", "''")
    $escapedTaskName = $script:AutoStartTaskName.Replace("'", "''")
    $escapedUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name.Replace("'", "''")
    $enableFlag = if ($Enable) { '1' } else { '0' }
    $taskScript = @'
$ErrorActionPreference = 'Stop'
try {
    $taskName = '__TASK_NAME__'
    if ('__ENABLE__' -eq '1') {
        $launcher = '__LAUNCHER__'
        $user = '__USER__'
        $startCommand = "& '$launcher' -Startup -AutoMine"
        $encodedStart = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($startCommand))
        $powershellExe = Join-Path $PSHOME 'powershell.exe'
        $arguments = "-NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand $encodedStart"
        $action = New-ScheduledTaskAction -Execute $powershellExe -Argument $arguments
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero)
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    } else {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    }
    exit 0
} catch {
    Write-Error $_
    exit 1
}
'@
    $taskScript = $taskScript.Replace('__TASK_NAME__', $escapedTaskName).Replace('__LAUNCHER__', $escapedLauncher).Replace('__USER__', $escapedUser).Replace('__ENABLE__', $enableFlag)
    $encodedTask = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($taskScript))
    $powershellExe = Join-Path $PSHOME 'powershell.exe'
    $taskUpdate = Start-Process -FilePath $powershellExe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encodedTask) -Verb RunAs -WindowStyle Hidden -PassThru -Wait -ErrorAction Stop
    if ($taskUpdate.ExitCode -ne 0) { throw "Windows could not update the startup task (exit code $($taskUpdate.ExitCode))." }

    $script:AutoStartMining = $Enable
    Save-LocalRigSettings
    Update-AutoModeButton
    if (-not $Enable -and $script:IsStartupTaskLaunch) {
        $script:IsStartupTaskLaunch = $false
        $script:TrayIcon.Visible = $false
        Show-RigPanelFromTray
    }
    if ($Enable) {
        Write-AgentLog "OK" "Автозапуск майнера включён: при следующем входе в Windows панель откроется в системном трее и запустит майнер. Текущий майнер не запускался."
        $lblStatus.Text = "Статус: автозапуск включён для следующего входа в Windows; сейчас майнер не запускался."
        $lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(52, 211, 153)
    } else {
        Write-AgentLog "INFO" "Автозапуск майнера отключён. Текущий процесс майнера не затронут."
        $lblStatus.Text = "Статус: ручной режим — автозапуск при входе в Windows отключён."
        $lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(251, 191, 36)
    }
}

function Update-TrayStatus {
    if (-not $script:TrayIcon) { return }
    $stateText = if ($script:IsMiningRunning) { "Майнер работает (PID $($script:MinerPid))" } else { "Майнер остановлен" }
    $script:TrayIcon.Text = "Pearl Rig Control — $stateText"
    if ($script:TrayStatusItem) { $script:TrayStatusItem.Text = "Состояние: $stateText" }
    if ($script:TrayStartItem) { $script:TrayStartItem.Enabled = -not $script:IsMiningRunning }
    if ($script:TrayStopItem) { $script:TrayStopItem.Enabled = [bool]$script:IsMiningRunning }
}

function Show-RigPanelFromTray {
    $form.ShowInTaskbar = $true
    $form.Opacity = 1
    $form.WindowState = [System.Windows.Forms.FormWindowState]::Normal
    $form.Visible = $true
    $form.Activate()
}

function Apply-GpuPowerLimit([int]$Watts) {
    if (-not $script:GpuPowerRangeVerified -or -not $script:NvidiaSmiPath) { throw "Не удалось прочитать реальные границы мощности видеокарт через nvidia-smi." }
    if ($Watts -lt $script:GpuPowerMin -or $Watts -gt $script:GpuPowerMax) { throw "Значение $Watts Вт вне общего диапазона карт ($($script:GpuPowerMin)-$($script:GpuPowerMax) Вт)." }
    $escapedCli = $script:NvidiaSmiPath.Replace("'", "''")
    $command = "`$ErrorActionPreference='Stop'; & '$escapedCli' -i 0 -pl $Watts; if (`$LASTEXITCODE -ne 0) { exit 11 }; & '$escapedCli' -i 1 -pl $Watts; if (`$LASTEXITCODE -ne 0) { exit 12 }; exit 0"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $powershellExe = Join-Path $PSHOME 'powershell.exe'
    $elevated = Start-Process -FilePath $powershellExe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded) -Verb RunAs -WindowStyle Hidden -PassThru -Wait -ErrorAction Stop
    if ($elevated.ExitCode -eq 11) { throw "nvidia-smi не смог применить лимит для GPU #0. Проверьте драйвер и минимально допустимое значение." }
    if ($elevated.ExitCode -eq 12) { throw "Лимит применился к GPU #0, но не к GPU #1. Проверьте драйвер и значение." }
    if ($elevated.ExitCode -ne 0) { throw "Windows завершила применение лимита с кодом $($elevated.ExitCode)." }
    $script:GpuPowerLimit = $Watts
    $script:CurrentPL = "$Watts,$Watts"
    Save-LocalRigSettings
    Write-AgentLog "OK" "Ограничение мощности применено: $Watts Вт на каждую видеокарту. Сохранено для следующих запусков майнера."
    $lblStatus.Text = "Статус: лимит GPU установлен — $Watts Вт на карту (диапазон $($script:GpuPowerMin)-$($script:GpuPowerMax) Вт)."
    $lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(52, 211, 153)
}

function Update-TelemetryDisplay {
    $poolStr = if ($null -eq $script:PoolConnected) { "нет данных" } elseif ($script:PoolConnected) { "Connected ($($script:ActivePoolUrl))" } else { "Disconnected" }
    $apiStr = if ($script:ApiAvailable) { "Доступен (127.0.0.1:$ApiPort)" } else { "нет данных (API 127.0.0.1:$ApiPort недоступен)" }
    $nvmlStr = if ($script:NvmlAvailable) { "nvidia-smi (реальные датчики)" } elseif ($script:ApiAvailable) { "API майнера (nvidia-smi недоступен)" } else { "нет данных" }
    $procStr = if ($script:IsMiningRunning) { "Работает (PID: $($script:MinerPid))" } elseif ($script:UserStoppedManually) { "Намеренно остановлен пользователем (Стоп)" } else { "Не запущен" }

    $g0 = $script:TelemetrySnapshot.gpus[0]
    $g1 = $script:TelemetrySnapshot.gpus[1]
    $totalPower = if ($null -ne $script:Gpu0Power -and $null -ne $script:Gpu1Power) { [double]$script:Gpu0Power + [double]$script:Gpu1Power } else { $null }
    $totalPowerKwText = if ($null -eq $totalPower) { "нет данных" } else { Format-Metric ($totalPower / 1000) ' кВт' }
    $energyCost = if ($script:ElectricityTariff -gt 0) { $script:TotalGpuKWh * $script:ElectricityTariff } else { $null }
    $script:DashboardData = @{
        totalHashText = "$(Format-Metric $script:TotalHash ' TH/s')"
        totalPowerText = if ($null -eq $totalPower) { "нет данных" } else { "$(Format-Metric $totalPower ' Вт')  ($totalPowerKwText)" }
        acceptedText = Format-Metric $script:SharesAcc; rejectedText = Format-Metric $script:SharesRej; poolText = $poolStr
        gpu0HashText = "$(Format-Metric $script:Gpu0Hash ' TH/s')"; gpu0HashRaw = $script:Gpu0Hash
        gpu0TempText = Format-Metric $script:Gpu0Temp ' °C'; gpu0VramTempText = Format-Metric $g0.vramTempC ' °C'; gpu0PowerText = Format-Metric $script:Gpu0Power ' Вт'
        gpu0FanText = Format-Metric $script:Gpu0Fan ' %'; gpu0ClocksText = "$(Format-Metric $script:Gpu0CClkNow)/$(Format-Metric $script:Gpu0MClkNow) MHz"
        gpu0SharesText = "$(Format-Metric $g0.acceptedShares) / $(Format-Metric $g0.rejectedShares)"
        gpu1HashText = "$(Format-Metric $script:Gpu1Hash ' TH/s')"; gpu1HashRaw = $script:Gpu1Hash
        gpu1TempText = Format-Metric $script:Gpu1Temp ' °C'; gpu1VramTempText = Format-Metric $g1.vramTempC ' °C'; gpu1PowerText = Format-Metric $script:Gpu1Power ' Вт'
        gpu1FanText = Format-Metric $script:Gpu1Fan ' %'; gpu1ClocksText = "$(Format-Metric $script:Gpu1CClkNow)/$(Format-Metric $script:Gpu1MClkNow) MHz"
        gpu1SharesText = "$(Format-Metric $g1.acceptedShares) / $(Format-Metric $g1.rejectedShares)"
        energyText = "$(Format-Metric $script:TotalGpuKWh ' кВт·ч')"
        energyCostText = if ($null -eq $energyCost) { "введите тариф" } else { "$(Format-Metric $energyCost ' ₽')" }
        processText = $procStr
    }
    $lblTelemetryBox.Invalidate()
    $poolFreshText = if ($script:PoolBalanceFetchedAt -and ((Get-Date) - $script:PoolBalanceFetchedAt).TotalSeconds -le 120) { $script:PoolBalanceFetchedAt.ToString('HH:mm:ss') } elseif ($script:PoolBalanceFetchedAt) { "устарели · $($script:PoolBalanceFetchedAt.ToString('HH:mm:ss'))" } else { "нет данных" }
    $rubBalance = if ($null -ne $script:PrlRub -and $null -ne $script:PoolBalance) { "$(Format-Metric ($script:PoolBalance * $script:PrlRub) ' ₽')" } else { "нет свежего курса" }
    $rubConfirmed = if ($null -ne $script:PrlRub -and $null -ne $script:PoolConfirmed) { "$(Format-Metric ($script:PoolConfirmed * $script:PrlRub) ' ₽')" } else { "нет свежего курса" }
    $lblEarningsPanel.Text = "ФАКТИЧЕСКИЕ ДАННЫЕ KRYPTEX · РУБЛИ ПО РЫНОЧНОМУ КУРСУ`r`nПодтверждено: $(Format-Metric $script:PoolConfirmed ' PRL')  ($rubConfirmed)`r`nОжидает подтверждения: $(Format-Metric $script:PoolPending ' PRL')`r`nВсего: $(Format-Metric $script:PoolBalance ' PRL')  ($rubBalance) · выплачено: $(Format-Metric $script:PoolPaid ' PRL')`r`nПорог выплаты: $(Format-Metric $script:PayoutThreshold ' PRL') · PRL/RUB: $(Format-Metric $script:PrlRub ' ₽') · $poolFreshText"
    $powerCurrent = if ($null -eq $totalPower) { "нет данных" } else { "$(Format-Metric $totalPower ' Вт')  ($totalPowerKwText)" }
    $powerCost = if ($null -eq $energyCost) { "укажите тариф" } else { "$(Format-Metric $energyCost ' ₽')" }
    $lblPowerPanel.Text = "ФАКТИЧЕСКОЕ ПОТРЕБЛЕНИЕ GPU · nvidia-smi`r`nСейчас всего: $powerCurrent · GPU0: $(Format-Metric $script:Gpu0Power ' Вт') · GPU1: $(Format-Metric $script:Gpu1Power ' Вт')`r`nНакоплено с этой панелью: $(Format-Metric $script:TotalGpuKWh ' кВт·ч')`r`nСтоимость по введённому тарифу: $powerCost · тариф: $(Format-Metric $script:ElectricityTariff ' ₽/кВт·ч')`r`nПотребление системы/розетки не измеряется этими датчиками."
    Update-TrayStatus
}

function Poll-RealTelemetry([switch]$Force, [switch]$SkipExternal) {
    $api = $null
    $nvLines = @()
    $proc = Get-Process -Name 'onezerominer' -ErrorAction SilentlyContinue | Select-Object -First 1
    $now = Get-Date
    # Do not block the WinForms UI every timer tick waiting for an API that cannot
    # exist while the miner is stopped. Back off briefly after an API timeout too.
    if ($proc -and ($script:ApiAvailable -or $now -ge $script:ApiRetryAfter)) {
        try {
            $api = Invoke-RestMethod -Uri "http://127.0.0.1:$ApiPort/" -TimeoutSec 1 -ErrorAction Stop
            $script:ApiRetryAfter = [DateTime]::MinValue
        } catch {
            $script:ApiRetryAfter = $now.AddSeconds(15)
        }
    }
    try {
        $nvLines = @(& nvidia-smi --query-gpu=index,pci.bus_id,temperature.gpu,power.draw,fan.speed,clocks.current.graphics,clocks.current.memory --format=csv,noheader,nounits 2>$null)
        if ($LASTEXITCODE -ne 0) { $nvLines = @() }
    } catch { $nvLines = @() }
    $script:TelemetrySnapshot = Convert-PearlTelemetry $api $nvLines
    $sample = $script:TelemetrySnapshot
    $script:ApiAvailable = $sample.apiAvailable
    $script:NvmlAvailable = $sample.nvmlAvailable
    $script:PoolConnected = $sample.poolConnected
    $script:ActivePoolUrl = $sample.poolUrl
    $script:UptimeSec = $sample.uptimeSec
    $script:TotalHash = $sample.totalHashrateTHs
    $script:SharesAcc = $sample.acceptedShares
    $script:SharesRej = $sample.rejectedShares
    foreach ($id in @(0,1)) {
        $gpu = $sample.gpus[$id]
        foreach ($mapping in @(@('Hash','hashrateTHs'),@('Temp','tempC'),@('Power','powerWatts'),@('Fan','fanPercent'),@('CClkNow','cclkMHz'),@('MClkNow','mclkMHz'))) {
            Set-Variable -Scope Script -Name ("Gpu"+$id+$mapping[0]) -Value $gpu[$mapping[1]]
        }
    }
    if (-not $SkipExternal) {
        if ($Force -or -not $script:PoolBalanceFetchedAt -or (($now - $script:PoolBalanceFetchedAt).TotalSeconds -ge 60)) { Update-PoolBalance }
        if ($Force -or -not $script:MarketRatesFetchedAt -or (($now - $script:MarketRatesFetchedAt).TotalMinutes -ge 15)) { Update-OfficialRates }
    }
    $currentWatts = if ($null -ne $script:Gpu0Power -and $null -ne $script:Gpu1Power) { [double]$script:Gpu0Power + [double]$script:Gpu1Power } else { $null }
    if ($null -ne $currentWatts) {
        if ($script:LastPowerSampleAt) {
            $elapsed = ($now - $script:LastPowerSampleAt).TotalSeconds
            if ($elapsed -gt 0 -and $elapsed -le 30 -and $null -ne $script:LastPowerSampleWatts) {
                $averageWatts = ($currentWatts + $script:LastPowerSampleWatts) / 2.0
                $script:TotalGpuKWh += ($averageWatts * $elapsed) / 3600000.0
                if (-not $script:LastEnergySaveAt -or (($now - $script:LastEnergySaveAt).TotalSeconds -ge 60)) {
                    Save-LocalRigSettings
                    $script:LastEnergySaveAt = $now
                }
            }
        }
        $script:LastPowerSampleAt = $now
        $script:LastPowerSampleWatts = $currentWatts
    } else { $script:LastPowerSampleAt = $null; $script:LastPowerSampleWatts = $null }
    $script:IsMiningRunning = $null -ne $proc
    $script:MinerPid = if ($proc) { $proc.Id } else { $null }
    Update-TelemetryDisplay
}

function Stop-ElevatedMinerProcess([int]$ProcessId) {
    if ($ProcessId -le 0) { return }
    $existing = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $existing) { return }
    if ($existing.ProcessName -ine 'onezerominer') { throw "Refusing to signal unexpected process ID $ProcessId." }
    $command = @'
$ErrorActionPreference = 'Stop'
$targetId = __TARGET_PID__
$target = Get-Process -Id $targetId -ErrorAction SilentlyContinue
if (-not $target) { exit 0 }
if ($target.ProcessName -ine 'onezerominer') { exit 31 }
$nativeSource = @"
using System;
using System.Runtime.InteropServices;
public static class PearlConsoleControl {
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
}
"@
Add-Type -TypeDefinition $nativeSource -ErrorAction SilentlyContinue
[void][PearlConsoleControl]::FreeConsole()
if (-not [PearlConsoleControl]::AttachConsole([uint32]$targetId)) { exit 32 }
[void][PearlConsoleControl]::SetConsoleCtrlHandler([IntPtr]::Zero, $true)
$sent = [PearlConsoleControl]::GenerateConsoleCtrlEvent(0, 0)
[void][PearlConsoleControl]::FreeConsole()
if (-not $sent) { exit 33 }
if ($target.WaitForExit(12000)) { exit 0 }
exit 34
'@
    $command = $command.Replace('__TARGET_PID__', [string]$ProcessId)
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $powershellExe = Join-Path $PSHOME 'powershell.exe'
    $stopper = Start-Process -FilePath $powershellExe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded) -Verb RunAs -WindowStyle Hidden -PassThru -Wait -ErrorAction Stop
    if ($stopper.ExitCode -ne 0) { throw "OneZeroMiner (PID $ProcessId) did not shut down after Ctrl+C (code $($stopper.ExitCode))." }
}

function Start-MinerProcess([bool]$IsWatchdogRestart = $false) {
    if ($ReadOnly) { return }
    $chk = Test-PearlWalletFormat $script:Wallet
    if (-not $script:WalletSaved -or -not $chk.Valid) {
        $lblStatus.Text = "Статус: ЗАПУСК ЗАБЛОКИРОВАН — сначала введите и сохраните корректный адрес Pearl!"
        $lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(248, 113, 113)
        Write-AgentLog "ERROR" "Запуск отклонён: публичный адрес кошелька пуст, неверен или не сохранён локально."
        return
    }

    $existing = Get-Process -Name "onezerominer" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($existing -and (-not $IsWatchdogRestart)) {
        $script:MiningDesired = $true
        $script:UserStoppedManually = $false
        $script:IsMiningRunning = $true
        $script:MinerPid = $existing.Id
        $lblStatus.Text = "Статус: OneZeroMiner уже работает (PID $($existing.Id)). Дубликат не запущен."
        $lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(52, 211, 153)
        Write-AgentLog "INFO" "Процесс onezerominer.exe (PID $($existing.Id)) уже запущен. Подключён мониторинг без остановки процесса."
        Poll-RealTelemetry
        return
    }

    if ($existing -and $IsWatchdogRestart) {
        try { Stop-ElevatedMinerProcess -ProcessId $existing.Id }
        catch {
            $lblStatus.Text = "Статус: перезапуск отложен — Windows не подтвердила остановку майнера."
            Write-AgentLog "ERROR" "Watchdog не смог остановить процесс $($existing.Id): $($_.Exception.Message)"
            return
        }
        Start-Sleep -Milliseconds 600
    }

    if (-not (Test-Path -LiteralPath (Join-Path $MinerDir 'onezerominer.exe'))) {
        $picker = New-Object System.Windows.Forms.OpenFileDialog
        $picker.Title = "Выберите onezerominer.exe"
        $picker.Filter = "OneZeroMiner (onezerominer.exe)|onezerominer.exe|Исполняемые файлы (*.exe)|*.exe"
        $picker.CheckFileExists = $true
        if ($picker.ShowDialog($form) -ne [System.Windows.Forms.DialogResult]::OK -or [IO.Path]::GetFileName($picker.FileName) -ine 'onezerominer.exe') {
            $lblStatus.Text = "Статус: запуск отменён — выберите файл onezerominer.exe"
            Write-AgentLog "WARN" "Запуск отменён: корректный onezerominer.exe не выбран."
            return
        }
        $MinerDir = Split-Path -LiteralPath $picker.FileName -Parent
        Save-LocalRigSettings
        Write-AgentLog "INFO" "Путь майнера сохранён локально: $MinerDir"
    }

    $logDir = Split-Path $LogFile -Parent
    if ($logDir -and (-not (Test-Path $logDir))) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

    Set-Location $MinerDir
    $minerArgs = "-a pearl -w $($script:Wallet) -o $Pool1 --worker $Worker --cclk $CurrentCClk --mclk $CurrentMClk --pl $CurrentPL --kernel $CurrentKernel --fan $CurrentFan --api-port $ApiPort --log-file `"$LogFile`""
    try {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $isElevated = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        if ($script:IsStartupTaskLaunch -and -not $isElevated) { throw "Автозадача не запущена с правами администратора; для безопасности майнер не запущен." }
        if ($isElevated) {
            $lblStatus.Text = "Статус: запуск майнера в фоне..."
            $started = Start-Process -FilePath "$MinerDir\onezerominer.exe" -ArgumentList $minerArgs -WorkingDirectory $MinerDir -WindowStyle Hidden -PassThru -ErrorAction Stop
        } else {
            $lblStatus.Text = "Статус: ожидается разрешение Windows для настройки GPU..."
            Write-AgentLog "INFO" "Запрашивается разрешение Windows для запуска OneZeroMiner с настройками GPU."
            $started = Start-Process -FilePath "$MinerDir\onezerominer.exe" -ArgumentList $minerArgs -WorkingDirectory $MinerDir -WindowStyle Normal -Verb RunAs -PassThru -ErrorAction Stop
        }
    } catch {
        $script:MiningDesired = $false
        $script:IsMiningRunning = $false
        $script:MinerPid = $null
        $lblStatus.Text = "Статус: запуск отменён — Windows не разрешила применить настройки GPU."
        $lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(251, 191, 36)
        Write-AgentLog "WARN" "Запуск OneZeroMiner отменён или отклонён Windows: $($_.Exception.Message)"
        return
    }
    $script:MiningDesired = $true
    $script:UserStoppedManually = $false
    $script:ThermalThrottled = $false
    $script:IsMiningRunning = $true
    $script:MinerPid = if ($started) { $started.Id } else { $null }

    $lblStatus.Text = "Статус: МАЙНЕР ЗАПУЩЕН (PID $($script:MinerPid)) -> $Pool1"
    $lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(52, 211, 153)
    Write-AgentLog "OK" "Запущен onezerominer.exe (PID $($script:MinerPid)): -w $($script:Wallet) -o $Pool1 --cclk $CurrentCClk --mclk $CurrentMClk --pl $CurrentPL --kernel $CurrentKernel --fan $CurrentFan"
    Poll-RealTelemetry
}

function Stop-MinerProcess([string]$Reason = "Нажата кнопка «Стоп» пользователем") {
    if ($ReadOnly) { return }
    # Latch the user's intent before signaling the miner so the watchdog cannot restart it.
    $script:UserStoppedManually = $true
    $script:MiningDesired = $false
    $script:ReconnectStage = "idle"
    $script:DisconnectElapsedSec = 0
    $processes = @(Get-Process -Name "onezerominer" -ErrorAction SilentlyContinue)
    try {
        foreach ($process in $processes) { Stop-ElevatedMinerProcess -ProcessId $process.Id }
        Start-Sleep -Milliseconds 300
        $remaining = @(Get-Process -Name "onezerominer" -ErrorAction SilentlyContinue)
        if ($remaining.Count -gt 0) { throw "OneZeroMiner is still running (PID $($remaining[0].Id))." }
    } catch {
        $script:IsMiningRunning = $true
        $script:MinerPid = if ($remaining -and $remaining.Count -gt 0) { $remaining[0].Id } elseif ($processes.Count -gt 0) { $processes[0].Id } else { $null }
        $lblStatus.Text = "Статус: майнер не остановился после Ctrl+C. Откройте его окно и нажмите Ctrl+C."
        $lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(248, 113, 113)
        Write-AgentLog "ERROR" "Штатная остановка OneZeroMiner по Ctrl+C не подтверждена: $($_.Exception.Message)"
        return
    }
    $script:IsMiningRunning = $false
    $script:MinerPid = $null

    $lblStatus.Text = "Статус: ОСТАНОВЛЕН ($Reason). Watchdog не будет перезапускать майнер."
    $lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(251, 191, 36)
    Write-AgentLog "WARN" "Майнинг остановлен ($Reason). Автоматический перезапуск отключён до явной команды «Запустить»."
    Poll-RealTelemetry
}

function Test-PoolTcpConnection {
    foreach ($endpoint in @($Pool1, $Pool2)) {
        $clean = ($endpoint -replace "^stratum\+(tcp|ssl)://", "")
        $parts = $clean -split ":"
        $hostName = $parts[0]
        $port = if ($parts.Count -ge 2) { [int]$parts[1] } else { 7048 }
        try {
            $tcp = New-Object System.Net.Sockets.TcpClient
            $iar = $tcp.BeginConnect($hostName, $port, $null, $null)
            if ($iar.AsyncWaitHandle.WaitOne(1200, $false) -and $tcp.Connected) {
                $tcp.Close()
                return $true
            }
            $tcp.Close()
        } catch {}
    }
    return $false
}

function Invoke-WatchdogCheck {
    if ($ReadOnly -or -not $script:MiningDesired -or $script:UserStoppedManually) { return }
    $maxTempNow = $null
    if ($null -ne $script:Gpu0Temp -and $null -ne $script:Gpu1Temp) {
        $maxTempNow = [Math]::Max($script:Gpu0Temp, $script:Gpu1Temp)
    } elseif ($null -ne $script:Gpu0Temp) {
        $maxTempNow = $script:Gpu0Temp
    } elseif ($null -ne $script:Gpu1Temp) {
        $maxTempNow = $script:Gpu1Temp
    }

    if ($null -ne $maxTempNow -and $maxTempNow -ge $MaxTemp) {
        if ($maxTempNow -ge $CriticalStopTemp -or $script:ThermalThrottled) {
            Write-AgentLog "THERMAL" "КРИТИЧЕСКИЙ ПЕРЕГРЕВ GPU ($maxTempNow °C >= $MaxTemp °C)! Немедленная остановка майнера."
            Stop-MinerProcess "Аварийная остановка из-за перегрева GPU ($maxTempNow °C)"
            return
        } else {
            $script:ThermalThrottled = $true
            $script:CurrentPL = "$EmergencyPL,$EmergencyPL"
            & nvidia-smi -i 0 -pl $EmergencyPL >$null 2>&1
            & nvidia-smi -i 1 -pl $EmergencyPL >$null 2>&1
            Write-AgentLog "THERMAL" "Перегрев GPU ($maxTempNow °C >= $MaxTemp °C): лимит мощности снижен до $EmergencyPL Вт на обеих картах."
            $lblStatus.Text = "Статус: ЗАЩИТА ОТ ПЕРЕГРЕВА ($maxTempNow °C) — Power Limit снижен до $EmergencyPL Вт"
            $lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(248, 113, 113)
        }
    }

    if (-not $script:MiningDesired -or $script:UserStoppedManually) {
        return
    }

    if ($script:ReconnectStage -eq "awaiting_confirmation") {
        $hasConnected = ($script:PoolConnected -eq $true)
        $hasHashrate = ($null -ne $script:TotalHash -and $script:TotalHash -gt 0)
        $hasNewShare = ($null -ne $script:SharesAcc -and $script:SharesAcc -gt $script:SharesAtRestart)
        if ($hasConnected -and $hasHashrate -and $hasNewShare) {
            $script:RecoveryConfirmed = $true
            $script:ReconnectStage = "idle"
            $script:RestartCount = 0
            $script:DisconnectElapsedSec = 0
            Write-AgentLog "OK" "Восстановление подтверждено: пул Connected, хешрейт $($script:TotalHash) TH/s, принята новая шара ($($script:SharesAcc) > $($script:SharesAtRestart))."
            $lblStatus.Text = "Статус: МАЙНИНГ ВОССТАНОВЛЕН И ПОДТВЕРЖДЁН ($($script:TotalHash) TH/s)"
            $lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(52, 211, 153)
        }
    }

    $poolHealthy = ($script:IsMiningRunning -and $script:ApiAvailable -and ($script:PoolConnected -eq $true) -and ($null -ne $script:TotalHash) -and ($script:TotalHash -gt 0))

    if ($poolHealthy) {
        $script:DisconnectElapsedSec = 0
        if ($script:ReconnectStage -eq "native_wait") {
            $script:ReconnectStage = "idle"
            Write-AgentLog "OK" "OneZeroMiner самостоятельно переподключился к пулу Kryptex."
        }
        return
    }

    $script:DisconnectElapsedSec += $CheckInterval
    if ($script:DisconnectElapsedSec -lt $ZeroHashLimitSec) {
        $script:ReconnectStage = "native_wait"
        Write-AgentLog "WARN" "Связь с пулом потеряна или хешрейт 0. Ожидание штатного переподключения OneZeroMiner ($($script:DisconnectElapsedSec)/$ZeroHashLimitSec сек)..."
        return
    }

    $script:DisconnectElapsedSec = 0
    if ($script:RestartCount -ge $MaxRestartAttempts) {
        $script:ReconnectStage = "limit_reached"
        $lblStatus.Text = "Статус: Достигнут лимит перезапусков ($MaxRestartAttempts/$MaxRestartAttempts). Проверьте соединение с пулом."
        $lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(248, 113, 113)
        Write-AgentLog "ERROR" "Превышен лимит попыток перезапуска ($MaxRestartAttempts). Автоперезапуск приостановлен до вмешательства пользователя."
        return
    }

    $netOk = Test-PoolTcpConnection
    if (-not $netOk) {
        Write-AgentLog "WARN" "Сеть/пул $Pool1 сейчас не отвечает по TCP. Попытка перезапуска отложена до восстановления сети."
        return
    }

    $script:RestartCount++
    $script:ReconnectStage = "awaiting_confirmation"
    $script:RecoveryConfirmed = $false
    $script:SharesAtRestart = 0 # New miner session resets accepted shares.
    Write-AgentLog "WARN" "Устойчивый сбой пула. Сеть доступна. Перезапуск майнера (попытка $($script:RestartCount) из $MaxRestartAttempts)..."
    Start-MinerProcess -IsWatchdogRestart $true
}

$httpListener = New-Object System.Net.HttpListener
$httpListener.Prefixes.Add("http://127.0.0.1:$BridgePort/")
$script:httpTask = $null
try {
    $httpListener.Start()
    $script:httpTask = $httpListener.GetContextAsync()
    Write-AgentLog "OK" "Защищённый локальный интерфейс телеметрии запущен на http://127.0.0.1:$BridgePort/telemetry (только чтение)"
} catch {}

$btnCheckWallet.Add_Click({
    $chk = Test-PearlWalletFormat $txtWallet.Text
    if ($chk.Valid) {
        $lblWalletHint.ForeColor = [System.Drawing.Color]::FromArgb(52, 211, 153)
        $lblWalletHint.Text = $chk.Reason
    } else {
        $lblWalletHint.ForeColor = [System.Drawing.Color]::FromArgb(248, 113, 113)
        $lblWalletHint.Text = $chk.Reason
    }
})

$btnSaveWallet.Add_Click({
    $candidate = $txtWallet.Text.Trim()
    $chk = Test-PearlWalletFormat $candidate
    if (-not $chk.Valid) {
        [System.Windows.Forms.MessageBox]::Show($chk.Reason, "Ошибка формата адреса Pearl", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
        Sync-WalletUiState
        return
    }
    $script:Wallet = $candidate
    $script:WalletSaved = $true
    $script:WalletEditMode = $false
    $payload = @{ walletAddress = $script:Wallet; savedAt = (Get-Date).ToString("o") } | ConvertTo-Json
    [System.IO.File]::WriteAllText($LocalWalletFile, $payload, [System.Text.Encoding]::UTF8)
    Write-AgentLog "OK" "Публичный адрес кошелька проверен и сохранён локально ($($script:Wallet.Length) симв.). Теперь можно нажать «▶ ЗАПУСТИТЬ»."
    Sync-WalletUiState
    Poll-RealTelemetry -Force
})

$btnEditWallet.Add_Click({
    $confirm = [System.Windows.Forms.MessageBox]::Show("Разблокировать поле для изменения публичного адреса выплат Pearl?", "Подтверждение изменения адреса", [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question)
    if ($confirm -eq [System.Windows.Forms.DialogResult]::Yes) {
        $script:WalletEditMode = $true
        $script:WalletSaved = $false
        Sync-WalletUiState
        $txtWallet.Focus()
    }
})

$btnStart.Add_Click({ Start-MinerProcess -IsWatchdogRestart $false })
$btnStop.Add_Click({ Stop-MinerProcess "Явная остановка пользователем (кнопка Стоп)" })
$btnAutoMode.Add_Click({
    try { Set-AutoStartMining -Enable (-not $script:AutoStartMining) }
    catch {
        Write-AgentLog "ERROR" "Не удалось изменить режим автозапуска: $($_.Exception.Message)"
        [System.Windows.Forms.MessageBox]::Show("Не удалось изменить режим автозапуска.`r`n`r`n$($_.Exception.Message)", "Автозапуск Pearl Rig Control", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    }
})
$script:TrayOpenItem.Add_Click({
    Show-RigPanelFromTray
})
$script:TrayIcon.Add_DoubleClick({ Show-RigPanelFromTray })
$script:TrayStartItem.Add_Click({ Start-MinerProcess -IsWatchdogRestart $false })
$script:TrayStopItem.Add_Click({ Stop-MinerProcess "Остановка через значок области уведомлений" })
$script:TrayExitItem.Add_Click({
    if ($script:IsMiningRunning) { Stop-MinerProcess "Остановка перед закрытием из области уведомлений" }
    if ($script:IsMiningRunning) { return }
    $script:TrayExitRequested = $true
    $form.Close()
})
$btnRefreshNow.Add_Click({ Poll-RealTelemetry -Force })
$btnApplyGpuPower.Add_Click({
    $btnApplyGpuPower.Enabled = $false
    try {
        Apply-GpuPowerLimit -Watts $trackGpuPower.Value
    } catch {
        $lblStatus.Text = "Статус: не удалось применить лимит GPU. Разрешите запрос Windows и проверьте драйвер."
        $lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(248, 113, 113)
        Write-AgentLog "ERROR" "Лимит мощности GPU не применён: $($_.Exception.Message)"
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, "Не удалось применить лимит мощности", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    } finally { $btnApplyGpuPower.Enabled = $script:GpuPowerRangeVerified -and (-not $ReadOnly) }
})

$btnSaveTariff.Add_Click({
    $rawTariff = $txtTariff.Text.Trim().Replace(',', '.')
    $parsedTariff = 0.0
    if (-not [double]::TryParse($rawTariff, [Globalization.NumberStyles]::AllowDecimalPoint, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsedTariff) -or $parsedTariff -lt 0) {
        [System.Windows.Forms.MessageBox]::Show("Введите неотрицательный тариф в ₽/кВт·ч.", "Проверка тарифа", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
        return
    }
    $script:ElectricityTariff = $parsedTariff
    Save-LocalRigSettings
    Write-AgentLog "INFO" "Тариф электроэнергии сохранён локально: $parsedTariff ₽/кВт·ч."
    Update-TelemetryDisplay
})

$form.Add_FormClosing({
    if ($script:IsStartupTaskLaunch -and $_.CloseReason -eq [System.Windows.Forms.CloseReason]::UserClosing -and -not $script:TrayExitRequested) {
        $_.Cancel = $true
        $form.Hide()
        $form.ShowInTaskbar = $false
        return
    }
    try { Save-LocalRigSettings } catch {}
    if ($script:TrayIcon) { $script:TrayIcon.Visible = $false }
})

# Проверка и скачивание обновлений нативной Windows-программы из GitHub Releases.
$btnCheckUpdate.Add_Click({
    if ($script:DownloadActive) { return }
    if ($script:UpdateCheckActive) { return }
    if ($script:UpdateAvailable -and $script:LatestReleaseUrl) {
        Start-AgentUpdateDownload
        return
    }
    Test-UpdateAvailable
})

$existingOnOpen = Get-Process -Name "onezerominer" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($existingOnOpen) {
    $script:IsMiningRunning = $true
    $script:MiningDesired = $false
    $script:MinerPid = $existingOnOpen.Id
    $lblStatus.Text = "Статус: Обнаружен уже работающий OneZeroMiner (PID $($existingOnOpen.Id)). Мониторинг подключён."
    $lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(52, 211, 153)
    Write-AgentLog "INFO" "При открытии обнаружен работающий процесс onezerominer.exe (PID $($existingOnOpen.Id)). Остановка или дублирующий запуск не выполнялись."
} else {
    Write-AgentLog "INFO" "Панель открыта. Автозапуск майнера отключён. Проверьте сохранённый адрес кошелька и нажмите «▶ ЗАПУСТИТЬ»."
}

Sync-WalletUiState
if ($ReadOnly) {
    $form.Text += " - только мониторинг"
    $btnStart.Enabled = $false
    $btnStop.Enabled = $false
    $btnSaveWallet.Enabled = $false
    $btnEditWallet.Enabled = $false
    $trackGpuPower.Enabled = $false
    $btnApplyGpuPower.Enabled = $false
    $txtWallet.ReadOnly = $true
    $lblWalletHint.Text = "Режим мониторинга: настройки и работа майнера не изменяются."
}
Poll-RealTelemetry -SkipExternal
$lblVersion.Text = "Установлена: v$AppVersion"
$lblUpdateInfo.Text = "Нажмите «ПРОВЕРИТЬ ОБНОВЛЕНИЯ», чтобы проверить новую версию."

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 2000
$tickSec = 0

$timer.Add_Tick({
    if ($script:UpdateCheckWork -and $script:UpdateCheckWork.Async.IsCompleted) { Complete-UpdateCheck }
    if ($script:UpdateDownloadWork) {
        $progress = $script:DownloadProgressState
        if ($progress -and $progress.Total -gt 0) {
            $pct = [Math]::Max(1, [Math]::Min(99, [int](($progress.Done * 100.0) / $progress.Total)))
            $pbarDownload.Value = $pct
            $script:DownloadProgress = $pct
            $lblUpdateInfo.Text = "Загрузка v$($progress.Version)... $pct% ($([Math]::Round($progress.Done/1MB, 1)) из $([Math]::Round($progress.Total/1MB, 1)) МБ)"
        } elseif ($progress) {
            $lblUpdateInfo.Text = "Загрузка v$($progress.Version)... $([Math]::Round($progress.Done/1MB, 1)) МБ"
        }
        if ($script:UpdateDownloadWork.Async.IsCompleted) { Complete-UpdateDownload }
    }
    Poll-RealTelemetry
    if ($script:ShowPanelEvent -and $script:ShowPanelEvent.WaitOne(0)) { Show-RigPanelFromTray }

    if ($httpListener.IsListening) {
        try {
            if ($null -eq $script:httpTask) {
                $script:httpTask = $httpListener.GetContextAsync()
            }
            if ($script:httpTask.IsCompleted) {
                $ctx = $script:httpTask.GetAwaiter().GetResult()
                $script:httpTask = $httpListener.GetContextAsync()
                $req = $ctx.Request
                $res = $ctx.Response

                $origin = $req.Headers["Origin"]
                $allowedOrigins = @("http://127.0.0.1:3000", "http://localhost:3000")
                if ($origin -and ($allowedOrigins -notcontains $origin)) {
                    $res.StatusCode = 403
                    $res.Close()
                } else {
                    if ($origin) {
                        $res.Headers.Add("Access-Control-Allow-Origin", $origin)
                        $res.Headers.Add("Vary", "Origin")
                    }
                    $path = $req.Url.AbsolutePath
                    if ($req.HttpMethod -eq "GET" -and ($path -eq "/telemetry" -or $path -eq "/status")) {
                        $payload = $script:TelemetrySnapshot.Clone()
                        $payload.minerProcessRunning = $script:IsMiningRunning
                        $payload.minerPid = $script:MinerPid
                        $payload.miningDesired = $script:MiningDesired
                        $payload.userStoppedManually = $script:UserStoppedManually
                        $payload.reconnectStage = $script:ReconnectStage
                        $payload.restartAttempts = $script:RestartCount
                        $payload.sharesAtRestart = $script:SharesAtRestart
                        $payload.recoveryConfirmed = $script:RecoveryConfirmed
                        $telemetryJson = $payload | ConvertTo-Json -Depth 5
                        $buf = [System.Text.Encoding]::UTF8.GetBytes($telemetryJson)
                        $res.StatusCode = 200
                        $res.Headers["Cache-Control"] = "no-store"
                        $res.ContentType = "application/json; charset=utf-8"
                        $res.ContentLength64 = $buf.Length
                        $res.OutputStream.Write($buf, 0, $buf.Length)
                        $res.Close()
                    } else {
                        $errJson = @{ ok = $false; error = "Команды управления через GET и внешняя смена кошелька заблокированы политикой безопасности." } | ConvertTo-Json
                        $buf = [System.Text.Encoding]::UTF8.GetBytes($errJson)
                        $res.StatusCode = 405
                        $res.Headers["Cache-Control"] = "no-store"
                        $res.ContentType = "application/json; charset=utf-8"
                        $res.ContentLength64 = $buf.Length
                        $res.OutputStream.Write($buf, 0, $buf.Length)
                        $res.Close()
                    }
                }
            }
        } catch {
            try { $script:httpTask = $httpListener.GetContextAsync() } catch {}
        }
    }

    $script:tickSec += 2
    if ($script:tickSec -ge $CheckInterval) {
        $script:tickSec = 0
        Invoke-WatchdogCheck
    }
})

$form.Add_Shown({
    if ($script:IsStartupTaskLaunch) {
        $form.ShowInTaskbar = $false
        $form.Hide()
    } else {
        $form.Opacity = 1
        $form.Activate()
    }
    $timer.Start()
    Test-UpdateAvailable
    if ($AutoMine) {
        Update-TrayStatus
        Start-MinerProcess -IsWatchdogRestart $false
    }
})

[void]$form.ShowDialog()
$timer.Stop()
if ($httpListener.IsListening) { $httpListener.Stop() }
if ($script:TrayIcon) { $script:TrayIcon.Visible = $false; $script:TrayIcon.Dispose() }
if ($script:TrayMenu) { $script:TrayMenu.Dispose() }
if ($tipAutoMode) { $tipAutoMode.Dispose() }
if ($script:InstanceMutex) {
    try { $script:InstanceMutex.ReleaseMutex() } catch {}
    $script:InstanceMutex.Dispose()
}
if ($script:ShowPanelEvent) { $script:ShowPanelEvent.Dispose() }
