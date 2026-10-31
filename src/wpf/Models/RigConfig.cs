using System.IO;

namespace PearlRigCore.Models;

/// <summary>
/// Persistent rig configuration stored in %LOCALAPPDATA%\PearlRigControl\rig-config.json.
/// </summary>
public sealed record RigConfig
{
    public int SchemaVersion { get; init; } = 4;
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
    public bool AutoStartMining { get; init; }

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
                        var localWallet = ReadWallet(local);
                        var migrated = Migrate(config);
                        return migrated with { WalletAddress = string.IsNullOrWhiteSpace(localWallet) ? migrated.WalletAddress : localWallet };
                    }
                }
            }
            catch (Exception) { /* Invalid optional configuration falls back to the documented local API port. */ }
        }

        return new RigConfig { WalletAddress = ReadWallet(local) };
    }

    private static RigConfig Migrate(RigConfig config)
    {
        if (config.SchemaVersion >= 4) return config;
        var worker = string.IsNullOrWhiteSpace(config.WorkerName) || config.WorkerName.Equals("dual4070ti", StringComparison.OrdinalIgnoreCase)
            ? CreateWorkerName()
            : config.WorkerName;
        return config with
        {
            SchemaVersion = 4,
            WorkerName = worker,
            CoreClock = config.CoreClock == "2400,2400" ? string.Empty : config.CoreClock,
            MemoryClock = config.MemoryClock == "810,810" ? string.Empty : config.MemoryClock,
            PowerLimit = config.PowerLimit == "150,150" ? string.Empty : config.PowerLimit,
            Kernel = config.Kernel == "2,2" ? string.Empty : config.Kernel,
            Fan = config.Fan == "65,*" ? string.Empty : config.Fan
        };
    }

    private static string CreateWorkerName()
    {
        var name = new string(Environment.MachineName.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(name) ? "pearl-rig" : name[..Math.Min(name.Length, 32)];
    }

    public static async Task SaveWalletAsync(string walletAddress, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PearlRigControl", "wallet-local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = System.Text.Json.JsonSerializer.Serialize(new { walletAddress, savedAt = DateTimeOffset.UtcNow });
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
        var safeConfig = config with { WalletAddress = string.Empty };
        var payload = System.Text.Json.JsonSerializer.Serialize(safeConfig, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, payload, new System.Text.UTF8Encoding(false), cancellationToken);
    }

    private static string ReadWallet(string localRigConfigPath)
    {
        var walletPath = Path.Combine(Path.GetDirectoryName(localRigConfigPath)!, "wallet-local.json");
        try
        {
            if (File.Exists(walletPath))
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(walletPath));
                if (document.RootElement.TryGetProperty("walletAddress", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
                    return value.GetString() ?? string.Empty;
            }
        }
        catch (Exception) { /* An unreadable local wallet file is treated as not configured. */ }
        return string.Empty;
    }
}
