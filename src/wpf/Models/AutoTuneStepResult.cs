namespace PearlRigCore.Models;

/// <summary>Represents one benchmarked power-limit step during GH/W auto-tuning.</summary>
public sealed record AutoTuneStepResult(
    int StepNumber,
    int TargetWatts,
    double HashrateTHs,
    double PowerWatts,
    double EfficiencyGhPerWatt,
    double MaxTempC,
    bool IsBest)
{
    public string WattsText => $"{TargetWatts} Вт";
    public string HashrateText => $"{HashrateTHs:0.00} TH/s";
    public string PowerText => $"{PowerWatts:0} Вт";
    public string EfficiencyText => $"{EfficiencyGhPerWatt:0.00} GH/W";
    public string MaxTempText => $"{MaxTempC:0} °C";
    public string StatusBadge => IsBest ? "ЛУЧШАЯ GH/W" : "Проверено";
}

/// <summary>Represents a single NVIDIA GPU overclock row when per-GPU editing is enabled.</summary>
public sealed record GpuOverclockRow(
    int Id,
    string Name,
    string PowerLimitsHint,
    string CoreClock,
    string MemoryClock,
    string PowerLimit,
    string Kernel,
    string Fan);
