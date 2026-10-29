import http from 'node:http';
import { readFile, writeFile, rename, mkdir, copyFile, stat } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { randomBytes, randomUUID } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import path from 'node:path';
import { fetchEarnings } from './pool-earnings.mjs';
import { fetchMarketRates } from './market-rates.mjs';
import { fetchMinerStatus } from './miner-status.mjs';
import { collectTelemetry } from './collect-telemetry.mjs';
import { authorizeRequest, validateProfile } from './control-policy.mjs';

const executeFile = promisify(execFile);
const home = fileURLToPath(new URL('../', import.meta.url));
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.ico': 'image/x-icon' };
const jsonRead = async file => JSON.parse((await readFile(file, 'utf8')).replace(/^\uFEFF/, ''));
const jsonWrite = async (file, data) => {
  await writeFile(file + '.tmp', JSON.stringify(data, null, 2));
  await rename(file + '.tmp', file);
};

export async function runAdminControl({ requestPath, resultPath, scriptRoot, configPath }) {
  const powerShell = path.join(process.env.SystemRoot ?? 'C:\\Windows', 'System32/WindowsPowerShell/v1.0/powershell.exe');
  try {
    await executeFile(powerShell, ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(scriptRoot, 'elevate-control.ps1'), '-RequestPath', requestPath, '-ResultPath', resultPath, '-ConfigPath', configPath], { windowsHide: true, timeout: 360000 });
  } catch (error) {
    try { return await jsonRead(resultPath); }
    catch { return { status: 'failed', message: 'Запрос администратора отклонён или операция не завершилась: ' + error.message, applied: false }; }
  }
  return jsonRead(resultPath);
}

async function persistLauncher(config) {
  if (!config.launcherPath) return;
  const old = await readFile(config.launcherPath, 'utf8');
  if (!old.includes(config.wallet) || !old.includes('onezerominer.exe')) throw new Error('Рабочий BAT не прошёл проверку; не изменён');
  try { await copyFile(config.launcherPath, config.launcherPath + '.before-rig-control.bak', 1); } catch (e) { if (e.code !== 'EEXIST') throw e; }
  const pair = key => config.gpus.map(g => g[key]).join(',');
  const line = `onezerominer.exe -a pearl -w ${config.wallet} -o ${config.pool} --worker ${config.worker} --cclk ${pair('cclk')} --mclk 810,810 --pl ${pair('pl')} --kernel 2,2 --fan ${pair('fan')} --api-port 3001 --log-file "${config.logFile}"`;
  const body = `@echo off\r\ncd /d "${path.dirname(config.minerExe)}"\r\nif errorlevel 1 exit /b 1\r\n${line}\r\npause\r\n`;
  await writeFile(config.launcherPath, body);
}

