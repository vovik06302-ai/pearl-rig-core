import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { parseMinerTelemetry, poolConnection, gpuFromTelemetry } from '../src/utils/telemetry';

const fixture = JSON.parse(await readFile(new URL('./fixtures/telemetry.json', import.meta.url), 'utf8'));
const now = fixture.last_update * 1000;
test('converts H/s to TH/s and maps shares per card, preserving actual clocks', () => {
  const result = parseMinerTelemetry(fixture, now);
  assert.equal(result.totalHashrateTHs, 275.7621422328);
  assert.equal(result.gpus[0].hashrateTHs, 135.719651443886);
  assert.equal(result.gpus[1].hashrateTHs, 140.042490788914);
  assert.deepEqual(result.gpus.map(g => g.acceptedShares), [29, 28]);
  assert.deepEqual(result.gpus.map(g => g.rejectedShares), [1, 0]);
  assert.equal(result.gpus[0].cclkMHz, 2325);
  assert.equal(result.gpus[0].mclkMHz, 810);
  assert.equal(result.gpus[0].vramTempC, null);
  assert.equal(result.acceptedShares, 57);
});
test('maps metrics by device ID even when returned in reverse order', () => {
  const data = structuredClone(fixture);
  data.devices.reverse();
  data.algos[0].hashrates.reverse();
  data.algos[0].devices_accepted_shares.reverse();
  data.algos[0].devices_rejected_shares.reverse();
  assert.deepEqual(parseMinerTelemetry(data, now), parseMinerTelemetry(fixture, now));
});
test('Disconnected and unknown status are never inferred as Connected from hashrate', () => {
  assert.equal(poolConnection({ pool_status: 'Disconnected', total_hashrate: 1e14 }), false);
  assert.equal(poolConnection({ pool_status: 'Reconnecting' }), false);
  assert.equal(poolConnection({ total_hashrate: 1e14 }), null);
  assert.equal(poolConnection({ pool_status: 'Connected' }), true);
});
test('zero fan is valid, missing counters and unsupported sensors stay null', () => {
  const data = structuredClone(fixture);
  data.devices[0].fan = 0;
  delete data.algos[0].devices_accepted_shares;
  const result = parseMinerTelemetry(data, now);
  assert.equal(result.gpus[0].fanPercent, 0);
  assert.equal(result.gpus[0].acceptedShares, null);
  assert.equal(gpuFromTelemetry({ hashrateTHs: NaN, powerWatts: Infinity, tempC: 'N/A' }, 0, 85).tempC, null);
});
test('stale data is cleared instead of retaining old temperatures and share counters', () => {
  const result = parseMinerTelemetry(fixture, now + 21000);
  assert.equal(result.apiAvailable, false);
  assert.equal(result.totalHashrateTHs, null);
  assert.equal(result.gpus[0].tempC, null);
  assert.equal(result.acceptedShares, null);
});
test('generator embeds the exact tested sensor converter', async () => {
  const { build } = await import('esbuild');
  const bundle = await build({
    entryPoints: ['src/utils/scriptGenerator.ts'], bundle: true, write: false, platform: 'node', format: 'esm',
    plugins: [{ name: 'raw-ps', setup(builder) {
      builder.onResolve({ filter: /\.ps1\?raw$/ }, args => ({ path: new URL(args.path.replace('?raw', ''), `file:///${args.resolveDir.replaceAll('\\', '/')}/`).pathname.replace(/^\/([A-Za-z]:)/, '$1'), namespace: 'raw-ps' }));
      builder.onLoad({ filter: /.*/, namespace: 'raw-ps' }, async args => ({ contents: await readFile(args.path, 'utf8'), loader: 'text' }));
    } }],
  });
  const generator = await import(`data:text/javascript;base64,${Buffer.from(bundle.outputFiles[0].text).toString('base64')}`);
  const config = {
    minerDir: "C:\\User's Rig\\miner", logFilePath: 'C:\\rig\\miner.log', workerName: 'dual4070ti',
    primaryPool: fixture.algos[0].pool, backupPool: '', apiPort: 3001, agentBridgePort: 3002,
    gpus: [0, 1].map(id => ({ cclk: 2400, mclk: 810, pl: 150, kernel: 2, fan: id ? '*' : '65' })),
  };
  const policy = { checkIntervalSec:15, maxTempC:85, criticalStopTempC:88, emergencyPlWatts:125, zeroHashrateRestartSec:90, maxRestartAttempts:3 };
  const bat = generator.generateWatchdogBatScript(config, policy);
  const helper = await readFile('src/agent/telemetry.ps1', 'utf8');
  const checkedIn = await readFile('pearl_watchdog_agent.bat', 'utf8');
  assert.ok(bat.includes(helper));
  assert.ok(checkedIn.replaceAll('\r\n', '\n').includes(helper.replaceAll('\r\n', '\n')));
  assert.ok(bat.includes("$MinerDir = 'C:\\User''s Rig\\miner'"));
  assert.equal((bat.match(/-o \$Pool/g) ?? []).length, 2); // launch command plus human-readable log
  assert.ok(!bat.includes('Connected|Online|Active")'));
});
