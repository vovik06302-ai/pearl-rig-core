import { RigControlPanel } from './components/RigControlPanel';
import { parseMinerTelemetry, gpuFromTelemetry, numberOrNull } from './utils/telemetry';
import React, { useState, useEffect, useMemo, useCallback } from 'react';
import {
  Download,
  Copy,
  Check,
  RefreshCw,
  ShieldAlert,
  ShieldCheck,
  Sliders,
  Cpu,
  AlertTriangle,
  FileText,
  Play,
  Square,
  Wallet,
  Lock,
  Edit3,
  CheckCircle2,
  XCircle,
  Activity,
} from 'lucide-react';
import {
  RigConfig,
  WatchdogPolicy,
  GpuLiveMetrics,
  GpuSettings,
  PoolLiveStatus,
  WatchdogEvent,
} from './types/rig';
import {
  validatePearlWallet,
  buildOneZeroMinerCommand,
  buildRemoteRigConfigJson,
  generateWatchdogBatScript,
  generateInnoSetupScript,
  generateGitHubWorkflowYaml,
  analyzeMinerLog,
} from './utils/scriptGenerator';
import {
  createEmptyGpuMetrics,
  runAllScenarioChecks,
} from './utils/scenarioVerifier';
import { SpeedometerGauge } from './components/SpeedometerGauge';

type ActiveTab = 'telemetry' | 'overclock' | 'diagnostics' | 'files';
type PreviewFileType = 'bat' | 'iss' | 'workflow' | 'json';

const STORAGE_CONFIG_KEY = 'pearl_rig_config_v3';
const STORAGE_POLICY_KEY = 'pearl_watchdog_policy_v3';
const STORAGE_WALLET_KEY = 'pearl_saved_public_wallet_v3';

function formatMetric(value: number | string | null | undefined, suffix = ''): string {
  if (value === null || value === undefined || value === '') {
    return 'нет данных';
  }
  if (typeof value === 'number' && !Number.isFinite(value)) {
    return 'нет данных';
  }
  return `${typeof value === 'number' ? value.toLocaleString('ru-RU', { maximumFractionDigits: 2 }) : value}${suffix}`;
}

