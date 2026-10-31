using PearlRigCore.Models;

namespace PearlRigCore.Services;

public sealed class TelemetryService(MinerApiClient minerApi, NvidiaSmiClient nvidiaSmi, int apiPort)
{
    public async Task<TelemetrySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var minerTask = minerApi.ReadAsync(apiPort, cancellationToken);
        var nvidiaTask = nvidiaSmi.ReadAsync(cancellationToken);
        await Task.WhenAll(minerTask, nvidiaTask);
        var miner = await minerTask;
        var nvidia = await nvidiaTask;

        var ids = nvidia.Gpus.Select(gpu => gpu.Id)
            .Concat(miner.Devices.Select(device => device.Id))
            .Distinct()
            .Order()
            .ToArray();

        var gpus = ids.Select(id =>
        {
            var hardware = nvidia.Gpus.FirstOrDefault(gpu => gpu.Id == id);
            var minerDevice = FindMinerDevice(miner.Devices, hardware, id);
            return new GpuLiveMetrics
            {
                Id = id,
                Name = hardware?.Name ?? minerDevice?.Name ?? $"GPU {id}",
                HashrateTHs = minerDevice?.HashrateTHs,
                AcceptedShares = minerDevice?.AcceptedShares,
                RejectedShares = minerDevice?.RejectedShares,
                TempC = hardware?.TempC ?? minerDevice?.TempC,
                VramTempC = hardware?.VramTempC ?? minerDevice?.VramTempC,
                FanPercent = hardware?.FanPercent ?? minerDevice?.FanPercent,
                PowerWatts = hardware?.PowerWatts ?? minerDevice?.PowerWatts,
                CclkMHz = hardware?.CclkMHz ?? minerDevice?.CclkMHz,
                MclkMHz = hardware?.MclkMHz ?? minerDevice?.MclkMHz,
                Status = hardware is not null || minerDevice is not null ? "available" : "no_data",
                Source = hardware?.Source == "nvidia-smi" ? "nvidia-smi" : minerDevice is not null ? "miner-api" : hardware?.Source
            };
        }).ToArray();

        return new TelemetrySnapshot(
            DateTimeOffset.Now,
            miner.Available,
            miner.Message,
            nvidia.Available,
            nvidia.Message,
            miner.PoolConnected,
            miner.PoolUrl,
            miner.TotalHashrateTHs,
            miner.AcceptedShares,
            miner.RejectedShares,
            gpus);
    }

    private static MinerGpuReading? FindMinerDevice(IReadOnlyList<MinerGpuReading> devices, NvidiaGpuReading? hardware, int id)
    {
        if (hardware?.BusId is int busId)
        {
            var byBus = devices.FirstOrDefault(device => device.BusId == busId);
            if (byBus is not null) return byBus;
        }

        return devices.FirstOrDefault(device => device.Id == id);
    }
}
