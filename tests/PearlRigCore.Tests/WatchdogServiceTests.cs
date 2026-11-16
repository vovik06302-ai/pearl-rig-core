using PearlRigCore.Models;
using PearlRigCore.Services;
using Xunit;

namespace PearlRigCore.Tests;

public sealed class WatchdogServiceTests
{
    private static TelemetrySnapshot MakeSnapshot(
        DateTimeOffset now,
        double? totalHashrateTHs,
        double? coreTempC,
        double? vramTempC = null,
        bool minerApiAvailable = true)
    {
        var gpus = new[]
        {
            new GpuLiveMetrics
            {
                Id = 0,
                Name = "NVIDIA GeForce RTX 4070 Ti",
                TempC = coreTempC,
                VramTempC = vramTempC,
                HashrateTHs = totalHashrateTHs is double r ? r / 2d : null
            },
            new GpuLiveMetrics
            {
                Id = 1,
                Name = "NVIDIA GeForce RTX 4070 Ti",
                TempC = coreTempC,
                VramTempC = vramTempC,
                HashrateTHs = totalHashrateTHs is double r2 ? r2 / 2d : null
            }
        };

        return new TelemetrySnapshot(
            now,
            minerApiAvailable,
            minerApiAvailable ? "OK" : "Unavailable",
            true,
            "NVML",
            true,
            "stratum+tcp://prl-ru.kryptex.network:7048",
            totalHashrateTHs,
            10,
            0,
            120,
            12,
            gpus);
    }

    [Fact]
    public void TriggersThermalEmergencyStop_WhenCoreTempExceedsCriticalThreshold()
    {
        var service = new WatchdogService();
        var config = new RigConfig { WarningTempC = 82, CriticalStopTempC = 86, ResumeTempC = 65 };
        var now = DateTimeOffset.UtcNow;

        var decision = service.Evaluate(
            MakeSnapshot(now, 150d, 87d, 80d),
            isMinerRunning: true,
            watchdogEnabled: true,
            isBusy: false,
            isAutoTuning: false,
            userManuallyStopped: false,
            config,
            now);

        Assert.Equal(WatchdogActionType.ThermalEmergencyStop, decision.Action);
        Assert.True(service.ThermalPaused);
        Assert.NotNull(decision.TelegramAlertMessage);
    }

    [Fact]
    public void TriggersThermalEmergencyStop_WhenVramExceeds104C()
    {
        var service = new WatchdogService();
        var config = new RigConfig { CriticalStopTempC = 86, ResumeTempC = 65 };
        var now = DateTimeOffset.UtcNow;

        var decision = service.Evaluate(
            MakeSnapshot(now, 150d, 68d, 105d),
            isMinerRunning: true,
            watchdogEnabled: true,
            isBusy: false,
            isAutoTuning: false,
            userManuallyStopped: false,
            config,
            now);

        Assert.Equal(WatchdogActionType.ThermalEmergencyStop, decision.Action);
        Assert.True(service.ThermalPaused);
    }

    [Fact]
    public void ResumesMining_OnlyAfterCoolingDownBelowResumeThreshold()
    {
        var service = new WatchdogService();
        var config = new RigConfig { CriticalStopTempC = 86, ResumeTempC = 65 };
        var t0 = DateTimeOffset.UtcNow;

        _ = service.Evaluate(MakeSnapshot(t0, 150d, 88d, 90d), true, true, false, false, false, config, t0);
        Assert.True(service.ThermalPaused);

        // Still too warm to resume
        var warmDecision = service.Evaluate(MakeSnapshot(t0.AddSeconds(15), null, 70d, 74d, false), false, true, false, false, false, config, t0.AddSeconds(15));
        Assert.Equal(WatchdogActionType.None, warmDecision.Action);
        Assert.True(service.ThermalPaused);

        // Cooled down to <= ResumeTempC and VRAM <= 88
        var coolDecision = service.Evaluate(MakeSnapshot(t0.AddSeconds(45), null, 64d, 70d, false), false, true, false, false, false, config, t0.AddSeconds(45));
        Assert.Equal(WatchdogActionType.ThermalResume, coolDecision.Action);
        Assert.False(service.ThermalPaused);
    }

    [Fact]
    public void TriggersZeroHashrateRestart_AfterWarmupAndConfiguredDelay()
    {
        var service = new WatchdogService();
        var config = new RigConfig { ZeroHashrateRestartSeconds = 60 };
        var t0 = DateTimeOffset.UtcNow;
        service.MarkMinerStarted(t0);

        // During 45s warmup -> None
        var warmup = service.Evaluate(MakeSnapshot(t0.AddSeconds(20), 0d, 55d), true, true, false, false, false, config, t0.AddSeconds(20));
        Assert.Equal(WatchdogActionType.None, warmup.Action);

        // Warmup passed, countdown begins
        var countdown = service.Evaluate(MakeSnapshot(t0.AddSeconds(50), 0d, 55d), true, true, false, false, false, config, t0.AddSeconds(50));
        Assert.Equal(WatchdogActionType.ZeroHashrateCountdown, countdown.Action);

        // Full 60s elapsed at zero hashrate -> Restart
        var restart = service.Evaluate(MakeSnapshot(t0.AddSeconds(111), 0d, 55d), true, true, false, false, false, config, t0.AddSeconds(111));
        Assert.Equal(WatchdogActionType.ZeroHashrateRestart, restart.Action);
        Assert.Equal(1, service.RestartCount);
    }

    [Fact]
    public void EvaluatesPoolFailover_AfterConsecutivePrimaryDrops_AndRecovers()
    {
        var service = new WatchdogService();
        var config = new RigConfig { AutoSwitchToBackupPoolOnDrop = true, PoolDropSwitchThresholdCount = 3 };

        Assert.Null(service.EvaluatePoolSwitch(new PoolPingResult(null, 40, "drop 1"), false, config));
        Assert.Null(service.EvaluatePoolSwitch(new PoolPingResult(null, 42, "drop 2"), false, config));
        Assert.True(service.EvaluatePoolSwitch(new PoolPingResult(null, 39, "drop 3"), false, config));

        // Primary recovers while on backup -> switch back (false)
        Assert.False(service.EvaluatePoolSwitch(new PoolPingResult(18, 41, "recovered"), true, config));
    }
}
