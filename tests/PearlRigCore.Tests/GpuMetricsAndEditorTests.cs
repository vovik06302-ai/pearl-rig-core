using PearlRigCore.Models;
using Xunit;

namespace PearlRigCore.Tests;

public sealed class GpuMetricsAndEditorTests
{
    [Fact]
    public void ComputesSemanticHealthFlagsAndStructuredBadges()
    {
        var warnGpu = new GpuLiveMetrics
        {
            Id = 0,
            Name = "RTX 4070 Ti",
            TempC = 80,
            VramTempC = 96,
            PcieLink = "Gen4 x16",
            PState = "P2",
            CoreUtilizationPercent = 100,
            MemoryUtilizationPercent = 99,
            HardwareEnergyKwh = 1.25,
            MinPowerLimitWatts = 100,
            MaxPowerLimitWatts = 320,
            DefaultPowerLimitWatts = 285,
            ThrottleReasons = "Термо-ограничение"
        };

        Assert.True(warnGpu.IsCoreTempWarning);
        Assert.False(warnGpu.IsCoreTempCritical);
        Assert.True(warnGpu.IsVramTempWarning);
        Assert.False(warnGpu.IsVramTempCritical);
        Assert.True(warnGpu.HasThrottleWarning);
        Assert.Equal("Gen4 x16", warnGpu.PcieBadgeText);
        Assert.Equal("P2", warnGpu.PStateBadgeText);
        Assert.Equal("Ядро 100% · Память 99%", warnGpu.LoadBadgeText);
        Assert.Equal("NVML 1.25 кВт·ч", warnGpu.EnergyCounterBadgeText);
        Assert.Equal("100–320 Вт (норма 285 Вт)", warnGpu.PowerRangeBadgeText);

        var criticalGpu = warnGpu with { TempC = 86, VramTempC = 104 };
        Assert.False(criticalGpu.IsCoreTempWarning);
        Assert.True(criticalGpu.IsCoreTempCritical);
        Assert.False(criticalGpu.IsVramTempWarning);
        Assert.True(criticalGpu.IsVramTempCritical);
        Assert.True(criticalGpu.IsOverheated);
    }

    [Fact]
    public void GpuOverclockRowEditorSuppressesCallbacksDuringSilentSync()
    {
        var callbackCount = 0;
        var row = new GpuOverclockRowEditor
        {
            Id = 0,
            Name = "RTX 4070 Ti",
            ChangedCallback = () => callbackCount++
        };

        row.UpdateSilently("2400", "810", "150", "2", "65", "100–320 Вт");
        Assert.Equal(0, callbackCount);
        Assert.Equal("2400", row.CoreClock);

        row.PowerLimit = "140";
        Assert.Equal(1, callbackCount);
        Assert.Equal("140", row.PowerLimit);
    }
}
