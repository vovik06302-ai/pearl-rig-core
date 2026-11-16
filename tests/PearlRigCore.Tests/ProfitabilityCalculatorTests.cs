using PearlRigCore.Models;
using PearlRigCore.Services;
using Xunit;

namespace PearlRigCore.Tests;

public sealed class ProfitabilityCalculatorTests
{
    [Fact]
    public void TracksHardwareNvmlEnergyDeltas_WhenMonotonicCountersAvailable()
    {
        var calc = new ProfitabilityCalculator();
        var config = new RigConfig { DualTariffEnabled = false };
        var t0 = DateTimeOffset.Now;

        // First sample establishes baseline hardware energy counters
        var first = calc.UpdateEnergyAndCost(
            t0,
            [
                new GpuLiveMetrics { Id = 0, Name = "GPU0", PowerWatts = 150, HardwareEnergyKwh = 10.000 },
                new GpuLiveMetrics { Id = 1, Name = "GPU1", PowerWatts = 150, HardwareEnergyKwh = 20.000 }
            ],
            300,
            config,
            dayTariff: 6.00,
            nightTariff: 3.00);

        Assert.Equal(0, first.SessionEnergyKwh, precision: 6);

        // Second sample reports +0.125 kWh on GPU0 and +0.125 kWh on GPU1 = +0.250 kWh -> 1.50 RUB at 6.00 RUB/kWh
        var second = calc.UpdateEnergyAndCost(
            t0.AddSeconds(3),
            [
                new GpuLiveMetrics { Id = 0, Name = "GPU0", PowerWatts = 150, HardwareEnergyKwh = 10.125 },
                new GpuLiveMetrics { Id = 1, Name = "GPU1", PowerWatts = 150, HardwareEnergyKwh = 20.125 }
            ],
            300,
            config,
            dayTariff: 6.00,
            nightTariff: 3.00);

        Assert.True(second.UsedHardwareCounter);
        Assert.Equal(0.250, second.SessionEnergyKwh, precision: 6);
        Assert.Equal(1.50, second.SessionElectricityCostRub, precision: 4);
    }

    [Fact]
    public void TracksCumulativeMinedPrlAcrossPoolPayoutDrops()
    {
        var calc = new ProfitabilityCalculator();
        var t0 = DateTimeOffset.UtcNow;

        // Baseline: balance = 40 PRL, paid = 100 PRL => cumulative = 140 PRL
        var baseline = calc.UpdateAccumulation(new PoolEarningsSnapshot(
            true, true, false, "OK", t0,
            ConfirmedPrl: 40, UnconfirmedPrl: 0, TotalPrl: 40, PayoutThresholdPrl: 50, PaidPrl: 100,
            PrlUsd: 0.05, UsdRub: 100, PrlRub: 5.0));
        Assert.True(baseline.BaselineCaptured);

        // After 6 hours: pool paid out 50 PRL! Balance dropped to 2 PRL, Paid increased to 150 PRL.
        // Cumulative = 152 PRL => actual mined = +12 PRL in 6h => 48 PRL/day => 240 RUB/day.
        var afterPayout = calc.UpdateAccumulation(new PoolEarningsSnapshot(
            true, true, false, "OK", t0.AddHours(6),
            ConfirmedPrl: 2, UnconfirmedPrl: 0, TotalPrl: 2, PayoutThresholdPrl: 50, PaidPrl: 150,
            PrlUsd: 0.05, UsdRub: 100, PrlRub: 5.0));

        Assert.False(afterPayout.BaselineCaptured);
        Assert.NotNull(afterPayout.EarnedSessionPrl);
        Assert.Equal(12.0, afterPayout.EarnedSessionPrl!.Value, precision: 6);
        Assert.Equal(48.0, afterPayout.PerDayPrl!.Value, precision: 4);
        Assert.Equal(240.0, afterPayout.GrossRubPerDay!.Value, precision: 2);
    }

    [Fact]
    public void EstimatesDaysToPayoutFromObservedAccumulationRate()
    {
        var calc = new ProfitabilityCalculator();
        var t0 = DateTimeOffset.UtcNow;

        Assert.Null(calc.EstimateDaysToPayout(10, 50));

        calc.UpdateAccumulation(new PoolEarningsSnapshot(
            true, true, false, "OK", t0,
            ConfirmedPrl: 10, UnconfirmedPrl: 0, TotalPrl: 10, PayoutThresholdPrl: 50, PaidPrl: 0,
            PrlUsd: 0.05, UsdRub: 100, PrlRub: 5.0));

        // +5 PRL in 6h => 20 PRL/day. Remaining = 50 - 15 = 35 PRL => 1.75 days.
        calc.UpdateAccumulation(new PoolEarningsSnapshot(
            true, true, false, "OK", t0.AddHours(6),
            ConfirmedPrl: 15, UnconfirmedPrl: 0, TotalPrl: 15, PayoutThresholdPrl: 50, PaidPrl: 0,
            PrlUsd: 0.05, UsdRub: 100, PrlRub: 5.0));

        Assert.Equal(20.0, calc.LastObservedPerDayPrl!.Value, precision: 4);
        var eta = calc.EstimateDaysToPayout(15, 50);
        Assert.NotNull(eta);
        Assert.Equal(1.75, eta!.Value, precision: 4);

        Assert.Equal(0.0, calc.EstimateDaysToPayout(55, 50)!.Value, precision: 4);
    }
}

