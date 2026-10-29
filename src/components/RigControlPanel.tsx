import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { GpuLiveMetrics, GpuSettings, PoolLiveStatus } from '../types/rig';
import { SpeedometerGauge } from './SpeedometerGauge';

type Profile = Pick<GpuSettings, 'id'|'cclk'|'mclk'|'pl'|'kernel'|'fan'>;
type Props = {
  mode: 'overview' | 'settings';
  gpus: [GpuLiveMetrics, GpuLiveMetrics];
  pool: PoolLiveStatus;
  onSession: (session: any) => void;
  onConfigure: () => void;
  onRefreshTelemetry?: () => Promise<void>;
};
const prl = (value: unknown) => typeof value === 'number' && Number.isFinite(value)
  ? value.toLocaleString('ru-RU', { minimumFractionDigits: 8, maximumFractionDigits: 8 }) : 'нет данных';
const metric = (value: number | null, suffix: string) => value === null ? 'нет данных' : value.toLocaleString('ru-RU', { maximumFractionDigits: 2 }) + suffix;

export function RigControlPanel({ mode, gpus, pool, onSession, onConfigure, onRefreshTelemetry }: Props) {
  const [session, setSession] = useState<any>(null);
  const [draft, setDraft] = useState<Profile[]>([]);
  const [earnings, setEarnings] = useState<any>(null);
  const [job, setJob] = useState<any>({ busy: false, status: 'idle' });
  const [minerStatus, setMinerStatus] = useState<any>(null);
  const [error, setError] = useState('');
  const [setup, setSetup] = useState({ wallet:'', minerExe:'C:\\Pearl\\miner\\onezerominer.exe', logFile:'C:\\Pearl\\miner.log' });
  const [refreshing, setRefreshing] = useState(false);
  const [fullscreen, setFullscreen] = useState(false);
  const [fallbackFullscreen, setFallbackFullscreen] = useState(false);
  const [rates, setRates] = useState(() => {
    try {
      const saved = JSON.parse(localStorage.getItem('pearl_panel_rates_v1') ?? '{}');
      return { kwhRub: typeof saved.kwhRub === 'number' ? saved.kwhRub : 0 };
    } catch { return { kwhRub: 0 }; }
  });
  const [marketRates, setMarketRates] = useState<any>(null);
  const [energyKwh, setEnergyKwh] = useState(() => {
    try { const value = Number(localStorage.getItem('pearl_measured_gpu_kwh_v1')); return Number.isFinite(value) && value >= 0 ? value : 0; }
    catch { return 0; }
  });
  const previousPower = useRef<{ watts: number; at: number } | null>(null);
  const totalPowerWatts = useMemo(() => {
    const values = gpus.map(gpu => gpu.powerWatts).filter((value): value is number => typeof value === 'number' && Number.isFinite(value) && value >= 0);
    return values.length ? values.reduce((sum, value) => sum + value, 0) : null;
  }, [gpus]);
  const totalPowerKw = totalPowerWatts === null ? null : totalPowerWatts / 1000;
  const powerCostPerHour = totalPowerKw !== null && rates.kwhRub > 0 ? totalPowerKw * rates.kwhRub : null;
  const measuredCost = rates.kwhRub > 0 ? energyKwh * rates.kwhRub : null;
  const confirmedValueRub = marketRates?.prlRub > 0 && typeof earnings?.confirmed === 'number' ? earnings.confirmed * marketRates.prlRub : null;
  const balanceValueRub = marketRates?.prlRub > 0 && typeof earnings?.balance === 'number' ? earnings.balance * marketRates.prlRub : null;

  useEffect(() => { localStorage.setItem('pearl_panel_rates_v1', JSON.stringify(rates)); }, [rates]);
  useEffect(() => { localStorage.setItem('pearl_measured_gpu_kwh_v1', String(energyKwh)); }, [energyKwh]);
  useEffect(() => {
    if (totalPowerWatts === null) { previousPower.current = null; return; }
    const now = Date.now();
    const previous = previousPower.current;
    if (previous) {
      const seconds = (now - previous.at) / 1000;
      if (seconds > 0 && seconds <= 300) setEnergyKwh(value => value + (previous.watts * seconds) / 3_600_000);
    }
    previousPower.current = { watts: totalPowerWatts, at: now };
  }, [totalPowerWatts]);

  const refreshMarketRates = async (force = false) => {
    const response = await fetch(`/api/market-rates${force ? '?refresh=1' : ''}`, { cache: 'no-store' });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error ?? 'Официальный курс PRL недоступен');
    setMarketRates(data);
    return data;
  };

  useEffect(() => {
    if (mode !== 'overview') return;
    refreshMarketRates().catch(e => setMarketRates((old: any) => ({ ...old, stale: true, error: (e as Error).message })));
    const timer = setInterval(() => refreshMarketRates().catch(e => setMarketRates((old: any) => ({ ...old, stale: true, error: (e as Error).message }))), 900000);
    return () => clearInterval(timer);
  }, [mode]);

  const refreshAll = async () => {
    setRefreshing(true);
    try {
      await Promise.all([
        loadSession(),
        onRefreshTelemetry?.(),
        refreshMarketRates(true),
        fetch('/api/earnings?refresh=1', { cache: 'no-store' }).then(async response => { const data = await response.json(); if (!response.ok) throw new Error(data.error ?? 'Нет данных о начислениях'); setEarnings(data); }),
      ]);
      setError('');
    } catch (e) { setError((e as Error).message); }
    finally { setRefreshing(false); }
  };
  const toggleFullscreen = async () => {
    try {
      if (document.fullscreenElement) { await document.exitFullscreen(); setFullscreen(false); return; }
      if (fallbackFullscreen) { setFallbackFullscreen(false); return; }
      if (typeof document.documentElement.requestFullscreen !== 'function') throw new Error('Fullscreen API недоступен');
      await document.documentElement.requestFullscreen();
      setFullscreen(true);
    } catch {
      // Резервный режим работает в браузерах/оболочках, где Fullscreen API запрещён.
      setFallbackFullscreen(true);
      setError('Включён резервный полноэкранный режим панели. Для выхода нажмите ту же кнопку или Esc.');
    }
  };
  useEffect(() => {
    const update = () => setFullscreen(Boolean(document.fullscreenElement));
    document.addEventListener('fullscreenchange', update);
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape' && fallbackFullscreen) setFallbackFullscreen(false); };
    document.addEventListener('keydown', escape);
    return () => { document.removeEventListener('fullscreenchange', update); document.removeEventListener('keydown', escape); };
  }, [fallbackFullscreen]);
  const loadSession = useCallback(async () => {
    const response = await fetch('/api/session', { cache: 'no-store' });
    if (!response.ok) throw new Error('Откройте локальную панель http://127.0.0.1:3000/');
    const data = await response.json();
    setSession(data);
    if (data.configured) { setDraft(data.gpus); onSession(data); }
    else setError(data.error ?? 'Локальное управление не настроено');
    return data;
  }, [onSession]);
  useEffect(() => { loadSession().catch(e => setError(e.message)); }, [loadSession]);
  useEffect(() => {
    if (mode !== 'overview') return;
    const refresh = async () => {
      try {
        const response = await fetch('/api/earnings', { cache: 'no-store' });
        const data = await response.json();
        if (!response.ok) throw new Error(data.error ?? 'Нет данных о начислениях');
        setEarnings(data);
      } catch (e) { setEarnings((old: any) => ({ ...old, stale: true, error: (e as Error).message })); }
    };
    refresh();
    const timer = setInterval(refresh, 60000);
    return () => clearInterval(timer);
  }, [mode]);
  useEffect(() => {
    const refresh = async () => {
      try {
        const response = await fetch('/api/control', { cache: 'no-store' });
        if (response.ok) setJob(await response.json());
      } catch { /* Keep any visible operation status. */ }
    };
    refresh();
    const timer = setInterval(refresh, 2000);
    return () => clearInterval(timer);
  }, []);
  useEffect(() => {
    if (job.busy === false && job.applied && job.gpus && session?.configured) {
      onSession({ ...session, gpus: job.gpus });
    }
  }, [job.id, job.busy, onSession]);
  useEffect(() => {
    const refresh = async () => {
      try {
        const response = await fetch('/api/miner-status', { cache: 'no-store', signal: AbortSignal.timeout(9000) });
        if (!response.ok) throw new Error('Состояние майнера недоступно');
        setMinerStatus(await response.json());
      } catch (e) { setMinerStatus({ state:'warning', message:(e as Error).message, errors:[], tail:[] }); }
    };
    refresh();
    const timer = setInterval(refresh, 5000);
    return () => clearInterval(timer);
  }, []);

  const command = async (action: 'apply'|'start'|'stop'|'validate') => {
    if (!session?.configured || job.busy) return;
    setError('');
    setJob({ busy: true, status: 'sending', message: 'Отправка команды…' });
    try {
      const response = await fetch('/api/control', {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Pearl-Token': session.token },
        body: JSON.stringify({ action, ...(['apply','validate'].includes(action) ? { gpus: draft } : {}) }),
      });
      const result = await response.json();
      if (!response.ok) throw new Error(result.error ?? 'Команда не выполнена');
      setJob(result);
    } catch (e) { setError((e as Error).message); setJob({ busy: false, status: 'failed' }); }
  };
  const change = (id: number, key: keyof Profile, value: any) => setDraft(old => old.map(g => g.id === id ? { ...g, [key]: value } : g));
  const busy = job.busy || !session?.configured;
  const button = 'px-4 py-2 rounded-lg text-sm font-semibold cursor-pointer disabled:opacity-40 disabled:cursor-not-allowed';

  const fullscreenActive = fullscreen || fallbackFullscreen;
  return <div className={fullscreenActive ? 'fixed inset-0 z-[100] overflow-y-auto bg-[#0B0F17] p-6 space-y-5' : 'space-y-5'}>
    <div className="flex flex-wrap items-center justify-between gap-3">
      <div>
        <h2 className="text-xl font-bold text-white">{mode === 'overview' ? 'Доход и состояние рига' : 'Настройки видеокарт'}</h2>
        <p className="text-xs text-slate-400 mt-1">{session?.configured ? `Выплаты: ${session.wallet}` : 'Подключение к локальному агенту…'}</p>
      </div>
      <div className="flex flex-wrap gap-2">
        <button aria-label="Обновить реальные данные" title="Запросить свежие баланс, курс, счётчики и датчики" className={`${button} bg-cyan-600 text-white`} disabled={refreshing} onClick={refreshAll}>{refreshing ? '⟳ ОБНОВЛЕНИЕ…' : '⟳ ОБНОВИТЬ'}</button>
        <button aria-label="Полный экран" title="Развернуть панель на весь экран" className={`${button} bg-slate-700 text-white`} onClick={toggleFullscreen}>{fullscreenActive ? 'ВЫЙТИ ИЗ ПОЛНОГО ЭКРАНА' : 'ПОЛНЫЙ ЭКРАН'}</button>
        <button className={`${button} bg-emerald-500 text-slate-950`} disabled={busy || pool.minerProcessRunning === true} onClick={() => command('start')}>Запустить</button>
        <button className={`${button} bg-rose-600 text-white`} disabled={busy || pool.minerProcessRunning !== true} onClick={() => command('stop')}>Остановить</button>
        {mode === 'overview' && <button className={`${button} bg-slate-700 text-white`} onClick={onConfigure}>Настроить GPU</button>}
      </div>
    </div>
    {error && <div role="alert" className="rounded-lg bg-rose-950/50 border border-rose-600/40 p-3 text-sm text-rose-200">{error}</div>}
    {session && !session.configured && <form className="p-5 border border-amber-600/50 bg-slate-900 rounded-xl space-y-3" onSubmit={async e => {
      e.preventDefault(); setError('');
      try {
        const response=await fetch('/api/setup',{method:'POST',headers:{'Content-Type':'application/json','X-Pearl-Token':session.token},body:JSON.stringify(setup)});
        const result=await response.json(); if(!response.ok)throw new Error(result.error);
        await loadSession();
      } catch(e){setError((e as Error).message);}
    }}>
      <h3 className="font-semibold text-white">Первичная настройка на этом ПК</h3>
      <p className="text-xs text-slate-400">Адрес закрепляется локально. Сохранение не запускает майнинг. Введите только публичный адрес выплат, не seed-фразу.</p>
      {([['wallet','Полный публичный адрес Pearl'],['minerExe','Полный путь к onezerominer.exe'],['logFile','Путь к журналу майнера']] as const).map(([key,label])=><label key={key} className="block text-xs text-slate-300">{label}<input className="block w-full mt-1 bg-slate-950 border border-slate-700 rounded p-2 font-mono" value={setup[key]} onChange={e=>setSetup(old=>({...old,[key]:e.target.value}))}/></label>)}
      <button className={`${button} bg-emerald-400 text-slate-950`} type="submit">Сохранить на этом ПК</button>
    </form>}
    {job.status !== 'idle' && job.message && <div role="status" className={`rounded-lg border p-3 text-sm ${job.status === 'failed' ? 'border-rose-700 text-rose-300' : job.busy || job.status === 'unverified' ? 'border-amber-600/50 text-amber-200' : 'border-emerald-600/50 text-emerald-200'}`}>
      {job.message}{job.busy && ' · Не закрывайте запрос UAC Windows.'}
    </div>}

    <section className="p-5 rounded-xl border border-slate-700 bg-[#111827] space-y-4">
      <div className="flex flex-wrap justify-between gap-2">
        <h3 className="text-base font-bold text-white">Состояние майнера</h3>
        <span className={`rounded-full px-3 py-1 text-xs font-bold ${minerStatus?.state === 'healthy' ? 'bg-emerald-500/15 text-emerald-300' : minerStatus?.state === 'error' ? 'bg-rose-500/20 text-rose-300' : 'bg-amber-500/15 text-amber-200'}`}>{minerStatus?.message ?? 'Проверка состояния…'}</span>
      </div>
      <div className="grid grid-cols-2 xl:grid-cols-4 gap-3 text-sm">
        {[
          ['Процесс', minerStatus?.processRunning === true ? `Работает · PID ${minerStatus.pids.join(', ')}` : minerStatus?.processRunning === false ? 'Остановлен' : 'Нет данных'],
          ['API OneZeroMiner', minerStatus?.apiAvailable === true ? 'Доступен' : minerStatus ? 'Нет свежих данных' : 'Проверка…'],
          ['Подключение к пулу', minerStatus?.poolConnected === true ? 'Connected' : minerStatus?.poolConnected === false ? 'Disconnected' : 'Нет данных'],
          ['Обе видеокарты', minerStatus?.hashing ? 'Считают' : 'Нет подтверждения'],
        ].map(([label,value]) => <div key={label} className="p-3 bg-slate-950/60 rounded-lg border border-slate-800"><div className="text-xs text-slate-400">{label}</div><div className="font-mono text-slate-200 mt-2">{value}</div></div>)}
      </div>
      <div className="text-xs text-slate-400">Принято: {metric(minerStatus?.accepted ?? null, '')} · Отклонено: {metric(minerStatus?.rejected ?? null, '')} · Invalid: {metric(minerStatus?.invalid ?? null, '')} · Время работы: {minerStatus?.uptimeSec === undefined || minerStatus?.uptimeSec === null ? 'нет данных' : `${Math.floor(minerStatus.uptimeSec / 3600)} ч ${Math.floor(minerStatus.uptimeSec % 3600 / 60)} мин`} · {minerStatus?.sampledAt ? `Проверено ${new Date(minerStatus.sampledAt).toLocaleTimeString('ru-RU')}` : ''}</div>
      <div className="border-t border-slate-800 pt-3">
        <h4 className="text-sm font-semibold text-white mb-2">Последние ошибки журнала</h4>
        {minerStatus?.logError ? <p className="text-xs text-amber-300">{minerStatus.logError}</p> : minerStatus?.errors?.length ? <div className="max-h-48 overflow-y-auto space-y-2">{minerStatus.errors.map((item: any, id: number) => <pre key={id} className={`whitespace-pre-wrap break-words text-xs p-2 rounded bg-slate-950/70 ${item.severity === 'error' ? 'text-rose-300' : 'text-amber-200'}`}>{item.text}</pre>)}</div> : <p className="text-xs text-slate-400">{minerStatus ? 'В последних доступных строках журнала ошибок не найдено.' : 'Чтение журнала…'}</p>}
        <details className="mt-3 text-xs text-slate-400"><summary className="cursor-pointer">Журнал OneZeroMiner · последние 35 строк</summary><pre className="mt-2 p-3 rounded bg-black/40 overflow-auto max-h-64 whitespace-pre-wrap">{minerStatus?.tail?.join('\n') ?? 'Нет данных'}</pre></details>
      </div>
    </section>

    {mode === 'overview' && <>
      <section className="p-5 rounded-xl border border-emerald-700/30 bg-[#111827] space-y-4">
        <div className="flex flex-wrap justify-between gap-2">
          <div><h3 className="font-semibold text-white">Фактические начисления Kryptex</h3><p className="text-xs text-slate-400 mt-1">Данные пула в PRL. Ожидающие начисления ещё не подтверждены; баланс пула — до выплаты в кошелёк.</p></div>
          <a href={session?.wallet ? `https://pool.kryptex.com/prl/miner/${session.wallet}` : 'https://pool.kryptex.com/prl'} target="_blank" rel="noreferrer" className="text-xs text-emerald-400 underline">Проверить на пуле ↗</a>
        </div>
        <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-3">
          {[
            ['Подтверждено на пуле', earnings?.confirmed],
            ['Ожидает подтверждения', earnings?.unconfirmed],
            ['Выплачено в кошелёк', earnings?.paid],
            ['Начислено за сутки пула (UTC)', earnings?.todayUtc],
          ].map(([label, value]) => <div key={String(label)} className="p-4 rounded-lg bg-slate-950/60 border border-slate-800">
            <div className="text-xs text-slate-400 min-h-8">{label}</div>
            <div className={`text-lg font-mono font-bold ${earnings?.stale ? 'text-slate-400' : 'text-emerald-300'}`}>{prl(value)}</div><div className="text-xs text-slate-500">PRL</div>
          </div>)}
        </div>
        <div className="flex flex-wrap justify-between gap-2 text-xs text-slate-400">
          <span>Баланс с ожидающими: <b className="text-slate-200">{prl(earnings?.balance)} PRL</b> · Порог выплаты: {prl(earnings?.payoutThreshold)} PRL</span>
          <span>{earnings?.stale ? 'Нет свежих данных · ' : ''}{earnings?.fetchedAt ? `Получено ${new Date(earnings.fetchedAt).toLocaleTimeString('ru-RU')}` : 'Ожидание ответа пула'}</span>
        </div>
        {(earnings?.error || earnings?.partial) && <p className="text-xs text-amber-300">{earnings.error ?? 'Часть данных пула недоступна. Отсутствующие суммы не заменяются нулями.'}</p>}
        <div className="grid grid-cols-1 md:grid-cols-3 gap-3 border-t border-slate-800 pt-4">
          <div className="rounded-lg bg-slate-950/60 border border-slate-800 p-3 text-xs">
            <div className="text-slate-400">Официальный курс PRL</div>
            <div className="mt-1 font-mono text-emerald-300">{marketRates?.prlRub ? `${marketRates.prlRub.toLocaleString('ru-RU', { maximumFractionDigits: 4 })} ₽` : 'нет свежего курса'}</div>
            <div className="mt-1 text-[11px] text-slate-500">PRL/USD {marketRates?.prlUsd?.toFixed?.(6) ?? '—'} · USD/RUB {marketRates?.usdRub?.toFixed?.(4) ?? '—'}</div>
            <a href="https://pool.kryptex.com/prl/price" target="_blank" rel="noreferrer" className="text-[11px] text-emerald-400 underline">Источник Kryptex ↗</a>
            <span className="mx-1 text-[11px] text-slate-600">·</span><a href="https://www.cbr.ru/scripts/XML_daily.asp" target="_blank" rel="noreferrer" className="text-[11px] text-emerald-400 underline">Банк России ↗</a>
          </div>
          <label className="text-xs text-slate-400">Электроэнергия, ₽/кВт·ч
            <input type="number" min="0" step="0.01" value={rates.kwhRub || ''} onChange={e => setRates(old => ({ ...old, kwhRub: Math.max(0, Number(e.target.value) || 0) }))} className="block w-full mt-1 bg-slate-950 border border-slate-700 rounded p-2 text-slate-100 font-mono" placeholder="Введите тариф" />
          </label>
          <div className="rounded-lg bg-slate-950/60 border border-slate-800 p-3 text-xs">
            <div className="text-slate-400">Подтверждённый заработок по курсу</div>
            <div className="mt-1 font-mono text-emerald-300">{confirmedValueRub === null ? 'нет курса или данных' : `${confirmedValueRub.toLocaleString('ru-RU', { maximumFractionDigits: 2 })} ₽`}</div>
            <div className="mt-1 text-[11px] text-slate-500">Баланс с ожиданием: {balanceValueRub === null ? 'нет данных' : `${balanceValueRub.toLocaleString('ru-RU', { maximumFractionDigits: 2 })} ₽`}</div>
          </div>
        </div>
      </section>
      <section className="p-5 rounded-xl border border-slate-700 bg-[#111827] space-y-3">
        <h3 className="font-semibold text-white">Электропотребление видеокарт</h3>
        <div className="grid grid-cols-1 sm:grid-cols-3 gap-3 text-sm">
          <div className="p-3 rounded-lg bg-slate-950/60 border border-slate-800"><div className="text-xs text-slate-400">GPU сейчас всего</div><b className="font-mono text-slate-100">{totalPowerWatts === null ? 'нет данных' : `${totalPowerWatts.toFixed(1)} Вт (${totalPowerKw?.toFixed(3)} кВт)`}</b></div>
          <div className="p-3 rounded-lg bg-slate-950/60 border border-slate-800"><div className="text-xs text-slate-400">Измерено с этой панели</div><b className="font-mono text-slate-100">{energyKwh.toFixed(4)} кВт·ч</b></div>
          <div className="p-3 rounded-lg bg-slate-950/60 border border-slate-800"><div className="text-xs text-slate-400">Стоимость измеренной энергии</div><b className="font-mono text-slate-100">{measuredCost === null ? 'нет тарифа' : `${measuredCost.toFixed(2)} ₽`}</b><div className="text-[11px] text-slate-500">Сейчас: {powerCostPerHour === null ? 'нет тарифа/мощности' : `${powerCostPerHour.toFixed(2)} ₽/ч`}</div></div>
        </div>
        <p className="text-xs text-slate-500">Общая мощность — сумма реальных показаний GPU из nvidia-smi. кВт·ч накапливаются только при доступных измерениях и сохраняются локально в браузере; это не показания счётчика дома.</p>
      </section>
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-5">
        {gpus.map(gpu => <section key={gpu.id} className="p-4 rounded-xl border border-slate-700 bg-[#111827]">
          <div className="flex items-center justify-between"><h3 className="text-base font-bold text-white">GPU {gpu.id} · RTX 4070 Ti</h3><span className="text-xs text-slate-400">nvidia-smi / API</span></div>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4 mt-3">
            <SpeedometerGauge label={`Температура GPU ${gpu.id}`} value={gpu.tempC} min={20} max={95} unit="°C" warningThreshold={80} criticalThreshold={85} decimals={0} accent="amber" />
            <div className="text-sm space-y-3 py-2">
              {[
                ['Хешрейт', metric(gpu.hashrateTHs, ' TH/s')], ['Мощность', metric(gpu.powerWatts, ' Вт')],
                ['Вентилятор', metric(gpu.fanPercent, '%')], ['Ядро / память', `${metric(gpu.cclkMHz, '')} / ${metric(gpu.mclkMHz, ' МГц')}`],
                ['Принято / отклонено', `${metric(gpu.acceptedShares, '')} / ${metric(gpu.rejectedShares, '')}`],
              ].map(([label, value]) => <div key={label} className="flex justify-between gap-2 border-b border-slate-800 pb-2"><span className="text-slate-400">{label}</span><b className="font-mono text-slate-200 text-right">{value}</b></div>)}
            </div>
          </div>
        </section>)}
      </div>
      <p className="text-xs text-slate-400">Пул: {pool.connected === true ? 'Connected' : pool.connected === false ? 'Disconnected' : 'нет данных'} · {pool.poolUrl ?? 'нет данных'} · Датчики обновляются каждые 5 секунд, начисления — раз в минуту. Кнопка «Обновить данные» запускает запрос немедленно.</p>
    </>}

    {mode === 'settings' && <section className="p-5 rounded-xl border border-slate-700 bg-[#111827] space-y-5">
      <p className="text-sm text-slate-300">Измените профиль и нажмите «Применить к ригу». Изменение частот или вентилятора потребует перезапуска майнера; кошелёк и RU-пул сохраняются.</p>
      <div className="grid grid-cols-1 md:grid-cols-2 gap-5">{draft.map(gpu => <div key={gpu.id} className="rounded-lg p-4 bg-slate-950/70 border border-slate-800 space-y-4">
        <h3 className="font-semibold text-white">GPU {gpu.id} · RTX 4070 Ti</h3>
        <div className="text-xs text-slate-400">Сейчас: {metric(gpus[gpu.id].tempC, '°C')} · {metric(gpus[gpu.id].powerWatts, ' Вт')} · вентилятор {metric(gpus[gpu.id].fanPercent, '%')}</div>
        <label className="block text-sm text-slate-300">Мощность GPU {gpu.id}, Вт (100–150)<input aria-label={`Мощность GPU ${gpu.id}`} className="block w-full mt-1 bg-slate-900 border border-slate-600 rounded p-2 font-mono" type="number" min="100" max="150" value={gpu.pl} onChange={e => change(gpu.id,'pl',Number(e.target.value))} /></label>
        <label className="block text-sm text-slate-300">Целевая частота ядра, МГц (1800–2400)<input aria-label={`Ядро GPU ${gpu.id}`} className="block w-full mt-1 bg-slate-900 border border-slate-600 rounded p-2 font-mono" type="number" min="1800" max="2400" step="15" value={gpu.cclk} onChange={e => change(gpu.id,'cclk',Number(e.target.value))} /></label>
        <label className="flex items-center gap-2 text-sm text-slate-300"><input type="checkbox" aria-label={`Авто вентилятор GPU ${gpu.id}`} checked={gpu.fan === '*'} onChange={e => change(gpu.id,'fan',e.target.checked ? '*' : '65')} />Автоматический вентилятор</label>
        {gpu.fan !== '*' && <label className="block text-sm text-slate-300">Вентилятор, % (45–100)<input aria-label={`Вентилятор GPU ${gpu.id}`} className="block w-full mt-1 bg-slate-900 border border-slate-600 rounded p-2 font-mono" type="number" min="45" max="100" value={gpu.fan} onChange={e => change(gpu.id,'fan',e.target.value)} /></label>}
        <p className="text-xs text-slate-500">Память: 810 МГц · Kernel 2 · Остановка при 85°C во время применения.</p>
      </div>)}</div>
      <div className="flex flex-wrap gap-3">
        <button className={`${button} bg-emerald-400 text-slate-950`} disabled={busy} onClick={() => command('apply')}>Применить к ригу</button>
        <button className={`${button} bg-slate-700 text-white`} disabled={busy} onClick={() => command('validate')}>Проверить без применения</button>
        <button className={`${button} bg-slate-800 text-slate-200`} disabled={job.busy} onClick={() => loadSession().catch(e => setError(e.message))}>Загрузить сохранённый профиль</button>
      </div>
      <p className="text-xs text-slate-400">Возможно появление запроса UAC Windows. После применения проверяются подключение к пулу, ненулевой хешрейт обеих карт и новая принятая шара.</p>
    </section>}
  </div>;
}
