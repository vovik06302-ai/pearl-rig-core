using PearlRigCore.Models;

namespace PearlRigCore.Services;

public sealed record ElectricitySnapshot(
    double SessionEnergyKwh,
    double SessionElectricityCostRub,
    double? DailyElectricityCostRub,
    double ActiveTariffRubPerKwh,
    string ZoneLabel,
    bool UsedHardwareCounter);

public sealed record SessionAccumulationResult(
    bool BaselineCaptured,
    double? EarnedSessionPrl,
    double? PerDayPrl,
    double? GrossRubPerDay,
    string SummaryText);

/// <summary>Tracks energy using NVML hardware counters (with software power integration fallback), dynamic day/night tariffs, and payout-safe PRL accumulation rate.</summary>
public sealed class ProfitabilityCalculator
{
    private readonly Dictionary<int, double> _lastHardwareEnergyByGpu = [];
    private DateTimeOffset? _lastEnergySampleAt;
    private DateTimeOffset? _sessionFirstObservedAt;
    private double? _sessionFirstCumulativePrl;

    public double SessionEnergyKwh { get; private set; }
    public double SessionElectricityCostRub { get; private set; }
    public double? LastObservedGrossRubPerDay { get; private set; }
    public double? LastObservedNetRubPerDay { get; private set; }
    public double? LastObservedPerDayPrl { get; private set; }

    public void RestoreSessionEnergy(double energyKwh, double costRub)
    {
        if (double.IsFinite(energyKwh) && energyKwh >= 0) SessionEnergyKwh = energyKwh;
        if (double.IsFinite(costRub) && costRub >= 0) SessionElectricityCostRub = costRub;
    }

    public void ResetAccumulationBaseline()
    {
        _sessionFirstObservedAt = null;
        _sessionFirstCumulativePrl = null;
        LastObservedGrossRubPerDay = null;
        LastObservedNetRubPerDay = null;
        LastObservedPerDayPrl = null;
    }

    public double? EstimateDaysToPayout(double? currentTotalPrl, double? payoutThresholdPrl)
    {
        if (currentTotalPrl is not double total || payoutThresholdPrl is not double threshold || threshold <= 0)
            return null;
        if (total >= threshold)
            return 0d;
        if (LastObservedPerDayPrl is not double rate || rate <= 0.0001)
            return null;
        return (threshold - total) / rate;
    }

