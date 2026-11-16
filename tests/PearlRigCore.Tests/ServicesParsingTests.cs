using PearlRigCore.Models;
using PearlRigCore.Services;
using Xunit;

namespace PearlRigCore.Tests;

public sealed class ServicesParsingTests
{
    [Fact]
    public void ValidatesPearlBech32WalletAddresses()
    {
        // Valid Taproot-length (prl1p + 58 bech32 chars) and SegWit (prl1q + 37-39 chars)
        var validTaproot = "prl1p" + new string('q', 58);
        var validSegwit = "prl1q" + new string('z', 38);

        Assert.True(PoolEarningsClient.IsValidWallet(validTaproot));
        Assert.True(PoolEarningsClient.IsValidWallet(validSegwit));
        Assert.False(PoolEarningsClient.IsValidWallet("prl1p12345"));
        Assert.False(PoolEarningsClient.IsValidWallet("bc1q" + new string('q', 38)));
        Assert.False(PoolEarningsClient.IsValidWallet(""));
        Assert.False(PoolEarningsClient.IsValidWallet(null));
    }

    [Fact]
    public void BuildsOneZeroMinerArguments_UsingBackupPoolWhenRequested()
    {
        var rig = new RigConfig
        {
            WalletAddress = "prl1p" + new string('q', 58),
            PrimaryPool = "stratum+tcp://prl-ru.kryptex.network:7048",
            BackupPool = "stratum+tcp://prl-eu.kryptex.network:7048",
            WorkerName = "rig-01",
            ApiPort = 3001,
            CoreClock = "2400",
            PowerLimit = "150",
            Kernel = "2"
        };

        var primaryArgs = MinerControlService.BuildArguments(rig, @"C:\logs\miner.log", useBackupPool: false, gpuCount: 2);
        Assert.Contains("-o \"stratum+tcp://prl-ru.kryptex.network:7048\"", primaryArgs);
        Assert.Contains("--cclk \"2400,2400\"", primaryArgs);
        Assert.Contains("--pl \"150,150\"", primaryArgs);
        Assert.Contains("--kernel \"2,2\"", primaryArgs);

        var backupArgs = MinerControlService.BuildArguments(rig, @"C:\logs\miner.log", useBackupPool: true, gpuCount: 2);
        Assert.Contains("-o \"stratum+tcp://prl-eu.kryptex.network:7048\"", backupArgs);
    }

    [Fact]
    public void DetectsWhenSettingsChangeRequiresMinerRestart_VsLivePowerLimitOnly()
    {
        var baseCfg = new RigConfig
        {
            WorkerName = "rig-01",
            PrimaryPool = "stratum+tcp://prl-ru.kryptex.network:7048",
            PowerLimit = "150,150",
            CoreClock = "2400,2400"
        };

        // PowerLimit change applies via NVML without restarting miner
        var powerOnly = baseCfg with { PowerLimit = "135,135" };
        Assert.False(MinerControlService.RequiresMinerRestartForSettings(baseCfg, powerOnly));

        // CoreClock or pool change requires soft miner restart
        var cclkChanged = baseCfg with { CoreClock = "2250,2250" };
        Assert.True(MinerControlService.RequiresMinerRestartForSettings(baseCfg, cclkChanged));
    }
}
