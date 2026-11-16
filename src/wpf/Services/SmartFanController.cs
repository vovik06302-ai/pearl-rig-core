using PearlRigCore.Models;

namespace PearlRigCore.Services;

/// <summary>Computes smooth piecewise-linear fan targets from GPU Core + GDDR6X temperatures with configurable hysteresis.</summary>
public sealed class SmartFanController
{
    private readonly Dictionary<int, (int SpeedPct, double EffectiveTempC)> _lastApplied = [];

    public void ResetState() => _lastApplied.Clear();

    public IReadOnlyList<(int GpuId, int TargetFanPercent)> EvaluateTargets(
        IReadOnlyList<GpuLiveMetrics> gpus,
        RigConfig config)
    {
        if (gpus.Count == 0)
            return [];

        var targets = new List<(int GpuId, int TargetFanPercent)>();
        foreach (var gpu in gpus)
        {
            var effectiveTemp = ComputeEffectiveTemperature(gpu, config.VramTempOffsetC);
            if (effectiveTemp <= 0)
                continue;

            var desiredPct = InterpolateCurve(effectiveTemp, config);
            if (_lastApplied.TryGetValue(gpu.Id, out var prev))
            {
                var tempDelta = Math.Abs(effectiveTemp - prev.EffectiveTempC);
                var speedDelta = Math.Abs(desiredPct - prev.SpeedPct);
                if (tempDelta < config.FanHysteresisC && speedDelta < 4)
                    continue;
            }

            _lastApplied[gpu.Id] = (desiredPct, effectiveTemp);
            targets.Add((gpu.Id, desiredPct));
        }

        return targets;
    }

    public static double ComputeEffectiveTemperature(GpuLiveMetrics gpu, int vramTempOffsetC)
    {
        var core = gpu.TempC ?? 0;
        var vramAdjusted = gpu.VramTempC is double vram ? vram - vramTempOffsetC : 0;
        return Math.Max(core, vramAdjusted);
    }

    public static int InterpolateCurve(double tempC, RigConfig config)
    {
        var t1 = config.FanCurveTempLowC;
        var s1 = config.FanCurveSpeedLowPct;
        var t2 = Math.Max(t1 + 1, config.FanCurveTempMidC);
        var s2 = Math.Max(s1, config.FanCurveSpeedMidPct);
        var t3 = Math.Max(t2 + 1, config.FanCurveTempHighC);
        var s3 = Math.Max(s2, config.FanCurveSpeedHighPct);

        if (tempC <= t1) return Math.Clamp(s1, 30, 100);
        if (tempC >= t3) return Math.Clamp(s3, 30, 100);

        double interpolated;
        if (tempC <= t2)
        {
            var ratio = (tempC - t1) / (t2 - t1);
            interpolated = s1 + ratio * (s2 - s1);
        }
        else
        {
            var ratio = (tempC - t2) / (t3 - t2);
            interpolated = s2 + ratio * (s3 - s2);
        }

        return Math.Clamp((int)Math.Round(interpolated), 30, 100);
    }
}
