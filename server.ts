import 'dotenv/config';
import express from 'express';
import { createServer as createViteServer } from 'vite';
import { readFile, writeFile, rename, mkdir, stat } from 'node:fs/promises';
import { randomBytes, randomUUID } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { fetchEarnings } from './scripts/pool-earnings.mjs';
import { fetchMarketRates } from './scripts/market-rates.mjs';
import { fetchMinerStatus } from './scripts/miner-status.mjs';
import { collectTelemetry } from './scripts/collect-telemetry.mjs';
import { validateProfile } from './scripts/control-policy.mjs';
import { runAdminControl } from './scripts/panel-server.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const stateRoot = process.env.PEARL_STATE_DIR || __dirname;
const configPath = path.join(stateRoot, 'rig-local.json');
const runtimeDir = path.join(stateRoot, '.runtime');

const WALLET_RE = /^prl1p[qpzry9x8gf2tvdw0s3jn54khce6mua7l]{58}$/;
const POOL_URL = 'stratum+tcp://prl-ru.kryptex.network:7048';
const isWindows = process.platform === 'win32';

const jsonRead = async (file: string) =>
  JSON.parse((await readFile(file, 'utf8')).replace(/^\uFEFF/, ''));

const jsonWrite = async (file: string, data: unknown) => {
  await writeFile(file + '.tmp', JSON.stringify(data, null, 2));
  await rename(file + '.tmp', file);
};

