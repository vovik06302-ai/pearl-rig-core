namespace PearlRigCore.Models;

/// <summary>GPU readings that came from the miner API or NVIDIA driver (NVML / nvidia-smi).</summary>
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
    public double? EnforcedPowerLimitWatts { get; init; }
    public double? MinPowerLimitWatts { get; init; }
    public double? MaxPowerLimitWatts { get; init; }
    public double? DefaultPowerLimitWatts { get; init; }
    public double? CclkMHz { get; init; }
    public double? MclkMHz { get; init; }
    public string? PState { get; init; }
    public double? CoreUtilizationPercent { get; init; }
    public double? MemoryUtilizationPercent { get; init; }
    public double? HardwareEnergyKwh { get; init; }
    public string? ThrottleReasons { get; init; }
    public string? PcieLink { get; init; }
    public IReadOnlyList<double> HashrateHistory { get; init; } = [];
    public IReadOnlyList<double> TemperatureHistory { get; init; } = [];
    public string Status { get; init; } = "no_data";
    public string? Source { get; init; }

    public double? EfficiencyGhPerWatt =>
        HashrateTHs is double rate && rate > 0 && PowerWatts is double watts && watts > 0
            ? (rate * 1000d) / watts
            : null;

    public string HashrateText => HashrateTHs is double value ? $"{value:0.00} TH/s" : "— TH/s";
    public string EfficiencyText => EfficiencyGhPerWatt is double eff ? $"{eff:0.00} GH/W" : "— GH/W";
    public string TemperatureText => TempC is double value ? $"{value:0} °C" : "— °C";
    public string VramTemperatureText => VramTempC is double value ? $"{value:0} °C" : "— °C";

    public string PowerText
    {
        get
        {
            var drawText = PowerWatts is double draw ? $"{draw:0}" : "—";
            var capValue = EnforcedPowerLimitWatts ?? PowerLimitWatts;
            var capText = capValue is double cap ? $"{cap:0}" : "—";
            return $"{drawText} / {capText} W";
        }
    }

    public string FanText => FanPercent is double value ? $"{value:0}%" : "—";
    public string CoreClockText => CclkMHz is double value ? $"{value:0} MHz" : "— MHz";
    public string MemoryClockText => MclkMHz is double value ? $"{value:0} MHz" : "— MHz";

    public string ThrottleText
    {
        get
        {
            var pStatePrefix = string.IsNullOrWhiteSpace(PState) ? string.Empty : $"{PState} · ";
            var loadSuffix = CoreUtilizationPercent is double util ? $" ({util:0}%)" : string.Empty;
            var baseThrottle = string.IsNullOrWhiteSpace(ThrottleReasons) ? "Нет данных" : ThrottleReasons;
            return $"{pStatePrefix}{baseThrottle}{loadSuffix}";
        }
    }

    public string PcieLinkText
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(PcieLink)) parts.Add(PcieLink);
            if (!string.IsNullOrWhiteSpace(PState)) parts.Add(PState);
            if (CoreUtilizationPercent is double gUtil)
            {
                var mText = MemoryUtilizationPercent is double mUtil ? $"/{mUtil:0}%" : string.Empty;
                parts.Add($"Загрузка {gUtil:0}%{mText}");
            }
            if (HardwareEnergyKwh is double hwKwh && hwKwh > 0)
                parts.Add($"Счётчик чипа {hwKwh:0.00} кВт·ч");
            return parts.Count == 0 ? string.Empty : " · " + string.Join(" · ", parts);
        }
    }

    public string SharesText
    {
        get
        {
            if (AcceptedShares is not long accepted) return "—";
            var rejected = RejectedShares ?? 0;
            var total = accepted + rejected;
            if (total <= 0) return $"{accepted:N0} / 0";
            var rejectPct = rejected * 100d / total;
            return $"{accepted:N0} / {rejected:N0} ({rejectPct:0.##}% откл.)";
        }
    }

    public bool HasHashrate => HashrateTHs is double value && double.IsFinite(value) && value >= 0;
    public bool HasTemperature => TempC is double value && double.IsFinite(value) && value >= 0;
    public bool IsOverheated => (TempC is double value && double.IsFinite(value) && value >= 80)
        || (VramTempC is double vram && double.IsFinite(vram) && vram >= 98);

    public string SourceText => (Source switch
    {
        "nvml" => "Прямой драйвер NVML",
        "nvidia-smi" => "Датчики NVIDIA (SMI)",
        "miner-api" => "API майнера",
        "windows-inventory" => "Обнаружена Windows; датчики недоступны",
        _ => "Источник не отвечает"
    }) + PcieLinkText;
}
