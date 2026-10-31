using System.Text.Json.Serialization;

namespace PearlRigCore.Models;

/// <summary>
/// Live status of the mining pool connection and overall rig state.
/// </summary>
public record PoolLiveStatus(
    bool? Connected,
    string? PoolUrl,
    int? PingMs,
    long? UptimeSec,
    bool ApiAvailable,
    bool NvmlAvailable,
    bool? MinerProcessRunning,
    int? MinerPid,
    string? ReconnectStage, // "idle", "native_wait", "restarting", "awaiting_confirmation", "limit_reached"
    int RestartAttempts,
    long SharesAtRestart,
    bool RecoveryConfirmed,
    string? LastError,
    string? AgentVersion,
    bool? UpdateAvailable,
    string? LatestVersion,
    int? DownloadProgress
);
