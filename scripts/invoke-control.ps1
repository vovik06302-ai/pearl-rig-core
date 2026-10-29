param([Parameter(Mandatory=$true)][string]$RequestPath,[Parameter(Mandatory=$true)][string]$ResultPath,[Parameter(Mandatory=$true)][string]$ConfigPath)
$ErrorActionPreference = 'Stop'

function Test-ControlProfile($Gpus) {
    if (@($Gpus).Count -ne 2) { throw 'Нужны настройки двух GPU' }
    foreach ($id in @(0,1)) {
        $g = $Gpus[$id]
        if ($g.id -ne $id -or $g.pl -ne [int]$g.pl -or $g.pl -lt 100 -or $g.pl -gt 150) { throw 'Недопустимый лимит мощности GPU' }
        if ($g.cclk -ne [int]$g.cclk -or $g.cclk -lt 1800 -or $g.cclk -gt 2400 -or $g.mclk -ne 810 -or $g.kernel -ne 2) { throw 'Недопустимые частоты или kernel' }
        if ([string]$g.fan -ne '*' -and ([string]$g.fan -notmatch '^\d{2,3}$' -or [int]$g.fan -lt 45 -or [int]$g.fan -gt 100)) { throw 'Недопустимая скорость вентилятора' }
    }
}
function Get-MinerArgument([string]$Command, [string]$Name) {
    $pattern = '(?:^|\s)' + [regex]::Escape($Name) + '\s+(?:"([^"]*)"|([^\s]+))'
    if ($Command -match $pattern) { if ($Matches[1]) { return $Matches[1] }; return $Matches[2] }
    return $null
}
function Get-ManagedMiner($Config) {
    $all = @(Get-CimInstance Win32_Process -Filter "name='onezerominer.exe'")
    if (-not $all.Count) { return @{ Pids=@(); Root=$null } }
    $exe = [IO.Path]::GetFullPath($Config.minerExe)
    if (@($all | Where-Object { -not $_.ExecutablePath -or [IO.Path]::GetFullPath($_.ExecutablePath) -ne $exe }).Count) { throw 'Обнаружен другой OneZeroMiner. Управление заблокировано.' }
    $roots = @($all | Where-Object { $_.ParentProcessId -notin $all.ProcessId })
    if ($roots.Count -ne 1) { throw 'Обнаружено несколько запусков майнера. Управление заблокировано.' }
    $root = $roots[0]
    if ((Get-MinerArgument $root.CommandLine '-w') -cne $Config.wallet -or
        (Get-MinerArgument $root.CommandLine '-o') -ne $Config.pool -or
        (Get-MinerArgument $root.CommandLine '-a') -ne 'pearl') { throw 'Кошелёк или пул процесса не совпадает с локальной конфигурацией. Процесс не изменён.' }
    $port = Get-MinerArgument $root.CommandLine '--api-port'
    if ($port -and $port -ne '3001') { throw 'У майнера другой API-порт. Процесс не изменён.' }
    return @{ Pids=@($all.ProcessId); Root=$root }
}
function Get-ControlArgs($Config, $Gpus) {
    Test-ControlProfile $Gpus
    if ($Config.worker -notmatch '^[A-Za-z0-9_-]{1,32}$' -or $Config.logFile -match '["\r\n]') { throw 'Недопустимая конфигурация запуска' }
    $core = ($Gpus | ForEach-Object { [int]$_.cclk }) -join ','
    $memory = ($Gpus | ForEach-Object { [int]$_.mclk }) -join ','
    $power = ($Gpus | ForEach-Object { [int]$_.pl }) -join ','
    $fan = ($Gpus | ForEach-Object { [string]$_.fan }) -join ','
    return "-a pearl -w $($Config.wallet) -o $($Config.pool) --worker $($Config.worker) --cclk $core --mclk $memory --pl $power --kernel 2,2 --fan $fan --api-port 3001 --log-file `"$($Config.logFile)`""
}
function Test-CurrentProfile($Root, $Gpus) {
    if (-not $Root) { return $false }
    foreach ($pair in @(@('--cclk','cclk'),@('--mclk','mclk'),@('--pl','pl'),@('--fan','fan'),@('--kernel','kernel'))) {
        $wanted = ($Gpus | ForEach-Object { $_.($pair[1]) }) -join ','
        if ((Get-MinerArgument $Root.CommandLine $pair[0]) -ne $wanted) { return $false }
    }
    return $true
}
function Test-HardwareProfile($Gpus) {
    $lines = @(& nvidia-smi --query-gpu=index,power.limit,clocks.mem,clocks.gr,fan.speed --format=csv,noheader,nounits 2>$null)
    if ($LASTEXITCODE -ne 0 -or $lines.Count -ne 2) { return $false }
    foreach ($line in $lines) {
        $v = $line -split ','
        if ($v.Count -ne 5) { return $false }
        $id = [int]$v[0].Trim()
        if ($id -notin @(0,1)) { return $false }
        $pl = [double]::Parse($v[1].Trim(),[Globalization.CultureInfo]::InvariantCulture)
        if ([Math]::Abs($pl - $Gpus[$id].pl) -gt 1 -or [int]$v[2].Trim() -ne 810 -or [int]$v[3].Trim() -gt ($Gpus[$id].cclk + 30)) { return $false }
        if ([string]$Gpus[$id].fan -ne '*' -and [Math]::Abs([int]$v[4].Trim() - [int]$Gpus[$id].fan) -gt 2) { return $false }
    }
    return $true
}
function Write-ControlResult($Data) {
    $temp = $ResultPath + '.tmp'
    [IO.File]::WriteAllText($temp,($Data | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temp -Destination $ResultPath -Force
}
function Get-ControlTemperatures {
    $lines = @(& nvidia-smi --query-gpu=temperature.gpu --format=csv,noheader,nounits 2>$null)
    if ($LASTEXITCODE -ne 0 -or $lines.Count -lt 2) { throw 'Невозможно проверить температуры обеих карт' }
    return @($lines | ForEach-Object { [int]$_.Trim() })
}
function Start-ControlMiner($Config, $Gpus) {
    if (@(Get-CimInstance Win32_Process -Filter "name='onezerominer.exe'").Count) { throw 'Майнер уже работает. Дубликат не запущен.' }
    $minerArgs = Get-ControlArgs $Config $Gpus
    return Start-Process -FilePath $Config.minerExe -ArgumentList $minerArgs -WorkingDirectory (Split-Path -LiteralPath $Config.minerExe) -WindowStyle Normal -PassThru -ErrorAction Stop
}

$stoppedExisting = $false
$thermalStop = $false
$newProcess = $null
$controlMutex = [Threading.Mutex]::new($false, 'Local\PearlRigProfileControl')
$mutexOwned = $false
try {
    $mutexOwned = $controlMutex.WaitOne(0)
    if (-not $mutexOwned) { throw 'Другая операция управления ещё выполняется' }
    $config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($config.wallet -cnotmatch '^prl1p[qpzry9x8gf2tvdw0s3jn54khce6mua7l]{58}$' -or $config.pool -ne 'stratum+tcp://prl-ru.kryptex.network:7048') { throw 'Локальная конфигурация выплат не прошла проверку' }
    if (-not (Test-Path -LiteralPath $config.minerExe -PathType Leaf) -or [IO.Path]::GetFileName($config.minerExe) -ne 'onezerominer.exe') { throw 'Файл майнера не найден или указан неверно' }
    $request = Get-Content -LiteralPath $RequestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($request.action -notin @('apply','start','stop','validate')) { throw 'Неизвестное действие' }
    $gpus = if ($request.action -eq 'apply' -or $request.action -eq 'validate') { $request.gpus } else { $config.gpus }
    Test-ControlProfile $gpus
    $family = Get-ManagedMiner $config
    if ($request.action -eq 'stop') {
        if ($family.Pids.Count) { Stop-Process -Id $family.Pids -Force -ErrorAction SilentlyContinue }
        $deadline = (Get-Date).AddSeconds(10)
        while ($family.Pids.Count -gt 0 -and @(Get-Process -Id $family.Pids -ErrorAction SilentlyContinue).Count -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
        if (@(Get-CimInstance Win32_Process -Filter "name='onezerominer.exe'").Count) { throw 'Майнер ещё не завершился' }
        Write-ControlResult @{status='success'; message='Майнер остановлен пользователем'; stopped=$true; applied=$false}
        exit 0
    }
    $temps = Get-ControlTemperatures
    if (@($temps | Where-Object { $_ -ge 85 }).Count) { throw 'Температура >=85°C. Запуск и применение профиля заблокированы; доступна остановка.' }
    $sameProfile = (Test-CurrentProfile $family.Root $gpus) -and (Test-HardwareProfile $gpus)
    if ($request.action -eq 'validate') {
        Write-ControlResult @{status='success'; message='Кошелёк, пул, процессы и диапазоны настроек проверены'; restartRequired=($null -ne $family.Root -and -not $sameProfile); applied=$false; temperatures=$temps}
        exit 0
    }
    if ($family.Root -and $request.action -eq 'start' -and -not $sameProfile) { throw 'Майнер уже работает с другим профилем. Используйте «Применить к ригу».' }
    if ($family.Root -and $sameProfile) {
        $api = Invoke-RestMethod 'http://127.0.0.1:3001/' -TimeoutSec 4
        $algo = $api.algos | Where-Object { $_.name -eq 'pearlhash' } | Select-Object -First 1
        if ([Math]::Abs([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - [double]$api.last_update) -gt 20 -or $algo.pool_status -ne 'Connected' -or @($algo.hashrates | Where-Object { $_ -gt 0 }).Count -lt 2 -or $algo.total_accepted_shares -le 0) { throw 'Профиль уже применён, но подключение и обе карты пока не подтверждены' }
        Write-ControlResult @{status='success'; message='Этот профиль уже действует: обе карты работают, повторный запуск не потребовался'; applied=$true; gpus=$gpus; restarted=$false; verified=$true; accepted=$algo.total_accepted_shares}
        exit 0
    }
    Write-ControlResult @{status='applying'; message='Применение профиля'; applied=$false}
    if ($family.Pids.Count) {
        Stop-Process -Id $family.Pids -Force -ErrorAction SilentlyContinue
        $stoppedExisting = $true
        $deadline = (Get-Date).AddSeconds(10)
        while ($family.Pids.Count -gt 0 -and @(Get-Process -Id $family.Pids -ErrorAction SilentlyContinue).Count -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    }
    $logDir = Split-Path -LiteralPath $config.logFile
    if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
    $newProcess = Start-ControlMiner $config $gpus
    Write-ControlResult @{status='verifying'; message='Майнер запущен. Проверка подключения, обеих карт и первой принятой шары'; applied=$true; gpus=$gpus; restarted=$stoppedExisting}
    $deadline = (Get-Date).AddSeconds(180)
    do {
        Start-Sleep -Seconds 3
        $temps = Get-ControlTemperatures
        if (@($temps | Where-Object { $_ -ge 85 }).Count) {
            $thermalStop = $true
            $hot = Get-ManagedMiner $config
            if ($hot.Pids.Count) { Stop-Process -Id $hot.Pids -Force }
            throw 'Майнер остановлен: температура достигла 85°C'
        }
        try {
            $api = Invoke-RestMethod 'http://127.0.0.1:3001/' -TimeoutSec 3
            $algo = $api.algos | Where-Object { $_.name -eq 'pearlhash' } | Select-Object -First 1
            $fresh = [Math]::Abs([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - [double]$api.last_update) -le 20
            $good = $fresh -and $algo.pool_status -eq 'Connected' -and @($algo.hashrates | Where-Object { $_ -gt 0 }).Count -eq 2
            if ($good -and $algo.total_accepted_shares -gt 0 -and (Test-HardwareProfile $gpus)) {
                Write-ControlResult @{status='success'; message='Настройки применены: пул подключён, обе карты работают, есть новая принятая шара'; applied=$true; gpus=$gpus; restarted=$stoppedExisting; verified=$true; accepted=$algo.total_accepted_shares}
                exit 0
            }
        } catch { $good = $false }
    } while ((Get-Date) -lt $deadline)
    Write-ControlResult @{status='unverified'; message='Профиль применён, но принятая шара пока не подтверждена. Проверьте текущую телеметрию и журнал.'; applied=$true; gpus=$gpus; restarted=$stoppedExisting; verified=$false}
} catch {
    $message = $_.Exception.Message
    if ($stoppedExisting -and -not $newProcess -and -not $thermalStop -and $config) {
        try { [void](Start-ControlMiner $config $config.gpus); $message += '. Восстановлен прежний профиль.' } catch { $message += '. Автовосстановление не удалось: '+$_.Exception.Message }
    }
    Write-ControlResult @{status='failed'; message=$message; applied=$false}
    exit 1
}

finally { if ($mutexOwned) { $controlMutex.ReleaseMutex() }; $controlMutex.Dispose() }
