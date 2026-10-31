using System.IO;

namespace PearlRigCore.Models;

/// <summary>
/// Persistent rig configuration stored in %LOCALAPPDATA%\PearlRigControl\rig-config.json.
/// </summary>
public sealed record RigConfig
{
    public int SchemaVersion { get; init; } = 3;
    public int ApiPort { get; init; } = 3001;
    public int AgentBridgePort { get; init; } = 3002;
    public string WorkerName { get; init; } = "dual4070ti";
    public string PrimaryPool { get; init; } = "stratum+tcp://prl-ru.kryptex.network:7048";
    public string BackupPool { get; init; } = "stratum+tcp://prl-eu.kryptex.network:7048";
    public string WalletAddress { get; init; } = string.Empty;
    public string CoreClock { get; init; } = "2400,2400";
    public string MemoryClock { get; init; } = "810,810";
    public string PowerLimit { get; init; } = "150,150";
    public string Kernel { get; init; } = "2,2";
    public string Fan { get; init; } = "65,*";
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
                        return config with { WalletAddress = string.IsNullOrWhiteSpace(localWallet) ? config.WalletAddress : localWallet };
                    }
                }
            }
            catch (Exception) { /* Invalid optional configuration falls back to the documented local API port. */ }
        }

        return new RigConfig { WalletAddress = ReadWallet(local) };
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