export default function App() {
  const [activeTab, setActiveTab] = useState<ActiveTab>('telemetry');
  const [previewFile, setPreviewFile] = useState<PreviewFileType>('bat');

  // Загрузка локально сохранённого публичного адреса кошелька (игнорируя старый обрезанный адрес)
  const initialSavedWallet = useMemo(() => {
    try {
      const raw = localStorage.getItem(STORAGE_WALLET_KEY);
      if (!raw) return '';
      const parsed = JSON.parse(raw);
      const addr = typeof parsed?.address === 'string' ? parsed.address.trim() : '';
      const check = validatePearlWallet(addr);
      return check.valid ? addr : '';
    } catch {
      return '';
    }
  }, []);

  const [rigConfig, setRigConfig] = useState<RigConfig>(() => {
    const defaults: RigConfig = {
      miningActive: false, // НЕ запускаем майнер при установке или первом открытии
      userStoppedManually: false,
      minerDir:
        'C:\\Users\\User\\Documents\\Codex\\2026-10-25\\new-chat\\outputs\\Pearl\\miner\\onezerominer-win64-1.8.3',
      logFilePath:
        'C:\\Users\\User\\Documents\\Codex\\2026-10-25\\new-chat\\work\\pearl-miner.log',
      walletAddress: initialSavedWallet,
      walletSaved: Boolean(initialSavedWallet),
      workerName: 'dual4070ti',
      primaryPool: 'stratum+tcp://prl-ru.kryptex.network:7048',
      backupPool: 'stratum+tcp://prl-eu.kryptex.network:7048',
      apiPort: 3001,
      agentBridgePort: 3002,
      gpus: [
        {
          id: 0,
          name: 'NVIDIA GeForce RTX 4070 Ti',
          pciBus: '00:01:00',
          cclk: 2400,
          mclk: 810,
          pl: 150,
          fan: '65',
          kernel: 2,
        },
        {
          id: 1,
          name: 'NVIDIA GeForce RTX 4070 Ti',
          pciBus: '00:05:00',
          cclk: 2400,
          mclk: 810,
          pl: 150,
          fan: '*',
          kernel: 2,
        },
      ],
    };

    try {
      const saved = localStorage.getItem(STORAGE_CONFIG_KEY);
      if (!saved) return defaults;
      const parsed = JSON.parse(saved);
      return {
        ...defaults,
        minerDir: parsed.minerDir || defaults.minerDir,
        logFilePath: parsed.logFilePath || defaults.logFilePath,
        workerName: parsed.workerName || defaults.workerName,
        primaryPool: parsed.primaryPool || defaults.primaryPool,
        backupPool: parsed.backupPool || defaults.backupPool,
        apiPort: Number(parsed.apiPort) || 3001,
        agentBridgePort: Number(parsed.agentBridgePort) || 3002,
        walletAddress: initialSavedWallet,
        walletSaved: Boolean(initialSavedWallet),
        miningActive: false, // Никогда не включаем автозапуск при открытии
        userStoppedManually: Boolean(parsed.userStoppedManually),
        gpus: Array.isArray(parsed.gpus) && parsed.gpus.length === 2 ? parsed.gpus : defaults.gpus,
      };
    } catch {
      return defaults;
    }
  });

  const [policy, setPolicy] = useState<WatchdogPolicy>(() => {
    const defaults: WatchdogPolicy = {
      checkIntervalSec: 15,
      minHashrateTHs: 100,
      zeroHashrateRestartSec: 90,
      maxRestartAttempts: 3,
      maxTempC: 85,
      criticalStopTempC: 88,
      emergencyPlWatts: 125,
      telegramEnabled: false,
      telegramBotToken: '',
      telegramChatId: '',
    };
    try {
      const saved = localStorage.getItem(STORAGE_POLICY_KEY);
      if (!saved) return defaults;
      const parsed = JSON.parse(saved);
      return {
        ...defaults,
        ...parsed,
        maxTempC: Number(parsed.maxTempC) || 85,
        criticalStopTempC: Number(parsed.criticalStopTempC) || 88,
        emergencyPlWatts: Number(parsed.emergencyPlWatts) || 125,
        maxRestartAttempts: Number(parsed.maxRestartAttempts) || 3,
      };
    } catch {
      return defaults;
    }
  });

  useEffect(() => {
    localStorage.setItem(STORAGE_CONFIG_KEY, JSON.stringify(rigConfig));
  }, [rigConfig]);

  useEffect(() => {
    localStorage.setItem(STORAGE_POLICY_KEY, JSON.stringify(policy));
  }, [policy]);

  // Состояние ввода и подтверждения адреса кошелька
  const [draftWallet, setDraftWallet] = useState<string>(rigConfig.walletAddress);
  const [isEditingWallet, setIsEditingWallet] = useState<boolean>(!rigConfig.walletSaved);
  const [confirmingEditUnlock, setConfirmingEditUnlock] = useState<boolean>(false);
  const [walletStatusBanner, setWalletStatusBanner] = useState<{
    type: 'ok' | 'err' | 'info';
    text: string;
  } | null>(null);

  // Реальные метрики телеметрии (никаких выдуманных или случайных значений)
  const [gpuMetrics, setGpuMetrics] = useState<[GpuLiveMetrics, GpuLiveMetrics]>(() =>
    createEmptyGpuMetrics()
  );

  const [poolStatus, setPoolStatus] = useState<PoolLiveStatus>({
    connected: null,
    poolUrl: null,
    pingMs: null,
    uptimeSec: null,
    apiAvailable: false,
    nvmlAvailable: false,
    minerProcessRunning: null,
    minerPid: null,
    reconnectStage: 'idle',
    restartAttempts: 0,
    sharesAtRestart: 0,
    recoveryConfirmed: false,
    lastError: null,
  });

  const [copiedCode, setCopiedCode] = useState(false);
  const [copiedCmd, setCopiedCmd] = useState(false);
  const [rawLogInput, setRawLogInput] = useState<string>('');
  const [events, setEvents] = useState<WatchdogEvent[]>([
    {
      id: 'init-1',
      timestamp: new Date().toTimeString().slice(0, 8),
      category: 'PROCESS',
      severity: 'info',
      message: 'Панель открыта. Автозапуск майнера отключён.',
      actionTaken: rigConfig.walletSaved
        ? 'Загружен сохранённый публичный адрес кошелька. Ожидание команды пользователя «Запустить».'
        : 'Требуется ввести публичный адрес кошелька Pearl, проверить формат и нажать «Сохранить локально».',
    },
  ]);

  const addEvent = useCallback(
    (
      category: WatchdogEvent['category'],
      severity: WatchdogEvent['severity'],
      message: string,
      actionTaken: string
    ) => {
      const now = new Date().toTimeString().slice(0, 8);
      setEvents((prev) => [
        {
          id: `evt-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`,
          timestamp: now,
          category,
          severity,
          message,
          actionTaken,
        },
        ...prev.slice(0, 24),
      ]);
    },
    []
  );

  // Валидация сохранённого и чернового адреса
  const savedWalletValidation = useMemo(
    () => validatePearlWallet(rigConfig.walletAddress),
    [rigConfig.walletAddress]
  );

  const draftWalletValidation = useMemo(
    () => validatePearlWallet(draftWallet),
    [draftWallet]
  );

  const canStartMining =
    rigConfig.walletSaved && savedWalletValidation.valid && !isEditingWallet;

  // Опрос реальной телеметрии:
  // 1) Локальный агент 127.0.0.1:3002/telemetry (отдаёт данные API OneZeroMiner :3001 + nvidia-smi)
  // 2) Прямой опрос API OneZeroMiner 127.0.0.1:3001 (если запущен только майнер)
  // Если недоступно — выставляет null («нет данных»)
  const [totals, setTotals] = useState<{ hash: number | null; accepted: number | null; rejected: number | null }>({ hash: null, accepted: null, rejected: null });
  const [lastSampleAt, setLastSampleAt] = useState<string | null>(null);
  const pollRealTelemetry = useCallback(async () => {
    const fetchJson = async (url: string) => {
      const ctrl = new AbortController();
      const timer = setTimeout(() => ctrl.abort(), 4000);
      try {
        const res = await fetch(url, { signal: ctrl.signal, cache: 'no-store' });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return await res.json();
      } finally { clearTimeout(timer); }
    };
    let data: any = null;
    try {
      const bridge = await fetchJson('/api/telemetry');
      // Older agents use MH/s and a different per-GPU share mapping.
      const age = Date.now() - Date.parse(bridge.sampledAt);
      if (bridge.schemaVersion === 4 && Number.isFinite(age) && Math.abs(age) <= 20000) data = bridge;
    } catch { /* Try the miner's native API. */ }
    if (!data) {
      try { data = parseMinerTelemetry(await fetchJson(`http://127.0.0.1:${rigConfig.apiPort}/`)); }
      catch { data = parseMinerTelemetry({}); }
    }
    setPoolStatus({
      connected: typeof data.poolConnected === 'boolean' ? data.poolConnected : null,
      poolUrl: data.poolUrl ?? null, pingMs: null, uptimeSec: numberOrNull(data.uptimeSec),
      apiAvailable: data.apiAvailable === true, nvmlAvailable: data.nvmlAvailable === true,
      minerProcessRunning: typeof data.minerProcessRunning === 'boolean' ? data.minerProcessRunning : null,
      minerPid: numberOrNull(data.minerPid), reconnectStage: data.reconnectStage ?? 'idle',
      restartAttempts: data.restartAttempts ?? 0, sharesAtRestart: data.sharesAtRestart ?? 0,
      recoveryConfirmed: data.recoveryConfirmed === true, lastError: null,
    });
    setRigConfig(prev => ({ ...prev, miningActive: data.minerProcessRunning === true, userStoppedManually: data.userStoppedManually === true }));
    setTotals({ hash: numberOrNull(data.totalHashrateTHs), accepted: numberOrNull(data.acceptedShares), rejected: numberOrNull(data.rejectedShares) });
    setLastSampleAt(data.sampledAt ?? null);
    setGpuMetrics([0, 1].map(id => gpuFromTelemetry(data.gpus?.find((g: any) => g.id === id), id, policy.maxTempC)) as [GpuLiveMetrics, GpuLiveMetrics]);
  }, [rigConfig.agentBridgePort, rigConfig.apiPort, policy.maxTempC]);

  useEffect(() => {
    pollRealTelemetry();
    const interval = setInterval(pollRealTelemetry, 5000);
    return () => clearInterval(interval);
  }, [pollRealTelemetry]);

  // Вычисляемые агрегаты (только при наличии реальных данных, иначе null)
  const totalHashrate = totals.hash;

  const totalPowerWatts: number | null = useMemo(() => {
    const p0 = gpuMetrics[0].powerWatts;
    const p1 = gpuMetrics[1].powerWatts;
    if (p0 === null || p1 === null) return null;
    return Number(((p0 || 0) + (p1 || 0)).toFixed(1));
  }, [gpuMetrics]);

  const maxGpuTempC: number | null = useMemo(() => {
    const t0 = gpuMetrics[0].tempC;
    const t1 = gpuMetrics[1].tempC;
    if (t0 === null && t1 === null) return null;
    if (t0 !== null && t1 !== null) return Math.max(t0, t1);
    return t0 !== null ? t0 : t1;
  }, [gpuMetrics]);

  const totalAcceptedShares = totals.accepted;
  const totalRejectedShares = totals.rejected;

  // Обработчики для кошелька
  const handleCheckDraftWallet = () => {
    const res = validatePearlWallet(draftWallet);
    setWalletStatusBanner({
      type: res.valid ? 'info' : 'err',
      text: res.valid
        ? `${res.message} Нажмите «Сохранить локально», чтобы зафиксировать адрес перед запуском.`
        : res.message,
    });
  };

  const handleSaveWalletLocally = () => {
    const clean = draftWallet.trim();
    const res = validatePearlWallet(clean);
    if (!res.valid) {
      setWalletStatusBanner({
        type: 'err',
        text: `Сохранение отклонено: ${res.message}`,
      });
      addEvent('WALLET', 'error', 'Попытка сохранения неверного адреса кошелька отклонена.', res.message);
      return;
    }

    localStorage.setItem(
      STORAGE_WALLET_KEY,
      JSON.stringify({ address: clean, savedAt: new Date().toISOString() })
    );

    setRigConfig((prev) => ({
      ...prev,
      walletAddress: clean,
      walletSaved: true,
    }));
    setDraftWallet(clean);
    setIsEditingWallet(false);
    setConfirmingEditUnlock(false);
    setWalletStatusBanner({
      type: 'ok',
      text: `Публичный адрес кошелька сохранён локально (${clean.length} симв.). Кнопка «Запустить» разблокирована. Майнер ждёт вашей отдельной команды.`,
    });

    addEvent(
      'WALLET',
      'info',
      `Публичный адрес кошелька Pearl проверен и сохранён локально (${clean.length} симв.).`,
      `Адрес зафиксирован для локального запуска (-w ${clean}). Автозапуск не выполнялся.`
    );
  };

  const handleConfirmUnlockWalletEdit = () => {
    setDraftWallet(rigConfig.walletAddress);
    setIsEditingWallet(true);
    setConfirmingEditUnlock(false);
    setWalletStatusBanner({
      type: 'info',
      text: 'Режим изменения адреса открыт. Пока новый адрес не сохранён локально, запуск майнинга заблокирован.',
    });
  };

  const handleCancelWalletEdit = () => {
    if (rigConfig.walletSaved && savedWalletValidation.valid) {
      setDraftWallet(rigConfig.walletAddress);
      setIsEditingWallet(false);
      setConfirmingEditUnlock(false);
      setWalletStatusBanner(null);
    }
  };

  const handleLocalSession = useCallback((session: any) => {
    if (!session.configured) return;
    // Синхронизируем браузерное хранилище с закреплённой локальной конфигурацией
    // агента. Иначе после нового запуска UI снова показывал пустое поле кошелька,
    // хотя backend уже сохранил адрес.
    if (typeof session.wallet === 'string' && validatePearlWallet(session.wallet).valid) {
      localStorage.setItem(STORAGE_WALLET_KEY, JSON.stringify({ address: session.wallet, savedAt: new Date().toISOString(), source: 'local-agent' }));
    }
    setRigConfig(prev => ({ ...prev, walletAddress:session.wallet, walletSaved:true, primaryPool:session.pool,
      gpus: session.gpus.map((g: any, id: number) => ({ ...prev.gpus[id], ...g })) as [GpuSettings, GpuSettings] }));
    setDraftWallet(session.wallet);
    setIsEditingWallet(false);
    setConfirmingEditUnlock(false);
  }, []);

  // Кнопки «Запустить» и «Стоп»
  const handleStartMining = () => {
    if (!canStartMining) {
      setWalletStatusBanner({
        type: 'err',
        text: 'Запуск заблокирован: сначала введите корректный публичный адрес кошелька Pearl и нажмите «Сохранить локально».',
      });
      addEvent(
        'SECURITY',
        'error',
        'Запуск майнинга заблокирован из-за отсутствия сохранённого валидного адреса кошелька.',
        savedWalletValidation.message
      );
      return;
    }

    if (poolStatus.minerProcessRunning) {
      setRigConfig((prev) => ({
        ...prev,
        miningActive: true,
        userStoppedManually: false,
      }));
      addEvent(
        'PROCESS',
        'info',
        'Процесс OneZeroMiner уже запущен на ПК. Дублирующий экземпляр не запускается.',
        `Мониторинг подключён к работающему процессу (PID ${poolStatus.minerPid ?? 'активен'}).`
      );
      return;
    }

    setRigConfig((prev) => ({
      ...prev,
      miningActive: true,
      userStoppedManually: false,
    }));

    addEvent(
      'PROCESS',
      'info',
      'Команда «Запустить» зафиксирована в локальном профиле.',
      `В соответствии с защитой порта 3002 браузер не запускает процессы удалённо через GET. Для физического старта процесса на ПК нажмите «▶ ЗАПУСТИТЬ» в локальном окне pearl_watchdog_agent.bat или выполните сформированную строку запуска.`
    );
    pollRealTelemetry();
  };

  const handleStopMining = () => {
    addEvent('PROCESS', 'info', 'Для остановки используйте кнопку «Стоп» в локальном окне Rig Control.', 'Веб-панель только отображает телеметрию. Майнер продолжает работать.');
  };

  const handleResetDefaultOcProfile = () => {
    setRigConfig((prev) => ({
      ...prev,
      gpus: [
        {
          ...prev.gpus[0],
          cclk: 2400,
          mclk: 810,
          pl: 150,
          fan: '65',
          kernel: 2,
        },
        {
          ...prev.gpus[1],
          cclk: 2400,
          mclk: 810,
          pl: 150,
          fan: '*',
          kernel: 2,
        },
      ],
    }));
    addEvent(
      'OC_PROFILE',
      'info',
      'Восстановлен штатный профиль для двух RTX 4070 Ti (без повышения напряжения).',
      'PL: 150 Вт | Память: 810 МГц | Ядро: 2400 МГц | Kernel: 2 | Вентилятор GPU 0: 65%, GPU 1: Авто (*)'
    );
  };

  const generatedBatContent = useMemo(
    () => generateWatchdogBatScript(rigConfig, policy),
    [rigConfig, policy]
  );

  const generatedIssContent = useMemo(
    () => generateInnoSetupScript(rigConfig),
    [rigConfig]
  );

  const generatedWorkflowContent = useMemo(
    () => generateGitHubWorkflowYaml(),
    []
  );

  const generatedRemoteJson = useMemo(
    () => buildRemoteRigConfigJson(rigConfig, policy),
    [rigConfig, policy]
  );

  const activePreviewContent = useMemo(() => {
    if (previewFile === 'iss') return generatedIssContent;
    if (previewFile === 'workflow') return generatedWorkflowContent;
    if (previewFile === 'json') return generatedRemoteJson;
    return generatedBatContent;
  }, [previewFile, generatedBatContent, generatedIssContent, generatedWorkflowContent, generatedRemoteJson]);

  const generatedOneLineCmd = useMemo(
    () => buildOneZeroMinerCommand(rigConfig),
    [rigConfig]
  );

  const logDiagnostics = useMemo(
    () => analyzeMinerLog(rawLogInput, rigConfig.walletAddress),
    [rawLogInput, rigConfig.walletAddress]
  );

  const scenarioResults = useMemo(() => runAllScenarioChecks(), []);

  const handleDownloadBat = () => {
    const crlfContent = generatedBatContent.replace(/\r?\n/g, '\r\n');
    const blob = new Blob([crlfContent], { type: 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'pearl_watchdog_agent.bat';
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);

    addEvent(
      'PROCESS',
      'info',
      'Скачан локальный агент pearl_watchdog_agent.bat v3.0 (без автозапуска и без симуляции).',
      'При открытии на ПК агент сначала проверит сохранённый адрес кошелька и будет ждать нажатия «▶ ЗАПУСТИТЬ».'
    );
  };

  const handleDownloadActiveFile = () => {
    const filenameMap: Record<PreviewFileType, string> = {
      bat: 'pearl_watchdog_agent.bat',
      iss: 'installer.iss',
      workflow: 'build-installer.yml',
      json: 'rig-config.json',
    };
    const outText =
      previewFile === 'bat' || previewFile === 'iss'
        ? activePreviewContent.replace(/\r?\n/g, '\r\n')
        : activePreviewContent;
    const blob = new Blob([outText], { type: 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filenameMap[previewFile];
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
  };

  const handleCopyPreviewCode = () => {
    navigator.clipboard.writeText(activePreviewContent);
    setCopiedCode(true);
    setTimeout(() => setCopiedCode(false), 2000);
  };

  const handleCopyCmd = () => {
    navigator.clipboard.writeText(generatedOneLineCmd);
    setCopiedCmd(true);
    setTimeout(() => setCopiedCmd(false), 2000);
  };

  return (
    <div className="min-h-screen bg-[#0B0F17] text-slate-100 flex flex-col">
      {/* Верхняя панель */}
      <header className="flex flex-wrap items-center justify-between gap-4 px-6 py-4 border-b border-slate-800 bg-[#0E1420]">
        <div className="flex items-center gap-3">
          <span className="text-lg font-bold tracking-tight text-slate-100 font-display">
            Pearl Rig Control
          </span>
          <span className="px-2.5 py-0.5 text-[11px] font-mono rounded bg-slate-800 text-slate-300 border border-slate-700">
            2× RTX 4070 Ti · Без симуляции
          </span>
        </div>

        <nav className="flex flex-wrap items-center gap-5 text-sm font-medium text-slate-400">
          <button
            onClick={() => setActiveTab('telemetry')}
            className={`hover:text-slate-100 transition-colors pb-0.5 cursor-pointer ${
              activeTab === 'telemetry' ? 'text-slate-100 border-b-2 border-emerald-500' : ''
            }`}
          >
            Доход, температуры и состояние
          </button>
          <button
            onClick={() => setActiveTab('overclock')}
            className={`hover:text-slate-100 transition-colors pb-0.5 cursor-pointer ${
              activeTab === 'overclock' ? 'text-slate-100 border-b-2 border-emerald-500' : ''
            }`}
          >
            Настройки GPU
          </button>
          <button
            onClick={() => setActiveTab('diagnostics')}
            className={`hover:text-slate-100 transition-colors pb-0.5 cursor-pointer ${
              activeTab === 'diagnostics' ? 'text-slate-100 border-b-2 border-emerald-500' : ''
            }`}
          >
            Проверка сценариев (9/9) и Логи
          </button>
          <button
            onClick={() => setActiveTab('files')}
            className={`hover:text-slate-100 transition-colors pb-0.5 cursor-pointer ${
              activeTab === 'files' ? 'text-slate-100 border-b-2 border-emerald-500' : ''
            }`}
          >
            Исходники для ПК (.BAT / .ISS / .JSON)
          </button>
        </nav>

        <div className="flex items-center gap-2.5">
          <button
            onClick={handleDownloadBat}
            className="px-3.5 py-2 text-xs font-bold text-cyan-300 bg-cyan-500/15 border border-cyan-500/40 rounded-lg hover:bg-cyan-500/25 transition-colors flex items-center gap-2 cursor-pointer"
          >
            <Download className="w-3.5 h-3.5 text-cyan-400" />
            <span>Скачать pearl_watchdog_agent.bat</span>
          </button>
        </div>
      </header>

      <div className="flex-1 flex flex-col lg:flex-row max-w-[1440px] w-full mx-auto">
        {/* Боковая колонка реального статуса */}
        <aside className="w-full lg:w-[270px] shrink-0 border-b lg:border-b-0 lg:border-r border-slate-800 bg-[#0E1420]/60 p-5 flex flex-col justify-between gap-6">
          <div className="space-y-5">
            <div>
              <div className="text-xs text-slate-400">Воркер / Риг</div>
              <div className="mt-1 text-base font-semibold text-slate-100 font-mono">
                {rigConfig.workerName}
              </div>
              <div className="mt-1 text-xs font-mono">
                {poolStatus.minerProcessRunning ? (
                  <span className="text-emerald-400">
                    OneZeroMiner работает (PID {poolStatus.minerPid ?? '?'})
                  </span>
                ) : rigConfig.userStoppedManually ? (
                  <span className="text-amber-400">Намеренно остановлен (Стоп)</span>
                ) : (
                  <span className="text-slate-400">Ожидание команды запуска</span>
                )}
              </div>
            </div>

            <div className="p-3.5 rounded-lg bg-slate-900/80 border border-slate-800 space-y-2 text-xs">
              <div className="font-semibold text-slate-200">Источники реальных данных</div>
              <div className="flex items-center justify-between">
                <span className="text-slate-400">API :3001 (OneZeroMiner)</span>
                <span
                  className={`font-mono ${
                    poolStatus.apiAvailable ? 'text-emerald-400' : 'text-slate-500'
                  }`}
                >
                  {poolStatus.apiAvailable ? 'Доступен' : 'нет данных'}
                </span>
              </div>
              <div className="flex items-center justify-between">
                <span className="text-slate-400">nvidia-smi (GPU)</span>
                <span
                  className={`font-mono ${
                    poolStatus.nvmlAvailable ? 'text-emerald-400' : 'text-slate-500'
                  }`}
                >
                  {poolStatus.nvmlAvailable ? 'Доступен' : 'нет данных'}
                </span>
              </div>
              <div className="flex items-center justify-between">
                <span className="text-slate-400">Пул Kryptex</span>
                <span
                  className={`font-mono ${
                    poolStatus.connected === true
                      ? 'text-emerald-400'
                      : poolStatus.connected === false
                      ? 'text-red-400'
                      : 'text-slate-500'
                  }`}
                >
                  {poolStatus.connected === true
                    ? 'Connected'
                    : poolStatus.connected === false
                    ? 'Disconnected'
                    : 'нет данных'}
                </span>
              </div>
              <div className="flex items-center justify-between">
                <span className="text-slate-400">Кошелёк сохранён</span>
                <span
                  className={`font-mono ${
                    canStartMining ? 'text-emerald-400' : 'text-amber-400'
                  }`}
                >
                  {canStartMining ? 'Да (63 симв.)' : 'Требуется ввод'}
                </span>
              </div>
            </div>

            <div className="p-3.5 rounded-lg bg-slate-900/80 border border-slate-800 space-y-1.5 text-xs">
              <div className="font-semibold text-slate-200">Фиксированный профиль карт</div>
              <div className="text-slate-400 font-mono text-[11px] space-y-1">
                <div>GPU 0: {rigConfig.gpus[0].pl}W · {rigConfig.gpus[0].cclk}/810 MHz · Fan {rigConfig.gpus[0].fan}%</div>
                <div>GPU 1: {rigConfig.gpus[1].pl}W · {rigConfig.gpus[1].cclk}/810 MHz · Fan {rigConfig.gpus[1].fan === "*" ? "Авто" : rigConfig.gpus[1].fan+"%"}</div>
                <div>Порог перегрева: &ge; 85 °C</div>
              </div>
            </div>
          </div>

          <div className="pt-4 border-t border-slate-800 space-y-2 text-[11px] text-slate-400">
            <div className="flex items-center gap-1.5 text-emerald-400 font-medium">
              <Lock className="w-3.5 h-3.5 shrink-0" />
              <span>Защита локального интерфейса</span>
            </div>
            <p className="leading-relaxed">
              Порт 3002 отдаёт только чтение телеметрии (<code className="font-mono">/telemetry</code>).
              Команды через GET, открытый CORS (*) и удалённая смена кошелька отключены.
            </p>
          </div>
        </aside>

        {/* Основная рабочая область */}
        <main className="flex-1 p-6 lg:p-8 space-y-7 overflow-y-auto">
          {(activeTab === 'telemetry' || activeTab === 'overclock') && (
            <RigControlPanel mode={activeTab === 'telemetry' ? 'overview' : 'settings'} gpus={gpuMetrics} pool={poolStatus} onSession={handleLocalSession} onConfigure={() => setActiveTab('overclock')} onRefreshTelemetry={pollRealTelemetry} />
          )}

          {activeTab === 'diagnostics' && (
            <div className="space-y-6">
              {/* Автоматическая проверка 9 обязательных сценариев */}
              <div className="p-6 rounded-xl border border-slate-800 bg-[#111827] space-y-4">
                <div className="flex items-center justify-between pb-3 border-b border-slate-800">
                  <div className="flex items-center gap-2.5">
                    <Activity className="w-5 h-5 text-emerald-400" />
                    <div>
                      <h2 className="text-base font-bold text-white">
                        Автоматическая проверка 9 обязательных сценариев
                      </h2>
                      <p className="text-xs text-slate-400">
                        Детерминированные тесты логики запуска, валидатора кошелька, удержания кнопки «Стоп», перегрева &ge; 85 °C и отсутствия фейковых чисел
                      </p>
                    </div>
                  </div>
                  <span className="px-3 py-1 rounded-full text-xs font-mono font-bold bg-emerald-500/15 text-emerald-300 border border-emerald-500/30">
                    Пройдено: {scenarioResults.filter((r) => r.passed).length} / {scenarioResults.length}
                  </span>
                </div>

                <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
                  {scenarioResults.map((item) => (
                    <div
                      key={item.id}
                      className={`p-3.5 rounded-xl border space-y-1.5 text-xs ${
                        item.passed
                          ? 'bg-slate-900/80 border-emerald-500/30'
                          : 'bg-red-950/30 border-red-500/40'
                      }`}
                    >
                      <div className="flex items-center justify-between gap-2 font-bold text-slate-100">
                        <span>{item.title}</span>
                        {item.passed ? (
                          <CheckCircle2 className="w-4 h-4 text-emerald-400 shrink-0" />
                        ) : (
                          <XCircle className="w-4 h-4 text-red-400 shrink-0" />
                        )}
                      </div>
                      <p className="text-[11px] text-slate-400">{item.expected}</p>
                      <div className="text-[11px] font-mono text-emerald-300 pt-1">
                        {item.actual}
                      </div>
                    </div>
                  ))}
                </div>
              </div>

              {/* Анализатор реальных логов */}
              <div className="p-6 rounded-xl border border-slate-800 bg-[#111827] space-y-4">
                <div>
                  <h2 className="text-base font-bold text-white">
                    Разбор логов OneZeroMiner (pearl-miner.log) понятным языком
                  </h2>
                  <p className="text-xs text-slate-400 mt-0.5">
                    Вставьте строки из реального файла лога для расшифровки ошибок подключения к пулу или драйвера GPU
                  </p>
                </div>

                <textarea
                  rows={5}
                  value={rawLogInput}
                  onChange={(e) => setRawLogInput(e.target.value)}
                  placeholder="Вставьте строки из pearl-miner.log..."
                  className="w-full p-3.5 rounded-lg bg-slate-950 border border-slate-800 text-xs font-mono text-slate-200 focus:outline-none focus:border-emerald-500"
                />

                <div className="space-y-2.5">
                  {logDiagnostics.map((item, i) => (
                    <div
                      key={i}
                      className="p-3.5 rounded-lg bg-slate-900/80 border border-slate-800 space-y-1 text-xs"
                    >
                      <div className="flex items-center justify-between font-bold text-slate-100">
                        <span>{item.title}</span>
                        <span
                          className={`font-mono ${
                            item.severity === 'error'
                              ? 'text-red-400'
                              : item.severity === 'warning'
                              ? 'text-amber-400'
                              : 'text-emerald-400'
                          }`}
                        >
                          {item.severity.toUpperCase()}
                        </span>
                      </div>
                      <p className="text-slate-400">{item.detail}</p>
                      <div className="font-mono text-emerald-300">Рекомендация: {item.fixCommand}</div>
                    </div>
                  ))}
                </div>
              </div>
            </div>
          )}

          {activeTab === 'files' && (
            <div className="p-6 rounded-xl border border-slate-800 bg-[#111827] space-y-4">
              <div className="flex flex-col lg:flex-row lg:items-center justify-between gap-3">
                <div>
                  <h2 className="text-base font-bold text-white flex items-center gap-2">
                    <FileText className="w-4 h-4 text-emerald-400" />
                    <span>Исправленные файлы проекта для локального запуска на ПК</span>
                  </h2>
                  <p className="text-xs text-slate-400 mt-0.5">
                    Без обрезанного адреса, без автозапуска при установке, без изменения маршрутов/DNS/hosts и без удалённого управления из GitHub
                  </p>
                </div>

                <div className="flex flex-wrap items-center gap-1.5 p-1 bg-slate-900 rounded-lg border border-slate-800">
                  {(
                    [
                      ['bat', 'pearl_watchdog_agent.bat'],
                      ['json', 'rig-config.json'],
                      ['iss', 'installer.iss'],
                      ['workflow', 'build-installer.yml'],
                    ] as const
                  ).map(([key, label]) => (
                    <button
                      key={key}
                      onClick={() => setPreviewFile(key)}
                      className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors cursor-pointer ${
                        previewFile === key
                          ? 'bg-emerald-400 text-slate-950 font-semibold'
                          : 'text-slate-300 hover:text-white'
                      }`}
                    >
                      {label}
                    </button>
                  ))}
                </div>
              </div>

              <div className="flex items-center justify-end gap-2.5">
                <button
                  onClick={handleCopyPreviewCode}
                  className="px-3.5 py-2 text-xs font-medium text-slate-200 bg-slate-800 border border-slate-700 rounded-lg hover:bg-slate-700 flex items-center gap-2 cursor-pointer"
                >
                  {copiedCode ? <Check className="w-3.5 h-3.5 text-emerald-400" /> : <Copy className="w-3.5 h-3.5" />}
                  {copiedCode ? 'Скопировано' : 'Скопировать код'}
                </button>
                <button
                  onClick={handleDownloadActiveFile}
                  className="px-4 py-2 text-xs font-semibold text-slate-950 bg-emerald-400 rounded-lg hover:bg-emerald-300 flex items-center gap-2 cursor-pointer"
                >
                  <Download className="w-3.5 h-3.5" />
                  Скачать файл
                </button>
              </div>

              <pre className="p-4 rounded-lg bg-slate-950 border border-slate-800 text-xs font-mono text-slate-300 overflow-x-auto max-h-[460px] overflow-y-auto leading-relaxed">
                {activePreviewContent}
              </pre>
            </div>
          )}
        </main>
      </div>
    </div>
  );
}
