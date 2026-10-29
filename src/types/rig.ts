export interface GpuSettings {
  id: number;
  name: string;
  pciBus: string;
  cclk: number;
  mclk: number;
  pl: number;
  fan: string;
  kernel: number;
}

export interface RigConfig {
  miningActive: boolean;
  userStoppedManually: boolean;
  minerDir: string;
  logFilePath: string;
  walletAddress: string;
  walletSaved: boolean;
  workerName: string;
  primaryPool: string;
  backupPool: string;
  apiPort: number;
  agentBridgePort: number;
  gpus: [GpuSettings, GpuSettings];
}

export interface WatchdogPolicy {
  checkIntervalSec: number;
  minHashrateTHs: number;
  zeroHashrateRestartSec: number;
  maxRestartAttempts: number;
  maxTempC: number;
  criticalStopTempC: number;
  emergencyPlWatts: number;
  telegramEnabled: boolean;
  telegramBotToken: string;
  telegramChatId: string;
}

export interface GpuLiveMetrics {
  id: number;
  name: string;
  hashrateTHs: number | null;
  acceptedShares: number | null;
  rejectedShares: number | null;
  staleShares: number | null;
  lastShareSec: number | null;
  tempC: number | null;
  hotspotTempC: number | null;
  vramTempC: number | null;
  fanPercent: number | null;
  fanRpm: number | null;
  powerWatts: number | null;
  efficiencyGHW: number | null;
  cclkMHz: number | null;
  mclkMHz: number | null;
  voltageMv: number | null;
  vramUsedMb: number | null;
  pState: string | null;
  pcieLink: string | null;
  status: 'nominal' | 'warning' | 'critical' | 'offline' | 'no_data';
}

export interface PoolLiveStatus {
  connected: boolean | null;
  poolUrl: string | null;
  pingMs: number | null;
  uptimeSec: number | null;
  apiAvailable: boolean;
  nvmlAvailable: boolean;
  minerProcessRunning: boolean | null;
  minerPid: number | null;
  reconnectStage: 'idle' | 'native_wait' | 'restarting' | 'awaiting_confirmation' | 'limit_reached';
  restartAttempts: number;
  sharesAtRestart: number;
  recoveryConfirmed: boolean;
  lastError: string | null;
  agentVersion?: string | null;
  updateAvailable?: boolean;
  latestVersion?: string | null;
  downloadProgress?: number;
}

export interface WatchdogEvent {
  id: number | string;
  timestamp: string;
  category:
    | 'PROCESS'
    | 'POOL'
    | 'THERMAL'
    | 'WALLET'
    | 'SECURITY'
    | 'OC_PROFILE'
    | 'TELEGRAM';
  severity: 'info' | 'warning' | 'error';
  message: string;
  actionTaken: string;
}

export interface LogDiagnosticItem {
  severity: 'error' | 'warning' | 'ok';
  title: string;
  detail: string;
  fixCommand: string;
}

export interface ScenarioCheckResult {
  id: string;
  title: string;
  passed: boolean;
  expected: string;
  actual: string;
}
