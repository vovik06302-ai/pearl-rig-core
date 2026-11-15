using System.IO;

namespace PearlRigCore.Models;

/// <summary>
/// Persistent rig configuration stored in %LOCALAPPDATA%\PearlRigControl\rig-config.json.
/// </summary>
public sealed record RigConfig
{
    public int SchemaVersion { get; init; } = 6;
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
    public bool WatchdogEnabled { get; init; } = true;
    public bool SmartFanCurveEnabled { get; init; }
    public bool PreventSleepWhenMining { get; init; } = true;
    public int ZeroHashrateRestartSeconds { get; init; } = 90;
    public int WarningTempC { get; init; } = 82;
    public int CriticalStopTempC { get; init; } = 86;
    public int ResumeTempC { get; init; } = 65;
    public double ElectricityRateRubPerKwh { get; init; } = 5.50;
    public bool TelegramEnabled { get; init; }
    public string TelegramBotToken { get; init; } = string.Empty;
    public string TelegramChatId { get; init; } = string.Empty;
    public bool MinimizeToTray { get; init; } = true;

    public static RigConfig Load()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PearlRigControl", "rig-config.json");
        var installedDefault = Path.Combine(AppContext.BaseDirectory, "rig-config.json");
        foreach (var path in new[] { local, installedDefault })
        {
            try
            {
                if (File.Exists(path))
                {
                    var config = System.Text.Json.JsonSerializer.Deserialize<RigConfig>(File.ReadAllText(path), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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
            catch (Exception) { /* Invalid optional configuration falls back to defaults. */ }
        }

        var fallbackSecrets = ReadLocalSecrets(local);
        return new RigConfig
        {
            WalletAddress = fallbackSecrets.Wallet,
            TelegramBotToken = fallbackSecrets.TelegramBotToken,
            TelegramChatId = fallbackSecrets.TelegramChatId
        };
    }

    private static RigConfig Migrate(RigConfig config)
    {
        var worker = string.IsNullOrWhiteSpace(config.WorkerName) || config.WorkerName.Equals("dual4070ti", StringComparison.OrdinalIgnoreCase)
            ? CreateWorkerName()
            : config.WorkerName;

        var migrated = config.SchemaVersion < 4
            ? config with
            {
                SchemaVersion = 6,
                WorkerName = worker,
                CoreClock = config.CoreClock == "2400,2400" ? string.Empty : config.CoreClock,
                MemoryClock = config.MemoryClock == "810,810" ? string.Empty : config.MemoryClock,
                PowerLimit = config.PowerLimit == "150,150" ? string.Empty : config.PowerLimit,
                Kernel = config.Kernel == "2,2" ? string.Empty : config.Kernel,
                Fan = config.Fan == "65,*" ? string.Empty : config.Fan
            }
            : config with { SchemaVersion = 6, WorkerName = worker };

        return migrated with
        {
            ZeroHashrateRestartSeconds = migrated.ZeroHashrateRestartSeconds is >= 30 and <= 600 ? migrated.ZeroHashrateRestartSeconds : 90,
            WarningTempC = migrated.WarningTempC is >= 60 and <= 95 ? migrated.WarningTempC : 82,
            CriticalStopTempC = migrated.CriticalStopTempC is >= 65 and <= 100 ? migrated.CriticalStopTempC : 86,
            ResumeTempC = migrated.ResumeTempC is >= 45 and <= 80 ? migrated.ResumeTempC : 65,
            ElectricityRateRubPerKwh = migrated.ElectricityRateRubPerKwh is >= 0 and <= 100 ? migrated.ElectricityRateRubPerKwh : 5.50
        };
    }

    private static string CreateWorkerName()
    {
        var name = new string(Environment.MachineName.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(name) ? "pearl-rig" : name[..Math.Min(name.Length, 32)];
    }

    public static async Task SaveWalletAsync(string walletAddress, CancellationToken cancellationToken = default)
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PearlRigControl", "rig-config.json");
        var existing = ReadLocalSecrets(local);
        await SaveLocalSecretsAsync(walletAddress, existing.TelegramBotToken, existing.TelegramChatId, cancellationToken);
    }

    public static async Task SaveTelegramSecretsAsync(string botToken, string chatId, CancellationToken cancellationToken = default)
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PearlRigControl", "rig-config.json");
        var existing = ReadLocalSecrets(local);
        await SaveLocalSecretsAsync(existing.Wallet, botToken.Trim(), chatId.Trim(), cancellationToken);
    }

    private static async Task SaveLocalSecretsAsync(string walletAddress, string telegramBotToken, string telegramChatId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PearlRigControl", "wallet-local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            walletAddress,
            telegramBotToken,
            telegramChatId,
            savedAt = DateTimeOffset.UtcNow
        });
        await File.WriteAllTextAsync(path, payload, new System.Text.UTF8Encoding(false), cancellationToken);
    }

    public async Task SaveAutoStartAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await SaveLocalSettingsAsync(this with { AutoStartMining = enabled }, cancellationToken);
    }

    public static async Task SaveLocalSettingsAsync(RigConfig config, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PearlRigControl", "rig-config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var safeConfig = config with
        {
            WalletAddress = string.Empty,
            TelegramBotToken = string.Empty,
            TelegramChatId = string.Empty
        };
        var payload = System.Text.Json.JsonSerializer.Serialize(safeConfig, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, payload, new System.Text.UTF8Encoding(false), cancellationToken);
    }

    private static (string Wallet, string TelegramBotToken, string TelegramChatId) ReadLocalSecrets(string localRigConfigPath)
    {
        var walletPath = Path.Combine(Path.GetDirectoryName(localRigConfigPath)!, "wallet-local.json");
        try
        {
            if (File.Exists(walletPath))
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(walletPath));
                var root = document.RootElement;
                var wallet = root.TryGetProperty("walletAddress", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.String ? w.GetString() ?? string.Empty : string.Empty;
                var token = root.TryGetProperty("telegramBotToken", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() ?? string.Empty : string.Empty;
                var chat = root.TryGetProperty("telegramChatId", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String ? c.GetString() ?? string.Empty : string.Empty;
                return (wallet, token, chat);
            }
        }
        catch (Exception) { /* Unreadable local file is treated as empty. */ }
        return (string.Empty, string.Empty, string.Empty);
    }
}
