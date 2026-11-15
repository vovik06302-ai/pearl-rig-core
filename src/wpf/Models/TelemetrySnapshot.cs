namespace PearlRigCore.Models;

public sealed record TelemetrySnapshot(
    DateTimeOffset SampledAt,
    bool MinerApiAvailable,
    string MinerApiMessage,
    bool NvidiaAvailable,
    string NvidiaMessage,
    bool? PoolConnected,
    string? PoolUrl,
    double? TotalHashrateTHs,
    long? AcceptedShares,
    long? RejectedShares,
    long? UptimeSeconds,
    int? ApiLatencyMs,
    IReadOnlyList<GpuLiveMetrics> Gpus);
