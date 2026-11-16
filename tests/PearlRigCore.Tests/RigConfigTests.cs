using PearlRigCore.Models;
using PearlRigCore.Services;
using Xunit;

namespace PearlRigCore.Tests;

public sealed class RigConfigTests
{
    [Fact]
    public void CalculatesDynamicNightHours_AndBlendedTariff_WhenWindowWrapsMidnight()
    {
        var config = new RigConfig
        {
            DualTariffEnabled = true,
            DayTariffStartHour = 7,
            NightTariffStartHour = 23,
            ElectricityRateRubPerKwh = 6.0,
            NightElectricityRateRubPerKwh = 3.0
        };

        Assert.Equal(8, config.GetNightHoursPerDay());
        Assert.True(config.IsNightTariffWindow(new DateTime(2026, 1, 1, 23, 30, 0)));
        Assert.True(config.IsNightTariffWindow(new DateTime(2026, 1, 2, 6, 45, 0)));
        Assert.False(config.IsNightTariffWindow(new DateTime(2026, 1, 2, 7, 0, 0)));
        Assert.False(config.IsNightTariffWindow(new DateTime(2026, 1, 2, 15, 0, 0)));

        // (16 * 6.0 + 8 * 3.0) / 24 = 120 / 24 = 5.0
        Assert.Equal(5.0, config.GetBlendedDailyTariff(6.0, 3.0), precision: 6);
    }

    [Fact]
    public void Migrate_ClampsOutOfRangeValuesSafely()
    {
        var migrated = RigConfig.Migrate(new RigConfig
        {
            SchemaVersion = 2,
            WorkerName = "dual4070ti",
            WarningTempC = 120,
            CriticalStopTempC = 120,
            ResumeTempC = 20,
            FanCurveTempLowC = 10,
            FanCurveTempMidC = 15,
            FanCurveTempHighC = 20,
            AutoTuneStepSeconds = 5
        });

        Assert.Equal(8, migrated.SchemaVersion);
        Assert.NotEqual("dual4070ti", migrated.WorkerName);
        Assert.Equal(82, migrated.WarningTempC);
        Assert.Equal(86, migrated.CriticalStopTempC);
        Assert.Equal(65, migrated.ResumeTempC);
        Assert.Equal(35, migrated.AutoTuneStepSeconds);
        Assert.True(migrated.FanCurveTempLowC < migrated.FanCurveTempMidC);
        Assert.True(migrated.FanCurveTempMidC < migrated.FanCurveTempHighC);
    }

    [Fact]
    public void ProtectedSecretsService_RoundTripsWithDpapiOnWindows()
    {
        const string secret = "123456:ABC-DEF_secret_bot_token";
        var cipher = ProtectedSecretsService.Protect(secret);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(ProtectedSecretsService.IsProtected(cipher));
            Assert.NotEqual(secret, cipher);
        }

        Assert.Equal(secret, ProtectedSecretsService.Unprotect(cipher));
        // Backward compatibility with legacy plaintext config values
        Assert.Equal(secret, ProtectedSecretsService.Unprotect(secret));
    }
}
