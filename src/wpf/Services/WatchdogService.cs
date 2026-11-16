using PearlRigCore.Models;

namespace PearlRigCore.Services;

public enum WatchdogActionType
{
    None,
    ThermalEmergencyStop,
    ThermalResume,
    ThermalWarning,
    ZeroHashrateRestart,
    Healthy,
    ZeroHashrateCountdown
}

public sealed record WatchdogDecision(
    WatchdogActionType Action,
    string StatusText,
    string? EventLogMessage = null,
    string? TelegramAlertMessage = null);

/// <summary>Deterministic state machine for GPU thermal protection, zero-hashrate restart, and pool failover hysteresis.</summary>
public sealed class WatchdogService
{
    private DateTimeOffset? _minerStartedOrObservedAt;
    private DateTimeOffset? _zeroHashrateSince;
    private DateTimeOffset _lastThermalWarningAt = DateTimeOffset.MinValue;
    private int _consecutivePrimaryPoolFailures;

    public bool ThermalPaused { get; private set; }
    public int RestartCount { get; private set; }

    public void ResetMinerTimers(bool clearThermalPause = false)
    {
        _minerStartedOrObservedAt = null;
        _zeroHashrateSince = null;
        if (clearThermalPause)
            ThermalPaused = false;
    }

    public void MarkMinerStarted(DateTimeOffset now)
    {
        _minerStartedOrObservedAt = now;
        _zeroHashrateSince = null;
    }

    public bool? EvaluatePoolSwitch(PoolPingResult ping, bool currentlyUsingBackup, RigConfig config)
    {
        if (!config.AutoSwitchToBackupPoolOnDrop)
            return null;

        if (ping.PrimaryPingMs is null && ping.BackupPingMs is not null)
        {
            _consecutivePrimaryPoolFailures++;
            if (!currentlyUsingBackup && _consecutivePrimaryPoolFailures >= config.PoolDropSwitchThresholdCount)
                return true;
        }
        else if (ping.PrimaryPingMs is not null)
        {
            _consecutivePrimaryPoolFailures = 0;
            if (currentlyUsingBackup)
                return false;
        }

        return null;
    }

    public WatchdogDecision Evaluate(
        TelemetrySnapshot snapshot,
        bool isMinerRunning,
        bool watchdogEnabled,
        bool isBusy,
        bool isAutoTuning,
        bool userManuallyStopped,
        RigConfig config,
        DateTimeOffset now)
    {
        if (!watchdogEnabled || isBusy || isAutoTuning || userManuallyStopped)
            return new WatchdogDecision(WatchdogActionType.None, string.Empty);

        var maxCoreTemp = snapshot.Gpus.Where(g => g.TempC is not null).Select(g => g.TempC!.Value).DefaultIfEmpty(0).Max();
        var maxVramTemp = snapshot.Gpus.Where(g => g.VramTempC is not null).Select(g => g.VramTempC!.Value).DefaultIfEmpty(0).Max();

        if (isMinerRunning && (maxCoreTemp >= config.CriticalStopTempC || maxVramTemp >= 104))
        {
            ThermalPaused = true;
            var reason = maxVramTemp >= 104
                ? $"АВАРИЙНЫЙ СТОП: память GDDR6X достигла {maxVramTemp:0} °C"
                : $"АВАРИЙНЫЙ СТОП: температура GPU достигла {maxCoreTemp:0} °C (порог {config.CriticalStopTempC} °C)";
            return new WatchdogDecision(
                WatchdogActionType.ThermalEmergencyStop,
                $"{reason}. Ожидание остывания до ≤ {config.ResumeTempC} °C.",
                reason,
                $"🚨 {reason}\nМайнер экстренно остановлен до остывания видеокарт ≤ {config.ResumeTempC} °C.");
        }

        if (ThermalPaused && !isMinerRunning && snapshot.Gpus.Count > 0 && maxCoreTemp > 0 && maxCoreTemp <= config.ResumeTempC && maxVramTemp <= 88)
        {
            ThermalPaused = false;
            var resumeMsg = $"Карты остыли до {maxCoreTemp:0} °C (≤ {config.ResumeTempC} °C). Автоматическое возобновление майнинга.";
            return new WatchdogDecision(
                WatchdogActionType.ThermalResume,
                resumeMsg,
                resumeMsg,
                $"✅ {resumeMsg}");
        }

        if (isMinerRunning && maxCoreTemp >= config.WarningTempC && now - _lastThermalWarningAt >= TimeSpan.FromSeconds(45))
        {
            _lastThermalWarningAt = now;
            var warnMsg = $"ВНИМАНИЕ: температура GPU {maxCoreTemp:0} °C приблизилась к критическому порогу ({config.CriticalStopTempC} °C).";
            return new WatchdogDecision(WatchdogActionType.ThermalWarning, warnMsg, warnMsg);
        }

        if (!isMinerRunning)
            return new WatchdogDecision(WatchdogActionType.None, string.Empty);

        _minerStartedOrObservedAt ??= now;
        var warmupPassed = now - _minerStartedOrObservedAt.Value >= TimeSpan.FromSeconds(45);
        var hasPositiveHashrate = snapshot.MinerApiAvailable && snapshot.TotalHashrateTHs is double rate && rate > 0.01;

        if (hasPositiveHashrate)
        {
            _zeroHashrateSince = null;
            if (!ThermalPaused && maxCoreTemp < config.WarningTempC)
                return new WatchdogDecision(WatchdogActionType.Healthy, $"Watchdog: норма · пиковая темп. {maxCoreTemp:0} °C · рестартов: {RestartCount}");
            return new WatchdogDecision(WatchdogActionType.None, string.Empty);
        }

        if (!warmupPassed)
            return new WatchdogDecision(WatchdogActionType.None, string.Empty);

        _zeroHashrateSince ??= now;
        var zeroElapsed = now - _zeroHashrateSince.Value;
        if (zeroElapsed >= TimeSpan.FromSeconds(config.ZeroHashrateRestartSeconds))
        {
            _zeroHashrateSince = null;
            RestartCount++;
            var restartMsg = $"Watchdog: 0 TH/s в течение {(int)zeroElapsed.TotalSeconds} с. Перезапуск OneZeroMiner (#{RestartCount}).";
            return new WatchdogDecision(
                WatchdogActionType.ZeroHashrateRestart,
                restartMsg,
                restartMsg,
                $"⚠️ {restartMsg}");
        }

        var remaining = Math.Max(0, config.ZeroHashrateRestartSeconds - (int)zeroElapsed.TotalSeconds);
        return new WatchdogDecision(
            WatchdogActionType.ZeroHashrateCountdown,
            $"Watchdog: 0 TH/s · до авто-перезапуска {remaining} с");
    }
}
