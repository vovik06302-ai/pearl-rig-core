using PearlRigCore.Models;
using PearlRigCore.Services;
using Xunit;

namespace PearlRigCore.Tests;

public sealed class SmartFanControllerTests
{
    [Fact]
    public void InterpolatesPiecewiseLinearCurve_CorrectlyAcrossSegments()
    {
        var config = new RigConfig
        {
            FanCurveTempLowC = 50,
            FanCurveSpeedLowPct = 40,
            FanCurveTempMidC = 60,
            FanCurveSpeedMidPct = 70,
            FanCurveTempHighC = 75,
            FanCurveSpeedHighPct = 100
        };

        Assert.Equal(40, SmartFanController.InterpolateCurve(45, config));
        Assert.Equal(40, SmartFanController.InterpolateCurve(50, config));
        Assert.Equal(55, SmartFanController.InterpolateCurve(55, config));
        Assert.Equal(70, SmartFanController.InterpolateCurve(60, config));
        Assert.Equal(90, SmartFanController.InterpolateCurve(70, config));
        Assert.Equal(100, SmartFanController.InterpolateCurve(80, config));
    }

    [Fact]
    public void UsesMaxOfCoreAndAdjustedVramTemperature()
    {
        var gpu = new GpuLiveMetrics
        {
            Id = 0,
            Name = "RTX 4070 Ti",
            TempC = 61,
            VramTempC = 86
        };

        // 86 - 14 = 72 > 61 -> effective temperature 72 C
        Assert.Equal(72, SmartFanController.ComputeEffectiveTemperature(gpu, vramTempOffsetC: 14));
    }

    [Fact]
    public void AppliesHysteresis_AndSkipsSmallTemperatureChanges()
    {
        var controller = new SmartFanController();
        var config = new RigConfig
        {
            FanCurveTempLowC = 50,
            FanCurveSpeedLowPct = 40,
            FanCurveTempMidC = 65,
            FanCurveSpeedMidPct = 70,
            FanCurveTempHighC = 75,
            FanCurveSpeedHighPct = 100,
            FanHysteresisC = 3,
            VramTempOffsetC = 14
        };

        var initial = controller.EvaluateTargets(
            [new GpuLiveMetrics { Id = 0, Name = "RTX 4070 Ti", TempC = 60 }],
            config);
        Assert.Single(initial);
        Assert.Equal((0, 60), initial[0]);

        // Only +1 C and +2% speed delta (< hysteresis 3 C and speed delta < 4%) -> suppressed
        var minorShift = controller.EvaluateTargets(
            [new GpuLiveMetrics { Id = 0, Name = "RTX 4070 Ti", TempC = 61 }],
            config);
        Assert.Empty(minorShift);

        // +5 C jump -> emits new target
        var majorShift = controller.EvaluateTargets(
            [new GpuLiveMetrics { Id = 0, Name = "RTX 4070 Ti", TempC = 65 }],
            config);
        Assert.Single(majorShift);
        Assert.Equal((0, 70), majorShift[0]);
    }
}