export function createPanelServer({ base = home, stateRoot = process.env.PEARL_STATE_DIR || base, runControl = runAdminControl, loadEarnings = fetchEarnings, loadMarketRates = fetchMarketRates, loadMinerStatus = fetchMinerStatus, loadTelemetry = collectTelemetry } = {}) {
  const root = path.join(base, 'dist');
  const configPath = path.join(stateRoot, 'rig-local.json');
  const runtime = path.join(stateRoot, '.runtime');
  const token = randomBytes(32).toString('hex');
  let job = null;
  let earnings = null;
  let earningsPromise = null;
  let marketRates = null;
  let marketRatesPromise = null;
  let minerStatus = null;
  let minerStatusPromise = null;
  let telemetry = null;
  let telemetryPromise = null;
  const configRead = async () => {
    const config = await jsonRead(configPath);
    if (!/^prl1p[qpzry9x8gf2tvdw0s3jn54khce6mua7l]{58}$/.test(config.wallet) || config.pool !== 'stratum+tcp://prl-ru.kryptex.network:7048') throw new Error('Локальный кошелёк и пул не настроены');
    config.gpus = validateProfile(config.gpus);
    return config;
  };
  const json = (res, status, value) => { res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff', 'X-Frame-Options': 'DENY' }); res.end(JSON.stringify(value)); };
  const readBody = async req => {
    let text = '';
    for await (const chunk of req) { text += chunk; if (Buffer.byteLength(text) > 4096) throw new Error('Слишком большой запрос'); }
    return JSON.parse(text);
  };

  return http.createServer(async (req, res) => {
    try {
      if (!authorizeRequest(req, token)) return json(res, 403, { error: 'Запрос управления должен идти из локальной панели' });
      const url = new URL(req.url, 'http://127.0.0.1:3000');
      if (req.method === 'GET' && url.pathname === '/api/telemetry') {
        if (!telemetry || Date.now() - Date.parse(telemetry.sampledAt) > 1500) {
          if (!telemetryPromise) telemetryPromise=loadTelemetry().then(value=>(telemetry=value)).finally(()=>{telemetryPromise=null;});
          await telemetryPromise;
        }
        return json(res,200,telemetry);
      }
      if (req.method === 'GET' && url.pathname === '/api/session') {
        try {
          const config = await configRead();
          return json(res, 200, { configured: true, token, wallet: config.wallet, pool: config.pool, gpus: config.gpus, miningDesired: config.miningDesired, limits: { pl:[100,150], cclk:[1800,2400], mclk:810, fan:[45,100] } });
        } catch (e) { return json(res, 200, { configured: false, token, error: e.message }); }
      }
      if (req.method === 'POST' && url.pathname === '/api/setup') {
        try { await configRead(); return json(res, 409, {error:'Кошелёк уже закреплён в локальной конфигурации'}); } catch { /* First setup only. */ }
        const body = await readBody(req);
        if (!/^prl1p[qpzry9x8gf2tvdw0s3jn54khce6mua7l]{58}$/.test(body.wallet ?? '')) throw new Error('Введите полный публичный адрес prl1p из 63 символов');
        if (typeof body.minerExe !== 'string' || !path.isAbsolute(body.minerExe) || path.basename(body.minerExe).toLowerCase() !== 'onezerominer.exe' || /[\"\r\n%]/.test(body.minerExe)) throw new Error('Укажите полный путь к onezerominer.exe');
        if (!(await stat(body.minerExe)).isFile()) throw new Error('Файл майнера не найден');
        if (typeof body.logFile !== 'string' || !path.isAbsolute(body.logFile) || /[\"\r\n%]/.test(body.logFile)) throw new Error('Укажите полный путь журнала');
        const config={wallet:body.wallet,minerExe:body.minerExe,logFile:body.logFile,pool:'stratum+tcp://prl-ru.kryptex.network:7048',worker:'dual4070ti',miningDesired:false,gpus:[0,1].map(id=>({id,cclk:2400,mclk:810,pl:150,kernel:2,fan:id?'*':'65'}))};
        await mkdir(stateRoot,{recursive:true}); await jsonWrite(configPath,config);
        return json(res,200,{configured:true});
      }
      if (req.method === 'GET' && url.pathname === '/api/earnings') {
        const config = await configRead();
        const forceRefresh = url.searchParams.get('refresh') === '1';
        if (forceRefresh) earnings = null;
        if (!earnings || Date.now() - Date.parse(earnings.fetchedAt) >= 60000) {
          if (!earningsPromise) earningsPromise = loadEarnings(config.wallet).then(value => (earnings = value)).finally(() => { earningsPromise = null; });
          try { await earningsPromise; }
          catch (e) { return json(res, earnings ? 200 : 503, { ...earnings, stale: true, error: e.message }); }
        }
        return json(res, 200, { ...earnings, stale: false });
      }
      if (req.method === 'GET' && url.pathname === '/api/market-rates') {
        const forceRefresh = url.searchParams.get('refresh') === '1';
        if (forceRefresh) marketRates = null;
        if (!marketRates || Date.now() - Date.parse(marketRates.fetchedAt) >= 900000) {
          if (!marketRatesPromise) marketRatesPromise = loadMarketRates().then(value => (marketRates = value)).finally(() => { marketRatesPromise = null; });
          try { await marketRatesPromise; }
          catch (e) { return json(res, marketRates ? 200 : 503, { ...marketRates, stale: true, error: e.message }); }
        }
        return json(res, 200, { ...marketRates, stale: false });
      }
      if (req.method === 'GET' && url.pathname === '/api/miner-status') {
        const config = await configRead();
        if (!minerStatus || Date.now() - Date.parse(minerStatus.sampledAt) > 4000) {
          if (!minerStatusPromise) minerStatusPromise = loadMinerStatus(config).then(value => (minerStatus=value)).finally(() => { minerStatusPromise=null; });
          await minerStatusPromise;
        }
        return json(res, 200, minerStatus);
      }
      if (req.method === 'GET' && url.pathname === '/api/control') {
        if (job?.busy) {
          try { const progress = await jsonRead(job.resultPath); return json(res, 200, { ...job, ...progress, busy: true, resultPath: undefined }); } catch { /* UAC or worker startup. */ }
        }
        return json(res, 200, job ? { ...job, resultPath: undefined } : { busy: false, status: 'idle' });
      }
      if (req.method === 'POST' && url.pathname === '/api/control') {
        if (job?.busy) return json(res, 409, { error: 'Предыдущая операция ещё выполняется' });
        const body = await readBody(req);
        if (Object.keys(body).some(k => !['action','gpus'].includes(k)) || !['apply','start','stop','validate'].includes(body.action)) return json(res, 400, { error: 'Неизвестная команда' });
        const config = await configRead();
        const gpus = body.action === 'apply' || body.action === 'validate' ? validateProfile(body.gpus) : config.gpus;
        const id = randomUUID();
        const folder = path.join(runtime, id);
        await mkdir(folder, { recursive: true });
        const requestPath = path.join(folder, 'request.json');
        const resultPath = path.join(folder, 'result.json');
        await jsonWrite(requestPath, { action: body.action, gpus });
        job = { id, busy: true, action: body.action, status: 'waiting_for_uac', message: 'Ожидание прав администратора Windows', resultPath };
        json(res, 202, { ...job, resultPath: undefined });
        (async () => {
          try {
            const result = await runControl({ requestPath, resultPath, scriptRoot: path.join(base, 'scripts'), configPath });
            if (result.applied) {
              config.gpus = validateProfile(result.gpus);
              config.miningDesired = true;
              await jsonWrite(configPath, config);
              try { await persistLauncher(config); } catch (e) { result.message += '. Профиль сохранён, но BAT не обновлён: '+e.message; result.persistenceWarning=true; }
            } else if (result.stopped) {
              config.miningDesired = false;
              await jsonWrite(configPath, config);
            }
            job = { id, action: body.action, ...result, busy: false };
          } catch (e) { job = { id, action: body.action, busy: false, status: 'failed', message: e.message }; }
        })();
        return;
      }
      if (!['GET', 'HEAD'].includes(req.method)) return json(res, 405, { error: 'Метод не поддерживается' });
      const filename = path.resolve(root, '.' + decodeURIComponent(url.pathname === '/' ? '/index.html' : url.pathname));
      const relative = path.relative(root, filename);
      if (relative.startsWith('..') || path.isAbsolute(relative)) return json(res, 403, { error: 'Недоступно' });
      const body = await readFile(filename);
      res.writeHead(200, { 'Content-Type': types[path.extname(filename)] ?? 'application/octet-stream', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff', 'X-Frame-Options': 'DENY' });
      res.end(req.method === 'HEAD' ? undefined : body);
    } catch (e) { json(res, e.code === 'ENOENT' ? 404 : 400, { error: e.message }); }
  });
}
