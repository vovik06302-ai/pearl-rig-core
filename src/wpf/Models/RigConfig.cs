using System.IO;
using System.Text;
using System.Text.Json;
using PearlRigCore.Services;

namespace PearlRigCore.Models;

/// <summary>
/// Persistent rig configuration stored in %LOCALAPPDATA%\PearlRigControl\rig-config.json.
/// </summary>
public sealed record RigConfig
{
    public int SchemaVersion { get; init; } = 8;
    public int ApiPort { get; init; } = 3001;
    public int AgentBridgePort { get; init; } = 3002;
    public string WorkerName { get; init; } = CreateWorkerName();
    public string PrimaryPool { get; init; } = "stratum+tcp://prl-ru.kryptex.network:7048";
    public string BackupPool { get; init; } = "stratum+tcp://prl-eu.kryptex.network:7048";
    public string WalletAddress { get; init; } = string.Empty;
    public string CoreClock { get; init; } = string.Empty;
    public string MemoryClock { get; init; } = string.Empty;
    public string PowerLimit { get; init; } = string.Empty;
    public string Kernel { get; init; } = string.Empty;
    public string Fan { get; init; } = string.Empty;
    public string ActivePreset { get; init; } = "Auto";
    public bool AutoStartMining { get; init; }
    public bool ResumeMiningAfterReboot { get; init; } = true;
    public bool LastKnownMiningActive { get; init; }
    public bool WatchdogEnabled { get; init; } = true;
    public bool SmartFanCurveEnabled { get; init; }
    public bool PreventSleepWhenMining { get; init; } = true;
    public bool AutoRestartMinerOnOverclockChange { get; init; } = true;
    public bool AutoSwitchToBackupPoolOnDrop { get; init; } = true;
    public int PoolDropSwitchThresholdCount { get; init; } = 3;
    public int AutoTuneStepSeconds { get; init; } = 35;
    public int ZeroHashrateRestartSeconds { get; init; } = 90;
    public int WarningTempC { get; init; } = 82;
    public int CriticalStopTempC { get; init; } = 86;
    public int ResumeTempC { get; init; } = 65;
    public int FanCurveTempLowC { get; init; } = 50;
    public int FanCurveSpeedLowPct { get; init; } = 45;
    public int FanCurveTempMidC { get; init; } = 65;
    public int FanCurveSpeedMidPct { get; init; } = 68;
    public int FanCurveTempHighC { get; init; } = 75;
    public int FanCurveSpeedHighPct { get; init; } = 95;
    public int FanHysteresisC { get; init; } = 3;
    public int VramTempOffsetC { get; init; } = 14;
    public double ElectricityRateRubPerKwh { get; init; } = 5.50;
    public double NightElectricityRateRubPerKwh { get; init; } = 2.90;
    public bool DualTariffEnabled { get; init; }
    public int DayTariffStartHour { get; init; } = 7;
    public int NightTariffStartHour { get; init; } = 23;
    public bool NightScheduleEnabled { get; init; }
    public string DaySchedulePreset { get; init; } = "Eco";
    public string NightSchedulePreset { get; init; } = "Balance";
    public bool TelegramEnabled { get; init; }
    public bool TelegramRemoteControlEnabled { get; init; } = true;
    public string TelegramBotToken { get; init; } = string.Empty;
    public string TelegramChatId { get; init; } = string.Empty;
    public bool MinimizeToTray { get; init; } = true;

    public bool IsNightTariffWindow(DateTime localTime)
    {
        var hour = localTime.Hour;
        if (NightTariffStartHour == DayTariffStartHour) return false;
        return NightTariffStartHour > DayTariffStartHour
            ? hour >= NightTariffStartHour || hour < DayTariffStartHour
            : hour >= NightTariffStartHour && hour < DayTariffStartHour;
    }

    public int GetNightHoursPerDay()
    {
        if (NightTariffStartHour == DayTariffStartHour) return 0;
        return NightTariffStartHour > DayTariffStartHour
            ? (24 - NightTariffStartHour) + DayTariffStartHour
            : DayTariffStartHour - NightTariffStartHour;
    }

    public double GetBlendedDailyTariff(double dayRate, double nightRate)
    {
        if (!DualTariffEnabled) return dayRate;
        var nightHours = Math.Clamp(GetNightHoursPerDay(), 0, 24);
        var dayHours = 24 - nightHours;
        return (dayRate * dayHours + nightRate * nightHours) / 24d;
    }

    public double GetActiveElectricityTariff(DateTime localTime) =>
        DualTariffEnabled && IsNightTariffWindow(localTime)
            ? NightElectricityRateRubPerKwh
            : ElectricityRateRubPerKwh;

    public static string GetAppDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PearlRigControl");