    public ElectricitySnapshot UpdateEnergyAndCost(
        DateTimeOffset sampledAt,
        IReadOnlyList<GpuLiveMetrics> gpus,
        double? totalPowerWatts,
        RigConfig config,
        double dayTariff,
        double nightTariff)
    {
        var isNightNow = config.DualTariffEnabled && config.IsNightTariffWindow(sampledAt.LocalDateTime);
        var activeTariff = isNightNow ? nightTariff : dayTariff;
        var zoneLabel = config.DualTariffEnabled
            ? (isNightNow ? $"Т2 Ночь {activeTariff:0.00} ₽" : $"Т1 День {activeTariff:0.00} ₽")
            : $"{activeTariff:0.00} ₽/кВт·ч";

        var usedHardwareCounter = false;
        double deltaKwh = 0;

        // Prefer real NVML hardware energy counter (millijoules -> kWh) when all GPUs report monotonic counters.
        var hwCounters = gpus.Where(g => g.HardwareEnergyKwh is double v && v > 0).ToArray();
        if (hwCounters.Length > 0 && hwCounters.Length == gpus.Count)
        {
            var allHadPrevious = hwCounters.All(g => _lastHardwareEnergyByGpu.ContainsKey(g.Id));
            if (allHadPrevious)
            {
                double sumDelta = 0;
                var monotonic = true;
                foreach (var gpu in hwCounters)
                {
                    var current = gpu.HardwareEnergyKwh!.Value;
                    var prev = _lastHardwareEnergyByGpu[gpu.Id];
                    var diff = current - prev;
                    if (diff < 0 || diff > 1.0)
                    {
                        monotonic = false;
                        break;
                    }
                    sumDelta += diff;
                }

                if (monotonic)
                {
                    deltaKwh = sumDelta;
                    usedHardwareCounter = true;
                }
            }

            foreach (var gpu in hwCounters)
                _lastHardwareEnergyByGpu[gpu.Id] = gpu.HardwareEnergyKwh!.Value;
        }

        if (!usedHardwareCounter && _lastEnergySampleAt is DateTimeOffset prevSample && totalPowerWatts is double watts && watts > 0)
        {
            var dtHours = (sampledAt - prevSample).TotalHours;
            if (dtHours is > 0 and < 0.1)
                deltaKwh = (watts / 1000d) * dtHours;
        }

        _lastEnergySampleAt = sampledAt;
        if (deltaKwh > 0)
        {
            SessionEnergyKwh += deltaKwh;
            SessionElectricityCostRub += deltaKwh * activeTariff;
        }

        double? dailyCostRub = null;
        if (totalPowerWatts is double currentWatts && currentWatts > 0)
        {
            var dailyKwh = currentWatts * 24d / 1000d;
            var blendedTariff = config.GetBlendedDailyTariff(dayTariff, nightTariff);
            dailyCostRub = dailyKwh * blendedTariff;
            LastObservedNetRubPerDay = LastObservedGrossRubPerDay is double gross ? gross - dailyCostRub.Value : null;
        }

        return new ElectricitySnapshot(
            SessionEnergyKwh,
            SessionElectricityCostRub,
            dailyCostRub,
            activeTariff,
            zoneLabel,
            usedHardwareCounter);
    }

    public SessionAccumulationResult UpdateAccumulation(PoolEarningsSnapshot earnings)
    {
        if (earnings.TotalPrl is not double observedTotal)
        {
            return new SessionAccumulationResult(false, null, null, LastObservedGrossRubPerDay, "Фактическая скорость сессии: ожидание данных баланса");
        }

        // Cumulative mined PRL = pool balance + total paid out, preventing payout drops from distorting session yield.
        var cumulativePrl = observedTotal + (earnings.PaidPrl ?? 0d);
        if (_sessionFirstCumulativePrl is null || _sessionFirstObservedAt is null || cumulativePrl < _sessionFirstCumulativePrl.Value)
        {
            _sessionFirstCumulativePrl = cumulativePrl;
            _sessionFirstObservedAt = earnings.FetchedAt;
            return new SessionAccumulationResult(true, 0, null, null, "Фактическая скорость сессии: точка отсчёта зафиксирована");
        }

        var elapsedHours = (earnings.FetchedAt - _sessionFirstObservedAt.Value).TotalHours;
        var deltaPrl = cumulativePrl - _sessionFirstCumulativePrl.Value;
        if (elapsedHours < 0.05 || deltaPrl <= 0)
        {
            return new SessionAccumulationResult(false, deltaPrl, null, LastObservedGrossRubPerDay, "Фактическая скорость сессии: накопление замеров…");
        }

        var perDayPrl = deltaPrl / elapsedHours * 24d;
        LastObservedPerDayPrl = perDayPrl;
        if (earnings.PrlRub is double rubRate && rubRate > 0)
        {
            LastObservedGrossRubPerDay = perDayPrl * rubRate;
            return new SessionAccumulationResult(
                false,
                deltaPrl,
                perDayPrl,
                LastObservedGrossRubPerDay,
                $"За сессию: +{deltaPrl:0.######} PRL · темп {perDayPrl:0.####} PRL/сут (≈ {LastObservedGrossRubPerDay:0.00} ₽/сут)");
        }

        return new SessionAccumulationResult(
            false,
            deltaPrl,
            perDayPrl,
            LastObservedGrossRubPerDay,
            $"За сессию: +{deltaPrl:0.######} PRL · темп {perDayPrl:0.####} PRL/сут");
    }
}
