const numberOrNull = (value) => typeof value === "number" && Number.isFinite(value) && value >= 0 ? value : null;
function poolConnection(algo) {
  if (typeof algo?.pool_connected === "boolean") return algo.pool_connected;
  const status = String(algo?.pool_status ?? "").trim().toLowerCase();
  if (["connected", "online", "active"].includes(status)) return true;
  if (["disconnected", "offline", "inactive", "connecting", "reconnecting"].includes(status)) return false;
  return null;
}
function parseMinerTelemetry(data, now = Date.now()) {
  const updated = numberOrNull(data?.last_update);
  const fresh = updated !== null && Math.abs(now / 1e3 - updated) <= 20;
  const algo = fresh && Array.isArray(data?.algos) ? data.algos.find((a) => a.name === "pearlhash") ?? null : null;
  const devices = fresh && Array.isArray(data?.devices) ? data.devices : [];
  const gpus = [0, 1].map((id) => {
    const position = devices.findIndex((d) => d.id === id);
    const device = position >= 0 ? devices[position] : null;
    const rate = position >= 0 ? numberOrNull(algo?.hashrates?.[position]) : null;
    const power = numberOrNull(device?.power);
    return {
      id,
      busId: numberOrNull(device?.bus_id),
      name: device?.name ?? `GPU ${id}`,
      hashrateTHs: rate === null ? null : rate / 1e12,
      acceptedShares: position < 0 ? null : numberOrNull(algo?.devices_accepted_shares?.[position]),
      rejectedShares: position < 0 ? null : numberOrNull(algo?.devices_rejected_shares?.[position]),
      tempC: numberOrNull(device?.temp),
      vramTempC: numberOrNull(device?.mem_temp),
      powerWatts: power,
      fanPercent: numberOrNull(device?.fan),
      cclkMHz: numberOrNull(device?.cclk),
      mclkMHz: numberOrNull(device?.mclk),
      sensorSource: device ? "miner-api" : null
    };
  });
  return {
    schemaVersion: 4,
    apiAvailable: fresh && algo !== null,
    nvmlAvailable: false,
    minerProcessRunning: fresh ? true : null,
    minerPid: null,
    sampledAt: fresh ? new Date(updated * 1e3).toISOString() : null,
    poolConnected: poolConnection(algo),
    poolUrl: algo?.pool ?? null,
    uptimeSec: fresh ? numberOrNull(data?.uptime_seconds) : null,
    totalHashrateTHs: numberOrNull(algo?.total_hashrate) === null ? null : algo.total_hashrate / 1e12,
    acceptedShares: numberOrNull(algo?.total_accepted_shares),
    rejectedShares: numberOrNull(algo?.total_rejected_shares),
    gpus
  };
}
export {
  numberOrNull,
  parseMinerTelemetry,
  poolConnection
};