function isValidAbsolutePath(p: string): boolean {
  if (typeof p !== 'string' || !p.trim() || /["\r\n%]/.test(p)) return false;
  if (isWindows) return path.isAbsolute(p);
  return path.isAbsolute(p) || /^[A-Za-z]:[\\/]/.test(p);
}

async function startServer() {
  const app = express();
  const token = randomBytes(32).toString('hex');

  let memoryConfig: any = null;
  let job: any = null;
  let earnings: any = null;
  let earningsPromise: Promise<any> | null = null;
  let marketRates: any = null;
  let marketRatesPromise: Promise<any> | null = null;
  let minerStatus: any = null;
  let minerStatusPromise: Promise<any> | null = null;
  let telemetry: any = null;
  let telemetryPromise: Promise<any> | null = null;

  const configRead = async () => {
    const config = memoryConfig ?? (await jsonRead(configPath));
    if (!WALLET_RE.test(config.wallet) || config.pool !== POOL_URL) {
      throw new Error('Локальный кошелёк и пул не настроены');
    }
    config.gpus = validateProfile(config.gpus);
    memoryConfig = config;
    return config;
  };

  app.use(express.json({ limit: '16kb' }));

  app.use('/api', (req, res, next) => {
    res.setHeader('Cache-Control', 'no-store');
    res.setHeader('X-Content-Type-Options', 'nosniff');
    if (req.method === 'POST') {
      const headerToken = req.headers['x-pearl-token'];
      if (headerToken !== token) {
        return res.status(403).json({ error: 'Запрос управления должен идти из локальной панели' });
      }
    }
    next();
  });

  app.get('/api/telemetry', async (_req, res) => {
    try {
      if (!telemetry || Date.now() - Date.parse(telemetry.sampledAt) > 1500) {
        if (!telemetryPromise) {
          telemetryPromise = collectTelemetry()
            .then((value) => (telemetry = value))
            .finally(() => {
              telemetryPromise = null;
            });
        }
        await telemetryPromise;
      }
      res.json(telemetry);
    } catch (e: any) {
      res.status(500).json({ error: e?.message ?? 'Ошибка телеметрии' });
    }
  });

  app.get('/api/session', async (_req, res) => {
    try {
      const config = await configRead();
      res.json({
        configured: true,
        token,
        wallet: config.wallet,
        pool: config.pool,
        gpus: config.gpus,
        miningDesired: config.miningDesired,
        limits: { pl: [100, 150], cclk: [1800, 2400], mclk: 810, fan: [45, 100] },
      });
    } catch (e: any) {
      res.json({ configured: false, token, error: e?.message ?? 'Локальная конфигурация не задана' });
    }
  });

  app.post('/api/setup', async (req, res) => {
    try {
      await configRead();
      return res.status(409).json({ error: 'Кошелёк уже закреплён в локальной конфигурации' });
    } catch {
      /* First setup only. */
    }
    try {
      const body = req.body ?? {};
      if (!WALLET_RE.test(body.wallet ?? '')) {
        throw new Error('Введите полный публичный адрес prl1p из 63 символов');
      }
      const minerExe = String(body.minerExe ?? '');
      const baseExe = minerExe.split(/[\\/]/).pop()?.toLowerCase();
      if (!isValidAbsolutePath(minerExe) || baseExe !== 'onezerominer.exe') {
        throw new Error('Укажите полный путь к onezerominer.exe');
      }
      if (isWindows) {
        if (!(await stat(minerExe)).isFile()) {
          throw new Error('Файл майнера не найден');
        }
      }
      const logFile = String(body.logFile ?? '');
      if (!isValidAbsolutePath(logFile)) {
        throw new Error('Укажите полный путь журнала');
      }
      const config = {
        wallet: body.wallet,
        minerExe,
        logFile,
        pool: POOL_URL,
        worker: 'dual4070ti',
        miningDesired: false,
        gpus: [0, 1].map((id) => ({
          id,
          cclk: 2400,
          mclk: 810,
          pl: 150,
          kernel: 2,
          fan: id ? '*' : '65',
        })),
      };
      memoryConfig = config;
      try {
        await mkdir(stateRoot, { recursive: true });
        await jsonWrite(configPath, config);
      } catch {
        /* Keep in-memory config if filesystem is read-only */
      }
      res.json({ configured: true });
    } catch (e: any) {
      res.status(400).json({ error: e?.message ?? 'Ошибка настройки' });
    }
  });

  app.get('/api/earnings', async (req, res) => {
    let config;
    try {
      config = await configRead();
    } catch (e: any) {
      return res.status(400).json({ error: e?.message ?? 'Кошелёк не настроен' });
    }
    const forceRefresh = req.query.refresh === '1';
    if (forceRefresh) earnings = null;
    if (!earnings || Date.now() - Date.parse(earnings.fetchedAt) >= 60000) {
      if (!earningsPromise) {
        earningsPromise = fetchEarnings(config.wallet)
          .then((value) => (earnings = value))
          .finally(() => {
            earningsPromise = null;
          });
      }
      try {
        await earningsPromise;
      } catch (e: any) {
        return res
          .status(earnings ? 200 : 503)
          .json({ ...earnings, stale: true, error: e?.message ?? 'Kryptex недоступен' });
      }
    }
    res.json({ ...earnings, stale: false });
  });

  app.get('/api/market-rates', async (req, res) => {
    const forceRefresh = req.query.refresh === '1';
    if (forceRefresh) marketRates = null;
    if (!marketRates || Date.now() - Date.parse(marketRates.fetchedAt) >= 900000) {
      if (!marketRatesPromise) {
        marketRatesPromise = fetchMarketRates()
          .then((value) => (marketRates = value))
          .finally(() => {
            marketRatesPromise = null;
          });
      }
      try {
        await marketRatesPromise;
      } catch (e: any) {
        return res
          .status(marketRates ? 200 : 503)
          .json({ ...marketRates, stale: true, error: e?.message ?? 'Курс недоступен' });
      }
    }
    res.json({ ...marketRates, stale: false });
  });

  app.get('/api/miner-status', async (_req, res) => {
    let config;
    try {
      config = await configRead();
    } catch {
      return res.json({
        state: 'stopped',
        message: 'Ожидает первичной настройки кошелька на ПК',
        processRunning: false,
        rootCount: 0,
        pids: [],
        apiAvailable: false,
        poolConnected: null,
        hashing: false,
        uptimeSec: null,
        accepted: null,
        rejected: null,
        invalid: null,
        sampledAt: new Date().toISOString(),
        errors: [],
        tail: [],
      });
    }
    if (!isWindows) {
      return res.json({
        state: 'warning',
        message: 'Мониторинг процессов Windows недоступен в веб-контейнере',
        processRunning: null,
        rootCount: 0,
        pids: [],
        apiAvailable: false,
        poolConnected: null,
        hashing: false,
        uptimeSec: null,
        accepted: null,
        rejected: null,
        invalid: null,
        sampledAt: new Date().toISOString(),
        errors: [],
        tail: [],
        logError: 'Для чтения локального журнала и управления майнером запустите панель на Windows-риге.',
      });
    }
    if (!minerStatus || Date.now() - Date.parse(minerStatus.sampledAt) > 4000) {
      if (!minerStatusPromise) {
        minerStatusPromise = fetchMinerStatus(config)
          .then((value) => (minerStatus = value))
          .finally(() => {
            minerStatusPromise = null;
          });
      }
      await minerStatusPromise;
    }
    res.json(minerStatus);
  });

  app.get('/api/control', async (_req, res) => {
    if (job?.busy) {
      try {
        const progress = await jsonRead(job.resultPath);
        return res.json({ ...job, ...progress, busy: true, resultPath: undefined });
      } catch {
        /* UAC or worker startup. */
      }
    }
    res.json(job ? { ...job, resultPath: undefined } : { busy: false, status: 'idle' });
  });

  app.post('/api/control', async (req, res) => {
    if (job?.busy) {
      return res.status(409).json({ error: 'Предыдущая операция ещё выполняется' });
    }
    try {
      const body = req.body ?? {};
      if (
        Object.keys(body).some((k) => !['action', 'gpus'].includes(k)) ||
        !['apply', 'start', 'stop', 'validate'].includes(body.action)
      ) {
        return res.status(400).json({ error: 'Неизвестная команда' });
      }
      const config = await configRead();
      const gpus =
        body.action === 'apply' || body.action === 'validate'
          ? validateProfile(body.gpus)
          : config.gpus;
      const id = randomUUID();

      if (!isWindows) {
        if (body.action === 'validate') {
          job = {
            id,
            busy: false,
            action: body.action,
            status: 'success',
            message: 'Профиль GPU прошёл проверку ограничений (PL 100–150 Вт, CCLK 1800–2400 МГц, MCLK 810 МГц, Kernel 2).',
          };
          return res.status(200).json(job);
        }
        job = {
          id,
          busy: false,
          action: body.action,
          status: 'unverified',
          message:
            'Физическое управление onezerominer.exe доступно только при локальном запуске панели на Windows-риге (Start-Pearl-Rig-Control.bat).',
        };
        return res.status(200).json(job);
      }

      const folder = path.join(runtimeDir, id);
      await mkdir(folder, { recursive: true });
      const requestPath = path.join(folder, 'request.json');
      const resultPath = path.join(folder, 'result.json');
      await jsonWrite(requestPath, { action: body.action, gpus });
      job = {
        id,
        busy: true,
        action: body.action,
        status: 'waiting_for_uac',
        message: 'Ожидание прав администратора Windows',
        resultPath,
      };
      res.status(202).json({ ...job, resultPath: undefined });

      (async () => {
        try {
          const result = await runAdminControl({
            requestPath,
            resultPath,
            scriptRoot: path.join(__dirname, 'scripts'),
            configPath,
          });
          if (result.applied) {
            config.gpus = validateProfile(result.gpus);
            config.miningDesired = true;
            await jsonWrite(configPath, config);
          } else if (result.stopped) {
            config.miningDesired = false;
            await jsonWrite(configPath, config);
          }
          job = { id, action: body.action, ...result, busy: false };
        } catch (e: any) {
          job = { id, action: body.action, busy: false, status: 'failed', message: e.message };
        }
      })();
    } catch (e: any) {
      res.status(400).json({ error: e?.message ?? 'Ошибка команды' });
    }
  });

  if (process.env.NODE_ENV !== 'production') {
    const vite = await createViteServer({
      server: { middlewareMode: true },
      appType: 'spa',
    });
    app.use(vite.middlewares);
  } else {
    const distDir = path.join(__dirname, 'dist');
    app.use(express.static(distDir));
    app.get('*', (_req, res) => {
      res.sendFile(path.join(distDir, 'index.html'));
    });
  }

  const port = Number(process.env.PORT) || 3000;
  app.listen(port, '0.0.0.0', () => {
    console.log(`Pearl Rig Control listening on http://0.0.0.0:${port}`);
  });
}

startServer().catch((err) => {
  console.error('Failed to start Pearl Rig Control server:', err);
  process.exit(1);
});
