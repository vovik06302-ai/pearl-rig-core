import { open } from 'node:fs/promises';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import path from 'node:path';
const executeFile = promisify(execFile);

export function classifyMiner(processes, api, now = Date.now()) {
  const roots = processes.filter(p => !processes.some(parent => parent.ProcessId === p.ParentProcessId));
  const fresh = typeof api?.last_update === 'number' && Math.abs(now / 1000 - api.last_update) <= 20;
  const algo = fresh ? api.algos?.find(a => a.name === 'pearlhash') : null;
  const connected = algo?.pool_status?.toLowerCase() === 'connected';
  const hashing = Array.isArray(algo?.hashrates) && algo.hashrates.length === 2 && algo.hashrates.every(h => typeof h === 'number' && h > 0);
  let state = 'healthy';
  let message = 'Майнер работает: пул подключён, обе карты считают';
  if (!processes.length) { state = 'stopped'; message = 'Майнер остановлен'; }
  else if (roots.length > 1) { state = 'error'; message = 'Обнаружено несколько независимых запусков майнера'; }
  else if (!fresh || !algo) { state = 'warning'; message = 'Процесс работает, но свежие данные API недоступны'; }
  else if (!connected) { state = 'warning'; message = 'Процесс работает, подключения к пулу нет'; }
  else if (!hashing) { state = 'warning'; message = 'Пул подключён, но не обе карты показывают ненулевой хешрейт'; }
  return { state, message, processRunning: processes.length > 0, rootCount: roots.length, pids: processes.map(p => p.ProcessId), apiAvailable: fresh && Boolean(algo), poolConnected: algo ? connected : null, hashing, uptimeSec: fresh ? api.uptime_seconds : null, accepted: algo?.total_accepted_shares ?? null, rejected: algo?.total_rejected_shares ?? null, invalid: algo?.total_invalid_shares ?? null, sampledAt: new Date(now).toISOString() };
}

export function parseMinerLog(text) {
  const marker = text.lastIndexOf('Miner started at ');
  const lines = (marker >= 0 ? text.slice(marker) : text).split(/\r?\n/).filter(Boolean);
  const errors = lines.filter(line => /\[(?:Error|Warn|Warning)\]|\b(?:fatal|failed|timed out|timeout)\b|share rejected|invalid share/i.test(line))
    .slice(-12).reverse().map(line => ({ text: line.slice(0, 1200), severity: /\[Error\]|fatal|failed|timed out|timeout/i.test(line) ? 'error' : 'warning' }));
  return { errors, tail: lines.slice(-35).map(line => line.slice(0, 1200)) };
}

export async function readLogTail(filename) {
  const file = await open(filename, 'r');
  try {
    const size = (await file.stat()).size;
    const buffer = Buffer.alloc(Math.min(size, 128 * 1024));
    const { bytesRead } = await file.read(buffer, 0, buffer.length, Math.max(0, size - buffer.length));
    return parseMinerLog(buffer.subarray(0, bytesRead).toString('utf8'));
  } finally { await file.close(); }
}

export async function fetchMinerStatus(config) {
  const powerShell = path.join(process.env.SystemRoot ?? 'C:\\Windows', 'System32/WindowsPowerShell/v1.0/powershell.exe');
  const results = await Promise.allSettled([
    executeFile(powerShell, ['-NoProfile','-Command', '$p=@(Get-CimInstance Win32_Process -Filter "name=\'onezerominer.exe\'" | Select-Object ProcessId,ParentProcessId); ConvertTo-Json -InputObject $p -Compress'], { windowsHide:true, timeout:6000 }),
    fetch('http://127.0.0.1:3001/', { signal:AbortSignal.timeout(3000) }).then(r => { if (!r.ok) throw new Error('API HTTP '+r.status); return r.json(); }),
    readLogTail(config.logFile),
  ]);
  const processes = results[0].status === 'fulfilled' ? JSON.parse(results[0].value.stdout.replace(/^\uFEFF/, '').trim() || '[]') : [];
  const status = classifyMiner(processes, results[1].status === 'fulfilled' ? results[1].value : null);
  if (results[0].status === 'rejected') { status.processRunning = null; status.state='warning'; status.message='Невозможно проверить список процессов'; }
  return { ...status, ...(results[2].status === 'fulfilled' ? results[2].value : { errors:[], tail:[], logError:'Журнал майнера недоступен' }) };
}
