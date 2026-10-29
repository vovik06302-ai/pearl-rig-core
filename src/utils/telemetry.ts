import type { GpuLiveMetrics } from '../types/rig';
import { numberOrNull } from '../../scripts/telemetry-core.mjs';
export { numberOrNull, poolConnection, parseMinerTelemetry } from '../../scripts/telemetry-core.mjs';
type Json = Record<string, any>;

export function gpuFromTelemetry(gpu: Json | null, id: number, maxTemp: number): GpuLiveMetrics {
  const rate = numberOrNull(gpu?.hashrateTHs);
  const power = numberOrNull(gpu?.powerWatts);
  const temp = numberOrNull(gpu?.tempC);
  return {
    id, name: gpu?.name ?? 'NVIDIA GeForce RTX 4070 Ti',
    hashrateTHs: rate, acceptedShares: numberOrNull(gpu?.acceptedShares),
    rejectedShares: numberOrNull(gpu?.rejectedShares), staleShares: null, lastShareSec: null,
    tempC: temp, hotspotTempC: null, vramTempC: numberOrNull(gpu?.vramTempC),
    fanPercent: numberOrNull(gpu?.fanPercent), fanRpm: null, powerWatts: power,
    efficiencyGHW: rate !== null && power !== null && power > 0 ? rate * 1000 / power : null,
    cclkMHz: numberOrNull(gpu?.cclkMHz), mclkMHz: numberOrNull(gpu?.mclkMHz),
    voltageMv: null, vramUsedMb: null, pState: null, pcieLink: null,
    status: temp !== null && temp >= maxTemp ? 'critical' : rate !== null || temp !== null ? 'nominal' : 'no_data',
  };
}
