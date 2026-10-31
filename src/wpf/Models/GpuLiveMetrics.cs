namespace PearlRigCore.Models;

/// <summary>GPU readings that came from the miner API or NVIDIA driver.</summary>
public sealed record GpuLiveMetrics
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public double? HashrateTHs { get; init; }
    public long? AcceptedShares { get; init; }
    public long? RejectedShares { get; init; }
    public double? TempC { get; init; }
    public double? VramTempC { get; init; }
    public double? FanPercent { get; init; }
    public double? PowerWatts { get; init; }
    public double? PowerLimitWatts { get; init; }
    public double? MinPowerLimitWatts { get; init; }
    public double? MaxPowerLimitWatts { get; init; }
    public double? CclkMHz { get; init; }
    public double? MclkMHz { get; init; }
    public string Status { get; init; } = "no_data";
    public string? Source { get; init; }

    public string HashrateText => HashrateTHs is double value ? $"{value:0.00} TH/s" : "— TH/s";
    public string TemperatureText => TempC is double value ? $"{value:0} °C" : "— °C";
    public string VramTemperatureText => VramTempC is double value ? $"{value:0} °C" : "— °C";
    public string PowerText => $"{(PowerWatts is double draw ? $"{draw:0}" : "—")} / {(PowerLimitWatts is double cap ? $"{cap:0}" : "—")} W";
    public string FanText => FanPercent is double value ? $"{value:0}%" : "—";
    public string CoreClockText => CclkMHz is double value ? $"{value:0} MHz" : "— MHz";
    public string MemoryClockText => MclkMHz is double value ? $"{value:0} MHz" : "— MHz";
    public string SharesText => AcceptedShares is long accepted ? accepted.ToString("N0") : "—";
    public bool HasHashrate => HashrateTHs is double value && double.IsFinite(value) && value >= 0;
    public bool HasTemperature => TempC is double value && double.IsFinite(value) && value >= 0;
    public bool IsOverheated => TempC is double value && double.IsFinite(value) && value >= 80;
    public string SourceText => Source switch
    {
        "nvidia-smi" => "Датчики NVIDIA",
        "miner-api" => "API майнера",
        "windows-inventory" => "Обнаружена Windows; датчики недоступны",
        _ => "Источник не отвечает"
    };
}