    public static RigConfig Load()
    {
        var local = Path.Combine(GetAppDataDirectory(), "rig-config.json");
        var installedDefault = Path.Combine(AppContext.BaseDirectory, "rig-config.json");
        foreach (var path in new[] { local, installedDefault })
        {
            try
            {
                if (File.Exists(path))
                {
                    var config = JsonSerializer.Deserialize<RigConfig>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (config is { ApiPort: > 0 and <= 65535 })
                    {
                        var secrets = ReadLocalSecrets(local);
                        var migrated = Migrate(config);
                        return migrated with
                        {
                            WalletAddress = string.IsNullOrWhiteSpace(secrets.Wallet) ? migrated.WalletAddress : secrets.Wallet,
                            TelegramBotToken = string.IsNullOrWhiteSpace(secrets.TelegramBotToken) ? migrated.TelegramBotToken : secrets.TelegramBotToken,
                            TelegramChatId = string.IsNullOrWhiteSpace(secrets.TelegramChatId) ? migrated.TelegramChatId : secrets.TelegramChatId
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Не удалось прочитать конфигурацию {path}: {ex.Message}");
            }
        }

        var fallbackSecrets = ReadLocalSecrets(local);
        return new RigConfig
        {
            WalletAddress = fallbackSecrets.Wallet,
            TelegramBotToken = fallbackSecrets.TelegramBotToken,
            TelegramChatId = fallbackSecrets.TelegramChatId
        };
    }

    public static RigConfig Migrate(RigConfig config)
    {
        var worker = string.IsNullOrWhiteSpace(config.WorkerName) || config.WorkerName.Equals("dual4070ti", StringComparison.OrdinalIgnoreCase)
            ? CreateWorkerName()
            : config.WorkerName;

        var migrated = config.SchemaVersion < 4
            ? config with
            {
                SchemaVersion = 8,
                WorkerName = worker,
                CoreClock = config.CoreClock == "2400,2400" ? string.Empty : config.CoreClock,
                MemoryClock = config.MemoryClock == "810,810" ? string.Empty : config.MemoryClock,
                PowerLimit = config.PowerLimit == "150,150" ? string.Empty : config.PowerLimit,
                Kernel = config.Kernel == "2,2" ? string.Empty : config.Kernel,
                Fan = config.Fan == "65,*" ? string.Empty : config.Fan
            }
            : config with { SchemaVersion = 8, WorkerName = worker };

        var tempLow = migrated.FanCurveTempLowC is >= 30 and <= 75 ? migrated.FanCurveTempLowC : 50;
        var tempMid = migrated.FanCurveTempMidC is >= 40 and <= 85 ? Math.Max(tempLow + 5, migrated.FanCurveTempMidC) : Math.Max(tempLow + 5, 65);
        var tempHigh = migrated.FanCurveTempHighC is >= 50 and <= 95 ? Math.Max(tempMid + 5, migrated.FanCurveTempHighC) : Math.Max(tempMid + 5, 75);

        var speedLow = migrated.FanCurveSpeedLowPct is >= 30 and <= 100 ? migrated.FanCurveSpeedLowPct : 45;
        var speedMid = migrated.FanCurveSpeedMidPct is >= 35 and <= 100 ? Math.Max(speedLow, migrated.FanCurveSpeedMidPct) : Math.Max(speedLow, 68);
        var speedHigh = migrated.FanCurveSpeedHighPct is >= 45 and <= 100 ? Math.Max(speedMid, migrated.FanCurveSpeedHighPct) : Math.Max(speedMid, 95);

        var warningTemp = migrated.WarningTempC is >= 60 and <= 95 ? migrated.WarningTempC : 82;
        var criticalTemp = migrated.CriticalStopTempC is >= 65 and <= 100 ? Math.Max(warningTemp + 2, migrated.CriticalStopTempC) : Math.Max(warningTemp + 2, 86);
        var resumeTemp = migrated.ResumeTempC is >= 45 and <= 80 ? Math.Min(warningTemp - 5, migrated.ResumeTempC) : 65;

        return migrated with
        {
            PrimaryPool = string.IsNullOrWhiteSpace(migrated.PrimaryPool) ? "stratum+tcp://prl-ru.kryptex.network:7048" : migrated.PrimaryPool.Trim(),
            BackupPool = string.IsNullOrWhiteSpace(migrated.BackupPool) ? "stratum+tcp://prl-eu.kryptex.network:7048" : migrated.BackupPool.Trim(),
            ZeroHashrateRestartSeconds = migrated.ZeroHashrateRestartSeconds is >= 30 and <= 600 ? migrated.ZeroHashrateRestartSeconds : 90,
            AutoTuneStepSeconds = migrated.AutoTuneStepSeconds is >= 20 and <= 120 ? migrated.AutoTuneStepSeconds : 35,
            PoolDropSwitchThresholdCount = migrated.PoolDropSwitchThresholdCount is >= 1 and <= 10 ? migrated.PoolDropSwitchThresholdCount : 3,
            WarningTempC = warningTemp,
            CriticalStopTempC = criticalTemp,
            ResumeTempC = resumeTemp,
            FanCurveTempLowC = tempLow,
            FanCurveSpeedLowPct = speedLow,
            FanCurveTempMidC = tempMid,
            FanCurveSpeedMidPct = speedMid,
            FanCurveTempHighC = tempHigh,
            FanCurveSpeedHighPct = speedHigh,
            FanHysteresisC = migrated.FanHysteresisC is >= 1 and <= 10 ? migrated.FanHysteresisC : 3,
            VramTempOffsetC = migrated.VramTempOffsetC is >= 0 and <= 30 ? migrated.VramTempOffsetC : 14,
            ElectricityRateRubPerKwh = migrated.ElectricityRateRubPerKwh is >= 0 and <= 100 ? migrated.ElectricityRateRubPerKwh : 5.50,
            NightElectricityRateRubPerKwh = migrated.NightElectricityRateRubPerKwh is >= 0 and <= 100 ? migrated.NightElectricityRateRubPerKwh : 2.90,
            DayTariffStartHour = migrated.DayTariffStartHour is >= 0 and <= 23 ? migrated.DayTariffStartHour : 7,
            NightTariffStartHour = migrated.NightTariffStartHour is >= 0 and <= 23 ? migrated.NightTariffStartHour : 23,
            DaySchedulePreset = string.IsNullOrWhiteSpace(migrated.DaySchedulePreset) ? "Eco" : migrated.DaySchedulePreset,
            NightSchedulePreset = string.IsNullOrWhiteSpace(migrated.NightSchedulePreset) ? "Balance" : migrated.NightSchedulePreset
        };
    }

    private static string CreateWorkerName()
    {
        var name = new string(Environment.MachineName.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(name) ? "pearl-rig" : name[..Math.Min(name.Length, 32)];
    }

    public static async Task SaveWalletAsync(string walletAddress, CancellationToken cancellationToken = default)
    {
        var local = Path.Combine(GetAppDataDirectory(), "rig-config.json");
        var existing = ReadLocalSecrets(local);
        await SaveLocalSecretsAsync(walletAddress, existing.TelegramBotToken, existing.TelegramChatId, cancellationToken);
    }

    public static async Task SaveTelegramSecretsAsync(string botToken, string chatId, CancellationToken cancellationToken = default)
    {
        var local = Path.Combine(GetAppDataDirectory(), "rig-config.json");
        var existing = ReadLocalSecrets(local);
        await SaveLocalSecretsAsync(existing.Wallet, botToken.Trim(), chatId.Trim(), cancellationToken);
    }

    private static async Task SaveLocalSecretsAsync(string walletAddress, string telegramBotToken, string telegramChatId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(GetAppDataDirectory(), "wallet-local.json");
        var protectedToken = ProtectedSecretsService.Protect(telegramBotToken);
        var protectedChatId = ProtectedSecretsService.Protect(telegramChatId);
        var payload = JsonSerializer.Serialize(new
        {
            walletAddress,
            telegramBotToken = protectedToken,
            telegramChatId = protectedChatId,
            encryptedWithDpapi = true,
            savedAt = DateTimeOffset.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true });
        await WriteAllTextAtomicAsync(path, payload, cancellationToken);
    }

    public async Task SaveAutoStartAsync(bool enabled, CancellationToken cancellationToken = default) =>
        await SaveLocalSettingsAsync(this with { AutoStartMining = enabled }, cancellationToken);

    public static async Task SaveLocalSettingsAsync(RigConfig config, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(GetAppDataDirectory(), "rig-config.json");
        var safeConfig = config with
        {
            WalletAddress = string.Empty,
            TelegramBotToken = string.Empty,
            TelegramChatId = string.Empty
        };
        var payload = JsonSerializer.Serialize(safeConfig, new JsonSerializerOptions { WriteIndented = true });
        await WriteAllTextAtomicAsync(path, payload, cancellationToken);
    }

    private static async Task WriteAllTextAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tempPath, content, new UTF8Encoding(false), cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* Best-effort cleanup. */ }
            }
        }
    }

    private static (string Wallet, string TelegramBotToken, string TelegramChatId) ReadLocalSecrets(string localRigConfigPath)
    {
        var walletPath = Path.Combine(Path.GetDirectoryName(localRigConfigPath)!, "wallet-local.json");
        try
        {
            if (File.Exists(walletPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(walletPath));
                var root = document.RootElement;
                var wallet = root.TryGetProperty("walletAddress", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() ?? string.Empty : string.Empty;
                var rawToken = root.TryGetProperty("telegramBotToken", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? string.Empty : string.Empty;
                var rawChat = root.TryGetProperty("telegramChatId", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : string.Empty;
                return (wallet, ProtectedSecretsService.Unprotect(rawToken), ProtectedSecretsService.Unprotect(rawChat));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Не удалось прочитать локальные секреты: {ex.Message}");
        }
        return (string.Empty, string.Empty, string.Empty);
    }
}
