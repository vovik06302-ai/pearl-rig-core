using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using PearlRigCore.Models;
using PearlRigCore.Services;

namespace PearlRigCore.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private const int MaxHistorySamples = 120;
    private readonly TelemetryService _telemetry;
    private readonly PoolEarningsClient _earningsClient;
    private readonly MinerControlService _minerControl;
    private readonly UpdateService _updateService;
    private readonly NvidiaSmiClient _nvidiaSmi;
    private readonly TelegramAlertService _telegramService;
    private readonly PoolPingService _poolPingService;
    private readonly SmartFanController _smartFanController = new();
    private readonly ProfitabilityCalculator _profitability = new();
    private readonly WatchdogService _watchdog = new();
    private readonly Dictionary<int, List<double>> _gpuHashrateBuffers = [];
    private readonly Dictionary<int, List<double>> _gpuTempBuffers = [];
    private readonly List<double> _totalHashrateBuffer = [];
    private readonly List<string> _watchdogEvents = [];
    private RigConfig _config;
    private readonly int _earningsRefreshSeconds = 60;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _lifetime;
    private DateTimeOffset _lastEarningsRead = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPoolPingAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTelegramPollAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCsvSnapshotAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSessionSaveAt = DateTimeOffset.MinValue;
    private double? _lastKnownPaidPrl;
    private double? _lastKnownTotalPrl;
    private double? _lastKnownPrlRubRate;
    private double? _lastTotalHashrateTHs;
    private double? _lastTotalPowerWatts;
    private double? _lastEfficiencyGhPerWatt;
    private bool _userManuallyStopped;
    private bool _isAutoTuning;
    private string? _lastScheduledWindowApplied;
    private string _walletAddress;
    private bool _isRefreshing;
    private bool _manualRefreshQueued;
    private bool _disposed;
    private string _connectionStatus = "Подключение к источникам…";
    private string _minerApiStatus = "Ожидание данных";
    private string _hardwareStatus = "Ожидание датчиков";
    private string _poolStatus = "Нет данных";
    private string _poolUrl = "Адрес пула недоступен";
    private string _poolPingText = "Пинг RU: — · EU: —";
    private string _totalHashrate = "Нет данных";
    private string _totalEfficiency = "Энергоэффективность: — GH/W · Потребление: — Вт";
    private string _electricitySummaryText = "Электроэнергия: 0.000 кВт·ч (0.00 ₽) · Сутки: — ₽";
    private string _netProfitSummaryText = "Чистая прибыль (после розетки): ожидание замеров";
    private string _acceptedShares = "Нет данных";
    private string _rejectedShares = "Нет данных";
    private string _minerUptimeText = "Аптайм: — · API: —";
    private string _lastUpdated = "Ожидание первого опроса";
    private string _detailStatus = "Показания появятся после ответа локальных источников.";
    private string _walletAddressDraft;
    private string _walletStatus;
    private string _poolBalanceStatus = "Баланс ещё не загружался";
    private string _poolBalance = "Нет данных";
    private string _poolConfirmed = "Нет данных";
    private string _poolUnconfirmed = "Нет данных";
    private string _poolPaid = "Нет данных";
    private string _poolThresholdText = "Порог: —";
    private double _payoutProgressPercent;
    private string _payoutProgressText = "До выплаты: нет данных";
    private string _sessionAccumulationRate = "Фактическая скорость сессии: ожидание второго замера баланса";
    private string _poolRubEstimate = "Нет данных";
    private string _marketRate = "Курс PRL/RUB недоступен";
    private string _poolUpdated = "Нет данных";
    private bool _isMinerBusy;
    private bool _gpuDiscoveryCompleted;
    private bool _autoStartMining;
    private bool _watchdogEnabled;
    private bool _smartFanCurveEnabled;
    private bool _dualTariffEnabled;
    private bool _nightScheduleEnabled;
    private bool _autoRestartMinerOnOverclockChange;
    private bool _autoSwitchToBackupPoolOnDrop;
    private string _activePreset;
    private string _watchdogStatusText = "Watchdog и термозащита активны";
    private string _watchdogEventsText = "Журнал защиты запущен. Аварийных событий нет.";
    private string _minerLogTail = "Ожидание чтения onezerominer.log…";
    private string _appLogTail = "Журнал программы инициализирован.";
    private string _minerCommandLinePreview = string.Empty;
    private string _minerControlStatus = "Проверка майнера…";
    private string _workerNameDraft;
    private string _coreClockDraft;
    private string _memoryClockDraft;
    private string _powerLimitDraft;
    private string _kernelDraft;
    private string _fanDraft;
    private string _electricityRateDraft;
    private string _nightElectricityRateDraft;
    private string _fanCurveTempLowCDraft;
    private string _fanCurveSpeedLowPctDraft;
    private string _fanCurveTempMidCDraft;
    private string _fanCurveSpeedMidPctDraft;
    private string _fanCurveTempHighCDraft;
    private string _fanCurveSpeedHighPctDraft;
    private string _fanHysteresisCDraft;
    private string _vramTempOffsetCDraft;
    private string _warningTempCDraft;
    private string _criticalStopTempCDraft;
    private string _resumeTempCDraft;
    private string _zeroHashrateRestartSecondsDraft;
    private string _poolDropSwitchThresholdCountDraft;
    private string _autoTuneStepSecondsDraft;
    private string _dayTariffStartHourDraft;
    private string _nightTariffStartHourDraft;
    private string _daySchedulePreset;
    private string _nightSchedulePreset;
    private string _primaryPoolDraft;
    private string _backupPoolDraft;
    private bool _telegramEnabled;
    private bool _telegramRemoteControlEnabled;
    private string _telegramBotTokenDraft;
    private string _telegramChatIdDraft;
    private string _telegramStatusText = "Уведомления Telegram не настроены";
    private double _powerSliderValue = 150;
    private double _powerSliderMinimum = 100;
    private double _powerSliderMaximum = 320;
    private bool _canAdjustPowerLimit = true;
    private bool _powerSliderInitialized;
    private string _powerRangeHint = "Передвиньте ползунок или выберите пресет — лимит мощности (Вт) сразу применится к GPU через NVML.";
    private IReadOnlyList<double> _totalHashrateHistory = [];
    private ReleaseUpdate? _availableUpdate;
    private Version _installedVersion;
    private bool _isCheckingUpdate;
    private bool _isDownloadingUpdate;
    private double _updateProgress;
    private string _updateStatusText = "Проверка версии…";
    private string _connectionHealthLevel = "Partial";
    private string _poolConnectedState = "Unknown";
    private string _toastMessage = string.Empty;
    private string _toastSeverity = "Info";
    private DateTimeOffset _toastExpiresAt = DateTimeOffset.MinValue;
    private string _dailyNetProfitKpiText = "Ожидание темпа PRL";
    private string _dailyElectricityKpiText = "— ₽/сут";
    private string _payoutEtaText = "Ожидание замеров";
    private string _currentScheduleStatusText = "Тариф и расписание";
    private bool _useIndividualGpuOverclock;
    private bool _syncingPerGpuRows;
    private double _autoTuneProgressPercent;
    private string _autoTuneProgressText = string.Empty;
    private double? _gpu0EffectiveTemp;
    private double? _gpu1EffectiveTemp;
    private string _logSearchQuery = string.Empty;
    private bool _logOnlyWarningsAndErrors;
    private bool _logAutoScrollEnabled = true;
    private bool _diagAdminOk;
    private string _diagAdminText = "Проверка…";
    private bool _diagNvmlOk;
    private string _diagNvmlText = "Проверка…";
    private bool _diagMinerExeOk;
    private string _diagMinerExeText = "Проверка…";
    private bool _diagAutoStartOk;
    private string _diagAutoStartText = "Проверка…";
    private bool _diagTdrOk = true;
    private string _diagTdrText = "Нет недавних сбросов";

    public MainViewModel(
        TelemetryService telemetry,
        PoolEarningsClient earningsClient,
        MinerControlService minerControl,
        UpdateService updateService,
        NvidiaSmiClient nvidiaSmi,
        TelegramAlertService telegramService,
        PoolPingService poolPingService,
        RigConfig config)
    {
        _telemetry = telemetry;
        _earningsClient = earningsClient;
        _minerControl = minerControl;
        _updateService = updateService;
        _nvidiaSmi = nvidiaSmi;
        _telegramService = telegramService;
        _poolPingService = poolPingService;
        _config = config;
        _installedVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(3, 9, 0);
        _autoStartMining = config.AutoStartMining || MinerControlService.IsAutoStartTaskRegistered();
        _watchdogEnabled = config.WatchdogEnabled;
        _smartFanCurveEnabled = config.SmartFanCurveEnabled;
        _dualTariffEnabled = config.DualTariffEnabled;
        _nightScheduleEnabled = config.NightScheduleEnabled;
        _autoRestartMinerOnOverclockChange = config.AutoRestartMinerOnOverclockChange;
        _autoSwitchToBackupPoolOnDrop = config.AutoSwitchToBackupPoolOnDrop;
        _activePreset = string.IsNullOrWhiteSpace(config.ActivePreset) ? "Auto" : config.ActivePreset;
        _workerNameDraft = config.WorkerName;
        _coreClockDraft = SettingDraft(config.CoreClock);
        _memoryClockDraft = SettingDraft(config.MemoryClock);
        _powerLimitDraft = SettingDraft(config.PowerLimit);
        if (TryExtractFirstWatts(_powerLimitDraft, out var initialDraftWatts))
            _powerSliderValue = Math.Clamp(initialDraftWatts, _powerSliderMinimum, _powerSliderMaximum);
        _kernelDraft = SettingDraft(config.Kernel);
        _fanDraft = SettingDraft(config.Fan);
        _electricityRateDraft = config.ElectricityRateRubPerKwh.ToString("0.00", CultureInfo.InvariantCulture);
        _nightElectricityRateDraft = config.NightElectricityRateRubPerKwh.ToString("0.00", CultureInfo.InvariantCulture);
        _fanCurveTempLowCDraft = config.FanCurveTempLowC.ToString(CultureInfo.InvariantCulture);
        _fanCurveSpeedLowPctDraft = config.FanCurveSpeedLowPct.ToString(CultureInfo.InvariantCulture);
        _fanCurveTempMidCDraft = config.FanCurveTempMidC.ToString(CultureInfo.InvariantCulture);
        _fanCurveSpeedMidPctDraft = config.FanCurveSpeedMidPct.ToString(CultureInfo.InvariantCulture);
        _fanCurveTempHighCDraft = config.FanCurveTempHighC.ToString(CultureInfo.InvariantCulture);
        _fanCurveSpeedHighPctDraft = config.FanCurveSpeedHighPct.ToString(CultureInfo.InvariantCulture);
        _fanHysteresisCDraft = config.FanHysteresisC.ToString(CultureInfo.InvariantCulture);
        _vramTempOffsetCDraft = config.VramTempOffsetC.ToString(CultureInfo.InvariantCulture);
        _warningTempCDraft = config.WarningTempC.ToString(CultureInfo.InvariantCulture);
        _criticalStopTempCDraft = config.CriticalStopTempC.ToString(CultureInfo.InvariantCulture);
        _resumeTempCDraft = config.ResumeTempC.ToString(CultureInfo.InvariantCulture);
        _zeroHashrateRestartSecondsDraft = config.ZeroHashrateRestartSeconds.ToString(CultureInfo.InvariantCulture);
        _poolDropSwitchThresholdCountDraft = config.PoolDropSwitchThresholdCount.ToString(CultureInfo.InvariantCulture);
        _autoTuneStepSecondsDraft = config.AutoTuneStepSeconds.ToString(CultureInfo.InvariantCulture);
        _dayTariffStartHourDraft = config.DayTariffStartHour.ToString(CultureInfo.InvariantCulture);
        _nightTariffStartHourDraft = config.NightTariffStartHour.ToString(CultureInfo.InvariantCulture);
        _daySchedulePreset = config.DaySchedulePreset;
        _nightSchedulePreset = config.NightSchedulePreset;
        _primaryPoolDraft = config.PrimaryPool;
        _backupPoolDraft = config.BackupPool;
        _telegramEnabled = config.TelegramEnabled;
        _telegramRemoteControlEnabled = config.TelegramRemoteControlEnabled;
        _telegramBotTokenDraft = config.TelegramBotToken;
        _telegramChatIdDraft = config.TelegramChatId;
        _telegramStatusText = _telegramEnabled && !string.IsNullOrWhiteSpace(_telegramBotTokenDraft) && !string.IsNullOrWhiteSpace(_telegramChatIdDraft)
            ? (_telegramRemoteControlEnabled ? "Telegram: алерты и команды (/status, /preset) активны (DPAPI)" : "Telegram-уведомления активны (DPAPI)")
            : "Введите токен бота и Chat ID для алертов и управления";
        _walletAddress = config.WalletAddress;
        _walletAddressDraft = config.WalletAddress;
        _walletStatus = PoolEarningsClient.IsValidWallet(config.WalletAddress) ? "Адрес выплат загружен из локального профиля" : "Введите публичный адрес PRL";
        _minerCommandLinePreview = _minerControl.GetCommandLinePreview(_walletAddressDraft, Math.Max(2, Gpus.Count));

        RestorePersistedSession();

        SaveWalletCommand = new RelayCommand(() => _ = SaveWalletAsync(), () => PoolEarningsClient.IsValidWallet(WalletAddressDraft));
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(force: true));
        StartMinerCommand = new RelayCommand(() => _ = StartMinerAsync(manual: true), () => !_isMinerBusy && !_isAutoTuning && !_minerControl.IsRunning);
        StopMinerCommand = new RelayCommand(() => _ = StopMinerAsync(manual: true), () => !_isMinerBusy && !_isAutoTuning && _minerControl.IsRunning);
        RestartMinerNowCommand = new RelayCommand(() => _ = RestartMinerAsync(), () => !_isMinerBusy && !_isAutoTuning && _minerControl.IsRunning);
        ToggleMinerModeCommand = new RelayCommand(() => _ = ToggleMinerModeAsync(), () => !_isMinerBusy);
        ToggleWatchdogCommand = new RelayCommand(() => _ = ToggleWatchdogAsync(), () => !_isMinerBusy);
        ToggleSmartFanCurveCommand = new RelayCommand(() => _ = ToggleSmartFanCurveAsync(), () => !_isMinerBusy);
        ToggleDualTariffCommand = new RelayCommand(() => _ = ToggleDualTariffAsync(), () => !_isMinerBusy);
        ToggleNightScheduleCommand = new RelayCommand(() => _ = ToggleNightScheduleAsync(), () => !_isMinerBusy);
        RunAutoTuneBenchmarkCommand = new RelayCommand(() => _ = RunAutoTuneBenchmarkAsync(), () => !_isMinerBusy && !_isAutoTuning && _minerControl.IsRunning);
        ExportCsvReportCommand = new RelayCommand(() => _ = ExportCsvReportAsync());
        OpenLogsFolderCommand = new RelayCommand(OpenLogsFolder);
        PresetAutoCommand = new RelayCommand(() => _ = ApplyPresetAndSaveAsync("Auto"), () => !_isMinerBusy && !_isAutoTuning);
        PresetEcoCommand = new RelayCommand(() => _ = ApplyPresetAndSaveAsync("Eco"), () => !_isMinerBusy && !_isAutoTuning);
        PresetBalanceCommand = new RelayCommand(() => _ = ApplyPresetAndSaveAsync("Balance"), () => !_isMinerBusy && !_isAutoTuning);
        PresetTurboCommand = new RelayCommand(() => _ = ApplyPresetAndSaveAsync("Turbo"), () => !_isMinerBusy && !_isAutoTuning);
        PresetSilentCommand = new RelayCommand(() => _ = ApplyPresetAndSaveAsync("Silent"), () => !_isMinerBusy && !_isAutoTuning);
        ApplyPowerLimitNowCommand = new RelayCommand(() => _ = ApplySliderPowerLimitAsync(), () => !_isMinerBusy && !_isAutoTuning);
        SaveMinerSettingsCommand = new RelayCommand(() => _ = SaveMinerSettingsAsync(), CanSaveMinerSettings);
        SaveFanCurveCommand = new RelayCommand(() => _ = SaveFanCurveSettingsAsync(), () => !_isMinerBusy);
        SaveWatchdogAndScheduleCommand = new RelayCommand(() => _ = SaveWatchdogAndScheduleSettingsAsync(), () => !_isMinerBusy);
        SaveTelegramCommand = new RelayCommand(() => _ = SaveTelegramSettingsAsync());
        TestTelegramCommand = new RelayCommand(() => _ = TestTelegramAsync());
        CopyCommandLineCommand = new RelayCommand(CopyCommandLineToClipboard);
        UpdateCommand = new RelayCommand(() => _ = UpdateOrCheckAsync(), () => !_isCheckingUpdate && !_isDownloadingUpdate);
        DismissToastCommand = new RelayCommand(DismissToast);
        SwitchPoolManuallyCommand = new RelayCommand(() => _ = SwitchPoolManuallyAsync(), () => !_isMinerBusy && !_isAutoTuning);
        PingPoolsNowCommand = new RelayCommand(() => _ = PingPoolsNowAsync());
        ClearWatchdogLogCommand = new RelayCommand(ClearWatchdogLog);
        CopyLogsCommand = new RelayCommand(CopyFilteredLogsToClipboard);
        RunDiagnosticsNowCommand = new RelayCommand(() => _ = RunDiagnosticsCheckAsync());

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        AppendWatchdogEvent($"Система v{_installedVersion.ToString(3)} инициализирована (предупреждение {_config.WarningTempC} °C, стоп {_config.CriticalStopTempC} °C, возврат {_config.ResumeTempC} °C).");
        RefreshSchedulePreview();
        UpdateDiagnosticFlagsCore(tdrEvent: null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string, string?>? InstallerDownloaded;
    public event Action<string, TrayHealthState>? TrayStatusChanged;

    public ObservableCollection<GpuLiveMetrics> Gpus { get; } = [];
    public ObservableCollection<AutoTuneStepResult> AutoTuneSteps { get; } = [];
    public ObservableCollection<GpuOverclockRowEditor> GpuOverclockRows { get; } = [];
    public IReadOnlyList<string> AvailableSchedulePresets { get; } = ["Auto", "Eco", "Balance", "Turbo", "Silent"];

    public RelayCommand RefreshCommand { get; }
    public RelayCommand SaveWalletCommand { get; }
    public RelayCommand StartMinerCommand { get; }
    public RelayCommand StopMinerCommand { get; }
    public RelayCommand RestartMinerNowCommand { get; }
    public RelayCommand ToggleMinerModeCommand { get; }
    public RelayCommand ToggleWatchdogCommand { get; }
    public RelayCommand ToggleSmartFanCurveCommand { get; }
    public RelayCommand ToggleDualTariffCommand { get; }
    public RelayCommand ToggleNightScheduleCommand { get; }
    public RelayCommand RunAutoTuneBenchmarkCommand { get; }
    public RelayCommand ExportCsvReportCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand PresetAutoCommand { get; }
    public RelayCommand PresetEcoCommand { get; }
    public RelayCommand PresetBalanceCommand { get; }
    public RelayCommand PresetTurboCommand { get; }
    public RelayCommand PresetSilentCommand { get; }
    public RelayCommand ApplyPowerLimitNowCommand { get; }
    public RelayCommand SaveMinerSettingsCommand { get; }
    public RelayCommand SaveFanCurveCommand { get; }
    public RelayCommand SaveWatchdogAndScheduleCommand { get; }
    public RelayCommand SaveTelegramCommand { get; }
    public RelayCommand TestTelegramCommand { get; }
    public RelayCommand CopyCommandLineCommand { get; }
    public RelayCommand UpdateCommand { get; }
    public RelayCommand DismissToastCommand { get; }
    public RelayCommand SwitchPoolManuallyCommand { get; }
    public RelayCommand PingPoolsNowCommand { get; }
    public RelayCommand ClearWatchdogLogCommand { get; }
    public RelayCommand CopyLogsCommand { get; }
    public RelayCommand RunDiagnosticsNowCommand { get; }

    public void SetUpdateStatus(string message) => UpdateStatusText = message;

    public bool MinimizeToTray => _config.MinimizeToTray;
    public string ConnectionStatus { get => _connectionStatus; private set => Set(ref _connectionStatus, value); }
    public string ConnectionHealthLevel { get => _connectionHealthLevel; private set => Set(ref _connectionHealthLevel, value); }
    public string MinerApiStatus { get => _minerApiStatus; private set => Set(ref _minerApiStatus, value); }
    public string HardwareStatus { get => _hardwareStatus; private set => Set(ref _hardwareStatus, value); }
    public string PoolStatus { get => _poolStatus; private set => Set(ref _poolStatus, value); }
    public string PoolConnectedState { get => _poolConnectedState; private set => Set(ref _poolConnectedState, value); }
    public string PoolUrl { get => _poolUrl; private set => Set(ref _poolUrl, value); }
    public string PoolPingText { get => _poolPingText; private set => Set(ref _poolPingText, value); }
    public string ActivePoolBadgeText => _minerControl.UseBackupPool ? "АКТИВЕН РЕЗЕРВ (EU)" : "АКТИВЕН ОСНОВНОЙ (RU)";
    public string ToastMessage { get => _toastMessage; private set { if (Set(ref _toastMessage, value)) OnPropertyChanged(nameof(HasToast)); } }
    public string ToastSeverity { get => _toastSeverity; private set => Set(ref _toastSeverity, value); }
    public bool HasToast => !string.IsNullOrWhiteSpace(_toastMessage);
    public string DailyNetProfitKpiText { get => _dailyNetProfitKpiText; private set => Set(ref _dailyNetProfitKpiText, value); }
    public string DailyElectricityKpiText { get => _dailyElectricityKpiText; private set => Set(ref _dailyElectricityKpiText, value); }
    public string PayoutEtaText { get => _payoutEtaText; private set => Set(ref _payoutEtaText, value); }
    public string TotalHashrate { get => _totalHashrate; private set => Set(ref _totalHashrate, value); }
    public string TotalEfficiency { get => _totalEfficiency; private set => Set(ref _totalEfficiency, value); }
    public string ElectricitySummaryText { get => _electricitySummaryText; private set => Set(ref _electricitySummaryText, value); }
    public string NetProfitSummaryText { get => _netProfitSummaryText; private set => Set(ref _netProfitSummaryText, value); }
    public IReadOnlyList<double> TotalHashrateHistory { get => _totalHashrateHistory; private set => Set(ref _totalHashrateHistory, value); }
    public string AcceptedShares { get => _acceptedShares; private set => Set(ref _acceptedShares, value); }
    public string RejectedShares { get => _rejectedShares; private set => Set(ref _rejectedShares, value); }
    public string MinerUptimeText { get => _minerUptimeText; private set => Set(ref _minerUptimeText, value); }
    public string LastUpdated { get => _lastUpdated; private set => Set(ref _lastUpdated, value); }
    public string DetailStatus { get => _detailStatus; private set => Set(ref _detailStatus, value); }

    public string WalletAddressDraft
    {
        get => _walletAddressDraft;
        set
        {
            if (!Set(ref _walletAddressDraft, value)) return;
            SaveWalletCommand.NotifyCanExecuteChanged();
            UpdateCommandLinePreviewFromDrafts();
        }
    }

    public string WalletStatus { get => _walletStatus; private set => Set(ref _walletStatus, value); }
    public string PoolBalanceStatus { get => _poolBalanceStatus; private set => Set(ref _poolBalanceStatus, value); }
    public string PoolBalance { get => _poolBalance; private set => Set(ref _poolBalance, value); }
    public string PoolConfirmed { get => _poolConfirmed; private set => Set(ref _poolConfirmed, value); }
    public string PoolUnconfirmed { get => _poolUnconfirmed; private set => Set(ref _poolUnconfirmed, value); }
    public string PoolPaid { get => _poolPaid; private set => Set(ref _poolPaid, value); }
    public string PoolThresholdText { get => _poolThresholdText; private set => Set(ref _poolThresholdText, value); }
    public double PayoutProgressPercent { get => _payoutProgressPercent; private set => Set(ref _payoutProgressPercent, value); }
    public string PayoutProgressText { get => _payoutProgressText; private set => Set(ref _payoutProgressText, value); }
    public string SessionAccumulationRate { get => _sessionAccumulationRate; private set => Set(ref _sessionAccumulationRate, value); }
    public string PoolRubEstimate { get => _poolRubEstimate; private set => Set(ref _poolRubEstimate, value); }
    public string MarketRate { get => _marketRate; private set => Set(ref _marketRate, value); }
    public string PoolUpdated { get => _poolUpdated; private set => Set(ref _poolUpdated, value); }
    public bool IsRefreshing { get => _isRefreshing; private set => Set(ref _isRefreshing, value); }
    public bool IsMinerRunning => _minerControl.IsRunning;
    public string MinerControlStatus { get => _minerControlStatus; private set => Set(ref _minerControlStatus, value); }
    public bool AutoStartMining { get => _autoStartMining; private set { if (Set(ref _autoStartMining, value)) OnPropertyChanged(nameof(MinerModeLabel)); } }
    public string MinerModeLabel => AutoStartMining ? "АВТОЗАПУСК" : "ВРУЧНУЮ";
    public bool WatchdogEnabled { get => _watchdogEnabled; private set { if (Set(ref _watchdogEnabled, value)) OnPropertyChanged(nameof(WatchdogButtonLabel)); } }
    public string WatchdogButtonLabel => WatchdogEnabled ? "WATCHDOG: ВКЛ" : "WATCHDOG: ВЫКЛ";
    public bool SmartFanCurveEnabled { get => _smartFanCurveEnabled; private set { if (Set(ref _smartFanCurveEnabled, value)) OnPropertyChanged(nameof(SmartFanButtonLabel)); } }
    public string SmartFanButtonLabel => SmartFanCurveEnabled ? "АВТО-КУЛЕРЫ: ВКЛ" : "АВТО-КУЛЕРЫ: ВЫКЛ";
    public bool DualTariffEnabled { get => _dualTariffEnabled; private set { if (Set(ref _dualTariffEnabled, value)) OnPropertyChanged(nameof(DualTariffButtonLabel)); } }
    public string DualTariffButtonLabel => DualTariffEnabled ? "ТАРИФ: ДЕНЬ/НОЧЬ" : "ТАРИФ: ЕДИНЫЙ";
    public bool NightScheduleEnabled { get => _nightScheduleEnabled; private set { if (Set(ref _nightScheduleEnabled, value)) OnPropertyChanged(nameof(NightScheduleButtonLabel)); } }
    public string NightScheduleButtonLabel => NightScheduleEnabled ? "РАСПИСАНИЕ: ВКЛ" : "РАСПИСАНИЕ: ВЫКЛ";
    public bool AutoRestartMinerOnOverclockChange { get => _autoRestartMinerOnOverclockChange; set => Set(ref _autoRestartMinerOnOverclockChange, value); }
    public bool AutoSwitchToBackupPoolOnDrop { get => _autoSwitchToBackupPoolOnDrop; set => Set(ref _autoSwitchToBackupPoolOnDrop, value); }
    public string AutoTuneButtonLabel => _isAutoTuning ? "АВТО-ПОДБОР…" : "АВТО-ПОДБОР GH/W";
    public bool IsAutoTuning => _isAutoTuning;
    public bool HasAutoTuneResults => AutoTuneSteps.Count > 0;
    public double AutoTuneProgressPercent { get => _autoTuneProgressPercent; private set => Set(ref _autoTuneProgressPercent, value); }
    public string AutoTuneProgressText { get => _autoTuneProgressText; private set => Set(ref _autoTuneProgressText, value); }
    public bool UseIndividualGpuOverclock
    {
        get => _useIndividualGpuOverclock;
        set
        {
            if (!Set(ref _useIndividualGpuOverclock, value)) return;
            if (value) SyncOverclockRowsFromDrafts();
        }
    }
    public string ActivePreset { get => _activePreset; private set { if (Set(ref _activePreset, value)) OnPropertyChanged(nameof(ActivePresetLabel)); } }
    public string ActivePresetLabel => $"ПРОФИЛЬ: {_activePreset.ToUpperInvariant()}";
    public string WatchdogStatusText { get => _watchdogStatusText; private set => Set(ref _watchdogStatusText, value); }
    public string WatchdogEventsText
    {
        get => _watchdogEventsText;
        private set
        {
            if (Set(ref _watchdogEventsText, value))
                OnPropertyChanged(nameof(FilteredWatchdogEventsText));
        }
    }
    public string MinerLogTail
    {
        get => _minerLogTail;
        private set
        {
            if (Set(ref _minerLogTail, value))
                OnPropertyChanged(nameof(FilteredMinerLogTail));
        }
    }
    public string AppLogTail
    {
        get => _appLogTail;
        private set
        {
            if (Set(ref _appLogTail, value))
                OnPropertyChanged(nameof(FilteredAppLogTail));
        }
    }
    public string LogSearchQuery
    {
        get => _logSearchQuery;
        set
        {
            if (!Set(ref _logSearchQuery, value)) return;
            OnPropertyChanged(nameof(FilteredMinerLogTail));
            OnPropertyChanged(nameof(FilteredWatchdogEventsText));
            OnPropertyChanged(nameof(FilteredAppLogTail));
        }
    }
    public bool LogOnlyWarningsAndErrors
    {
        get => _logOnlyWarningsAndErrors;
        set
        {
            if (!Set(ref _logOnlyWarningsAndErrors, value)) return;
            OnPropertyChanged(nameof(FilteredMinerLogTail));
            OnPropertyChanged(nameof(FilteredWatchdogEventsText));
            OnPropertyChanged(nameof(FilteredAppLogTail));
        }
    }
    public bool LogAutoScrollEnabled { get => _logAutoScrollEnabled; set => Set(ref _logAutoScrollEnabled, value); }
    public string FilteredMinerLogTail => FilterLogText(MinerLogTail);
    public string FilteredWatchdogEventsText => FilterLogText(WatchdogEventsText);
    public string FilteredAppLogTail => FilterLogText(AppLogTail);
    public string MinerCommandLinePreview { get => _minerCommandLinePreview; private set => Set(ref _minerCommandLinePreview, value); }

    public string WorkerNameDraft
    {
        get => _workerNameDraft;
        set
        {
            if (!Set(ref _workerNameDraft, value)) return;
            SaveMinerSettingsCommand.NotifyCanExecuteChanged();
            UpdateCommandLinePreviewFromDrafts();
        }
    }

    public string CoreClockDraft
    {
        get => _coreClockDraft;
        set
        {
            if (!Set(ref _coreClockDraft, value)) return;
            SaveMinerSettingsCommand.NotifyCanExecuteChanged();
            UpdateCommandLinePreviewFromDrafts();
            SyncOverclockRowsFromDrafts();
        }
    }

    public string MemoryClockDraft
    {
        get => _memoryClockDraft;
        set
        {
            if (!Set(ref _memoryClockDraft, value)) return;
            SaveMinerSettingsCommand.NotifyCanExecuteChanged();
            UpdateCommandLinePreviewFromDrafts();
            SyncOverclockRowsFromDrafts();
        }
    }

    public string PowerLimitDraft
    {
        get => _powerLimitDraft;
        set
        {
            if (!Set(ref _powerLimitDraft, value)) return;
            SaveMinerSettingsCommand.NotifyCanExecuteChanged();
            if (TryExtractFirstWatts(value, out var watts))
            {
                Set(ref _powerSliderValue, Math.Clamp(watts, PowerSliderMinimum, PowerSliderMaximum), nameof(PowerSliderValue));
            }
            else if (IsAutoSetting(value))
            {
                var defW = Gpus.FirstOrDefault()?.DefaultPowerLimitWatts
                    ?? Gpus.FirstOrDefault()?.PowerLimitWatts
                    ?? Gpus.FirstOrDefault()?.EnforcedPowerLimitWatts;
                if (defW is double d)
                    Set(ref _powerSliderValue, Math.Clamp(Math.Round(d), PowerSliderMinimum, PowerSliderMaximum), nameof(PowerSliderValue));
            }
            UpdateCommandLinePreviewFromDrafts();
            SyncOverclockRowsFromDrafts();
        }
    }

    public double PowerSliderValue
    {
        get => _powerSliderValue;
        set
        {
            var watts = Math.Round(Math.Clamp(value, PowerSliderMinimum, PowerSliderMaximum));
            var changed = Set(ref _powerSliderValue, watts);
            var valueText = watts.ToString(CultureInfo.InvariantCulture);
            var count = GetTargetNvidiaGpuCount();
            var repeated = string.Join(',', Enumerable.Repeat(valueText, count));
            if (changed || IsAutoSetting(_powerLimitDraft) || !string.Equals(_powerLimitDraft, repeated, StringComparison.Ordinal))
            {
                PowerLimitDraft = repeated;
                ActivePreset = $"Custom-{valueText}W";
            }
        }
    }

    public double PowerSliderMinimum { get => _powerSliderMinimum; private set => Set(ref _powerSliderMinimum, value); }
    public double PowerSliderMaximum { get => _powerSliderMaximum; private set => Set(ref _powerSliderMaximum, value); }
    public bool CanAdjustPowerLimit { get => _canAdjustPowerLimit; private set => Set(ref _canAdjustPowerLimit, value); }
    public string PowerRangeHint { get => _powerRangeHint; private set => Set(ref _powerRangeHint, value); }

    public string KernelDraft
    {
        get => _kernelDraft;
        set
        {
            if (!Set(ref _kernelDraft, value)) return;
            SaveMinerSettingsCommand.NotifyCanExecuteChanged();
            UpdateCommandLinePreviewFromDrafts();
            SyncOverclockRowsFromDrafts();
        }
    }

    public string FanDraft
    {
        get => _fanDraft;
        set
        {
            if (!Set(ref _fanDraft, value)) return;
            SaveMinerSettingsCommand.NotifyCanExecuteChanged();
            UpdateCommandLinePreviewFromDrafts();
            SyncOverclockRowsFromDrafts();
        }
    }

    public string ElectricityRateDraft { get => _electricityRateDraft; set { if (Set(ref _electricityRateDraft, value)) { SaveMinerSettingsCommand.NotifyCanExecuteChanged(); RefreshSchedulePreview(); } } }
    public string NightElectricityRateDraft { get => _nightElectricityRateDraft; set { if (Set(ref _nightElectricityRateDraft, value)) { SaveMinerSettingsCommand.NotifyCanExecuteChanged(); RefreshSchedulePreview(); } } }

    public string FanCurveTempLowCDraft { get => _fanCurveTempLowCDraft; set { if (Set(ref _fanCurveTempLowCDraft, value)) NotifyFanCurveChanged(); } }
    public string FanCurveSpeedLowPctDraft { get => _fanCurveSpeedLowPctDraft; set { if (Set(ref _fanCurveSpeedLowPctDraft, value)) NotifyFanCurveChanged(); } }
    public string FanCurveTempMidCDraft { get => _fanCurveTempMidCDraft; set { if (Set(ref _fanCurveTempMidCDraft, value)) NotifyFanCurveChanged(); } }
    public string FanCurveSpeedMidPctDraft { get => _fanCurveSpeedMidPctDraft; set { if (Set(ref _fanCurveSpeedMidPctDraft, value)) NotifyFanCurveChanged(); } }
    public string FanCurveTempHighCDraft { get => _fanCurveTempHighCDraft; set { if (Set(ref _fanCurveTempHighCDraft, value)) NotifyFanCurveChanged(); } }
    public string FanCurveSpeedHighPctDraft { get => _fanCurveSpeedHighPctDraft; set { if (Set(ref _fanCurveSpeedHighPctDraft, value)) NotifyFanCurveChanged(); } }
    public string FanHysteresisCDraft { get => _fanHysteresisCDraft; set { if (Set(ref _fanHysteresisCDraft, value)) NotifyFanCurveChanged(); } }
    public string VramTempOffsetCDraft { get => _vramTempOffsetCDraft; set { if (Set(ref _vramTempOffsetCDraft, value)) NotifyFanCurveChanged(); } }

    public double FanCurveTempLowValue => TryParseDoubleOrFallback(FanCurveTempLowCDraft, _config.FanCurveTempLowC);
    public double FanCurveSpeedLowValue => TryParseDoubleOrFallback(FanCurveSpeedLowPctDraft, _config.FanCurveSpeedLowPct);
    public double FanCurveTempMidValue => TryParseDoubleOrFallback(FanCurveTempMidCDraft, _config.FanCurveTempMidC);
    public double FanCurveSpeedMidValue => TryParseDoubleOrFallback(FanCurveSpeedMidPctDraft, _config.FanCurveSpeedMidPct);
    public double FanCurveTempHighValue => TryParseDoubleOrFallback(FanCurveTempHighCDraft, _config.FanCurveTempHighC);
    public double FanCurveSpeedHighValue => TryParseDoubleOrFallback(FanCurveSpeedHighPctDraft, _config.FanCurveSpeedHighPct);
    public double? Gpu0EffectiveTemp { get => _gpu0EffectiveTemp; private set => Set(ref _gpu0EffectiveTemp, value); }
    public double? Gpu1EffectiveTemp { get => _gpu1EffectiveTemp; private set => Set(ref _gpu1EffectiveTemp, value); }
    public string FanCurveValidationError => ValidateFanCurveDrafts();

    public string WarningTempCDraft { get => _warningTempCDraft; set { if (Set(ref _warningTempCDraft, value)) OnPropertyChanged(nameof(WatchdogValidationError)); } }
    public string CriticalStopTempCDraft { get => _criticalStopTempCDraft; set { if (Set(ref _criticalStopTempCDraft, value)) OnPropertyChanged(nameof(WatchdogValidationError)); } }
    public string ResumeTempCDraft { get => _resumeTempCDraft; set { if (Set(ref _resumeTempCDraft, value)) OnPropertyChanged(nameof(WatchdogValidationError)); } }
    public string ZeroHashrateRestartSecondsDraft { get => _zeroHashrateRestartSecondsDraft; set { if (Set(ref _zeroHashrateRestartSecondsDraft, value)) OnPropertyChanged(nameof(WatchdogValidationError)); } }
    public string PoolDropSwitchThresholdCountDraft { get => _poolDropSwitchThresholdCountDraft; set { if (Set(ref _poolDropSwitchThresholdCountDraft, value)) OnPropertyChanged(nameof(WatchdogValidationError)); } }
    public string AutoTuneStepSecondsDraft { get => _autoTuneStepSecondsDraft; set { if (Set(ref _autoTuneStepSecondsDraft, value)) OnPropertyChanged(nameof(WatchdogValidationError)); } }
    public string DayTariffStartHourDraft { get => _dayTariffStartHourDraft; set { if (Set(ref _dayTariffStartHourDraft, value)) { OnPropertyChanged(nameof(DayTariffStartHourValue)); OnPropertyChanged(nameof(WatchdogValidationError)); RefreshSchedulePreview(); } } }
    public string NightTariffStartHourDraft { get => _nightTariffStartHourDraft; set { if (Set(ref _nightTariffStartHourDraft, value)) { OnPropertyChanged(nameof(NightTariffStartHourValue)); OnPropertyChanged(nameof(WatchdogValidationError)); RefreshSchedulePreview(); } } }
    public int DayTariffStartHourValue => int.TryParse(DayTariffStartHourDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ? Math.Clamp(h, 0, 23) : _config.DayTariffStartHour;
    public int NightTariffStartHourValue => int.TryParse(NightTariffStartHourDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ? Math.Clamp(h, 0, 23) : _config.NightTariffStartHour;
    public string CurrentScheduleStatusText { get => _currentScheduleStatusText; private set => Set(ref _currentScheduleStatusText, value); }
    public string WatchdogValidationError => ValidateWatchdogDrafts();
    public string DaySchedulePreset { get => _daySchedulePreset; set { if (Set(ref _daySchedulePreset, value)) RefreshSchedulePreview(); } }
    public string NightSchedulePreset { get => _nightSchedulePreset; set { if (Set(ref _nightSchedulePreset, value)) RefreshSchedulePreview(); } }
    public string PrimaryPoolDraft { get => _primaryPoolDraft; set { if (Set(ref _primaryPoolDraft, value)) OnPropertyChanged(nameof(WatchdogValidationError)); } }
    public string BackupPoolDraft { get => _backupPoolDraft; set { if (Set(ref _backupPoolDraft, value)) OnPropertyChanged(nameof(WatchdogValidationError)); } }

    public bool TelegramEnabled { get => _telegramEnabled; set => Set(ref _telegramEnabled, value); }
    public bool TelegramRemoteControlEnabled { get => _telegramRemoteControlEnabled; set => Set(ref _telegramRemoteControlEnabled, value); }
    public string TelegramBotTokenDraft { get => _telegramBotTokenDraft; set { if (Set(ref _telegramBotTokenDraft, value)) OnPropertyChanged(nameof(HasSavedTelegramToken)); } }
    public bool HasSavedTelegramToken => !string.IsNullOrWhiteSpace(_telegramBotTokenDraft);
    public string TelegramChatIdDraft { get => _telegramChatIdDraft; set => Set(ref _telegramChatIdDraft, value); }
    public string TelegramStatusText { get => _telegramStatusText; private set => Set(ref _telegramStatusText, value); }
    public bool DiagAdminOk { get => _diagAdminOk; private set => Set(ref _diagAdminOk, value); }
    public string DiagAdminText { get => _diagAdminText; private set => Set(ref _diagAdminText, value); }
    public bool DiagNvmlOk { get => _diagNvmlOk; private set => Set(ref _diagNvmlOk, value); }
    public string DiagNvmlText { get => _diagNvmlText; private set => Set(ref _diagNvmlText, value); }
    public bool DiagMinerExeOk { get => _diagMinerExeOk; private set => Set(ref _diagMinerExeOk, value); }
    public string DiagMinerExeText { get => _diagMinerExeText; private set => Set(ref _diagMinerExeText, value); }
    public bool DiagAutoStartOk { get => _diagAutoStartOk; private set => Set(ref _diagAutoStartOk, value); }
    public string DiagAutoStartText { get => _diagAutoStartText; private set => Set(ref _diagAutoStartText, value); }
    public bool DiagTdrOk { get => _diagTdrOk; private set => Set(ref _diagTdrOk, value); }
    public string DiagTdrText { get => _diagTdrText; private set => Set(ref _diagTdrText, value); }
    public bool HasNoGpus => Gpus.Count == 0;
    public string GpuCountLabel => !_gpuDiscoveryCompleted ? "Поиск видеокарт…" : Gpus.Count == 0 ? "GPU не обнаружены" : $"Обнаружено GPU: {Gpus.Count}";
    public double MaxGpuHashrateTHs => Math.Max(1, Gpus.Where(gpu => gpu.HashrateTHs is not null).Select(gpu => gpu.HashrateTHs!.Value).DefaultIfEmpty(1).Max() * 1.25);
    public string ComputerSummary { get; } = ComputerInfoClient.GetSummary();
    public string InstalledVersionText => $"УСТАНОВЛЕНА v{_installedVersion.ToString(3)}";
    public bool UpdateAvailable => _availableUpdate is not null;
    public bool IsDownloadingUpdate => _isDownloadingUpdate;
    public double UpdateProgress => _updateProgress;
    public string UpdateStatusText { get => _updateStatusText; private set => Set(ref _updateStatusText, value); }
    public string UpdateButtonText => _isDownloadingUpdate
        ? $"v{_installedVersion.ToString(3)} · {UpdateProgress:0}%"
        : _availableUpdate is not null ? $"v{_installedVersion.ToString(3)} · ОБНОВИТЬ" : $"v{_installedVersion.ToString(3)} · ПРОВЕРИТЬ";

    private void RestorePersistedSession()
    {
        var saved = SessionStateService.Load();
        if (saved is null) return;

        _profitability.RestoreSessionEnergy(saved.SessionEnergyKwh, saved.SessionElectricityCostRub);
        foreach (var sample in saved.TotalHashrateHistory.TakeLast(MaxHistorySamples))
        {
            if (double.IsFinite(sample) && sample >= 0)
                _totalHashrateBuffer.Add(sample);
        }
        TotalHashrateHistory = _totalHashrateBuffer.ToArray();

        foreach (var evt in saved.WatchdogEvents.TakeLast(35))
        {
            if (!string.IsNullOrWhiteSpace(evt))
                _watchdogEvents.Add(evt);
        }
        if (_watchdogEvents.Count > 0)
            WatchdogEventsText = string.Join(Environment.NewLine, Enumerable.Reverse(_watchdogEvents));
    }

    public void Start(bool launchedWithAutoMineArg = false)
    {
        _lifetime = new CancellationTokenSource();
        _timer.Start();
        UpdateMinerState();
        _ = InitializeStartupTelemetryAndPowerAsync(launchedWithAutoMineArg);
        _ = CheckForUpdatesAsync();
    }

    private async Task InitializeStartupTelemetryAndPowerAsync(bool launchedWithAutoMineArg)
    {
        await RefreshAsync(force: true);

        if (!string.IsNullOrWhiteSpace(_config.PowerLimit))
        {
            try
            {
                var applied = await _nvidiaSmi.ApplyPowerLimitsAsync(_config.PowerLimit, _lifetime?.Token ?? CancellationToken.None);
                AppendWatchdogEvent($"Восстановлен сохранённый Power Limit: {applied}");
            }
            catch
            {
                // Ignore startup power limit restore error if user canceled or driver not ready.
            }
        }

        var shouldResumeAfterReboot = _config.ResumeMiningAfterReboot && _config.LastKnownMiningActive && AutoStartMining;
        if ((launchedWithAutoMineArg || shouldResumeAfterReboot) && !_minerControl.IsRunning && PoolEarningsClient.IsValidWallet(_walletAddress))
        {
            AppendWatchdogEvent("Автовосстановление сессии после загрузки Windows: запуск майнера.");
            await StartMinerAsync(manual: false);
        }
    }

    public void ApplyPresetFromTray(string presetName) => _ = ApplyPresetAndSaveAsync(presetName);

    public async Task ApplySliderPowerLimitAsync()
    {
        if (_isMinerBusy || _isAutoTuning || _disposed) return;
        var count = GetTargetNvidiaGpuCount();
        var watts = (int)Math.Round(Math.Clamp(PowerSliderValue, PowerSliderMinimum, PowerSliderMaximum));
        var valueText = watts.ToString(CultureInfo.InvariantCulture);
        var repeated = string.Join(',', Enumerable.Repeat(valueText, count));
        PowerLimitDraft = repeated;
        ActivePreset = $"Custom-{valueText}W";
        await SaveMinerSettingsAsync();
    }

    private async Task ApplyPresetAndSaveAsync(string presetName)
    {
        ApplyPreset(presetName);
        await SaveMinerSettingsAsync();
    }

    private void ApplyPreset(string presetName)
    {
        var count = GetTargetNvidiaGpuCount();
        string Repeat(string v) => string.Join(',', Enumerable.Repeat(v, count));

        int ClampPower(int desired)
        {
            var min = (int)Math.Ceiling(PowerSliderMinimum);
            var max = (int)Math.Floor(PowerSliderMaximum);
            return min <= max ? Math.Clamp(desired, min, max) : desired;
        }

        switch (presetName)
        {
            case "Eco":
                CoreClockDraft = Repeat("2250");
                MemoryClockDraft = Repeat("810");
                PowerLimitDraft = Repeat(ClampPower(135).ToString(CultureInfo.InvariantCulture));
                KernelDraft = Repeat("2");
                FanDraft = Repeat("60");
                break;
            case "Balance":
                CoreClockDraft = Repeat("2400");
                MemoryClockDraft = Repeat("810");
                PowerLimitDraft = Repeat(ClampPower(150).ToString(CultureInfo.InvariantCulture));
                KernelDraft = Repeat("2");
                FanDraft = Repeat("65");
                break;
            case "Turbo":
                CoreClockDraft = Repeat("2610");
                MemoryClockDraft = Repeat("5001");
                PowerLimitDraft = Repeat(ClampPower(185).ToString(CultureInfo.InvariantCulture));
                KernelDraft = Repeat("2");
                FanDraft = Repeat("75");
                break;
            case "Silent":
                CoreClockDraft = Repeat("2100");
                MemoryClockDraft = Repeat("810");
                PowerLimitDraft = Repeat(ClampPower(125).ToString(CultureInfo.InvariantCulture));
                KernelDraft = Repeat("1");
                FanDraft = Repeat("50");
                break;
            default:
                presetName = "Auto";
                CoreClockDraft = "Авто";
                MemoryClockDraft = "Авто";
                PowerLimitDraft = "Авто";
                KernelDraft = "Авто";
                FanDraft = "Авто";
                break;
        }

        ActivePreset = presetName;
        UpdateCommandLinePreviewFromDrafts();
        AppendWatchdogEvent($"Выбран профиль разгона: {presetName}");
    }

    private async Task ToggleDualTariffAsync()
    {
        var enabled = !DualTariffEnabled;
        _config = _config with { DualTariffEnabled = enabled };
        await RigConfig.SaveLocalSettingsAsync(_config, _lifetime?.Token ?? CancellationToken.None);
        DualTariffEnabled = enabled;
        RefreshSchedulePreview();
        var msg = enabled
            ? $"Включён двухтарифный учёт (День {_config.DayTariffStartHour:00}:00–{_config.NightTariffStartHour:00}:00 · Ночь {_config.NightTariffStartHour:00}:00–{_config.DayTariffStartHour:00}:00)"
            : "Включён единый круглосуточный тариф электроэнергии";
        MinerControlStatus = msg;
        AppendWatchdogEvent(msg);
        ShowToast(msg, "Info");
    }

    private async Task ToggleNightScheduleAsync()
    {
        var enabled = !NightScheduleEnabled;
        _config = _config with { NightScheduleEnabled = enabled };
        await RigConfig.SaveLocalSettingsAsync(_config, _lifetime?.Token ?? CancellationToken.None);
        NightScheduleEnabled = enabled;
        _lastScheduledWindowApplied = null;
        RefreshSchedulePreview();
        var msg = enabled
            ? $"Авто-расписание День/Ночь включено (День → {_config.DaySchedulePreset}, Ночь → {_config.NightSchedulePreset})"
            : "Авто-расписание профилей День/Ночь отключено";
        MinerControlStatus = msg;
        AppendWatchdogEvent(msg);
        ShowToast(msg, "Info");
    }

    private async Task EvaluateDayNightScheduleAsync()
    {
        if (!NightScheduleEnabled || _isMinerBusy || _isAutoTuning || Gpus.Count == 0) return;

        var isNight = _config.IsNightTariffWindow(DateTime.Now);
        var windowKey = isNight ? "Night" : "Day";
        if (string.Equals(_lastScheduledWindowApplied, windowKey, StringComparison.Ordinal))
            return;

        _lastScheduledWindowApplied = windowKey;
        var targetPreset = isNight ? _config.NightSchedulePreset : _config.DaySchedulePreset;
        if (string.Equals(ActivePreset, targetPreset, StringComparison.OrdinalIgnoreCase))
            return;

        ApplyPreset(targetPreset);
        AppendWatchdogEvent($"Расписание {(isNight ? "НОЧЬ" : "ДЕНЬ")}: автоматический переход на профиль «{targetPreset}».");
        await SaveMinerSettingsAsync();
    }

    private async Task RunAutoTuneBenchmarkAsync()
    {
        if (_isAutoTuning || _isMinerBusy || !_minerControl.IsRunning) return;
        var nvidiaGpus = Gpus.Where(IsNvidiaGpu).ToArray();
        if (nvidiaGpus.Length == 0)
        {
            MinerControlStatus = "Авто-подбор требует активные видеокарты NVIDIA и запущенный майнер.";
            ShowToast(MinerControlStatus, "Warning");
            return;
        }

        _isAutoTuning = true;
        AutoTuneSteps.Clear();
        AutoTuneProgressPercent = 0;
        AutoTuneProgressText = "Подготовка ступеней энергопотребления…";
        OnPropertyChanged(nameof(IsAutoTuning));
        OnPropertyChanged(nameof(HasAutoTuneResults));
        OnPropertyChanged(nameof(AutoTuneButtonLabel));
        UpdateMinerState();

        var minW = (int)Math.Ceiling(PowerSliderMinimum);
        var maxW = (int)Math.Floor(PowerSliderMaximum);
        var defaultW = (int)Math.Round(nvidiaGpus[0].DefaultPowerLimitWatts ?? maxW);
        var spanTop = Math.Min(maxW, Math.Max(minW + 20, defaultW));
        var candidates = Enumerable.Range(0, 5)
            .Select(step => minW + (int)Math.Round((spanTop - minW) * (0.20 + step * 0.18)))
            .Select(w => minW <= maxW ? Math.Clamp(w, minW, maxW) : w)
            .Distinct()
            .Order()
            .ToArray();

        var settleSeconds = Math.Clamp(_config.AutoTuneStepSeconds, 20, 120);
        AppendWatchdogEvent($"Старт авто-бенчмарка GH/W (шаг {settleSeconds} с): {string.Join(", ", candidates.Select(c => $"{c} Вт"))}");
        var bestWatts = candidates[0];
        var bestEfficiency = 0d;
        var token = _lifetime?.Token ?? CancellationToken.None;

        try
        {
            for (var idx = 0; idx < candidates.Length; idx++)
            {
                var candidateW = candidates[idx];
                var stepNum = idx + 1;
                AutoTuneProgressPercent = Math.Round((idx / (double)candidates.Length) * 100d);
                AutoTuneProgressText = $"Шаг {stepNum} из {candidates.Length} · Тест {candidateW} Вт (стабилизация {settleSeconds} с)…";
                var limitStr = string.Join(',', Enumerable.Repeat(candidateW.ToString(CultureInfo.InvariantCulture), nvidiaGpus.Length));
                MinerControlStatus = AutoTuneProgressText;
                await _nvidiaSmi.ApplyPowerLimitsAsync(limitStr, token);
                await Task.Delay(TimeSpan.FromSeconds(settleSeconds), token);

                var snap = await _telemetry.ReadAsync(token);
                Apply(snap);

                var power = snap.Gpus.Where(g => g.PowerWatts is not null).Sum(g => g.PowerWatts!.Value);
                var hashrate = snap.TotalHashrateTHs ?? 0;
                var maxTemp = snap.Gpus.Where(g => g.TempC is not null).Select(g => g.TempC!.Value).DefaultIfEmpty(0).Max();
                var eff = hashrate > 0 && power > 0 ? (hashrate * 1000d) / power : 0;

                var isNewBest = eff > bestEfficiency && maxTemp < _config.WarningTempC;
                if (isNewBest)
                {
                    bestEfficiency = eff;
                    bestWatts = candidateW;
                    for (var j = 0; j < AutoTuneSteps.Count; j++)
                        AutoTuneSteps[j] = AutoTuneSteps[j] with { IsBest = false };
                }

                AutoTuneSteps.Add(new AutoTuneStepResult(stepNum, candidateW, hashrate, power, eff, maxTemp, isNewBest));
                OnPropertyChanged(nameof(HasAutoTuneResults));

                var stepResult = $"Ступень {candidateW} Вт → {hashrate:0.00} TH/s · {power:0} Вт · {eff:0.00} GH/W · макс {maxTemp:0} °C";
                AppendWatchdogEvent(stepResult);
            }

            AutoTuneProgressPercent = 100;
            AutoTuneProgressText = $"Готово: оптимальный лимит {bestWatts} Вт ({bestEfficiency:0.00} GH/W)";
            PowerLimitDraft = string.Join(',', Enumerable.Repeat(bestWatts.ToString(CultureInfo.InvariantCulture), nvidiaGpus.Length));
            ActivePreset = $"Tuned-{bestWatts}W";
            await SaveMinerSettingsAsync();

            var summary = $"Авто-бенчмарк завершён: выбрана золотая точка {bestWatts} Вт ({bestEfficiency:0.00} GH/W).";
            MinerControlStatus = summary;
            AppendWatchdogEvent(summary);
            ShowToast(summary, "Success", 7);
            await RecordCsvSnapshotAsync(summary);
            await SendTelegramAlertIfEnabledAsync($"🏆 {summary}");
        }
        catch (OperationCanceledException)
        {
            MinerControlStatus = "Авто-подбор энергоэффективности отменён.";
            AutoTuneProgressText = "Бенчмарк отменён";
        }
        catch (Exception ex)
        {
            MinerControlStatus = $"Ошибка авто-подбора: {ex.Message}";
            AutoTuneProgressText = MinerControlStatus;
            AppendWatchdogEvent($"Авто-бенчмарк прерван: {ex.Message}");
            ShowToast(MinerControlStatus, "Error");
        }
        finally
        {
            _isAutoTuning = false;
            OnPropertyChanged(nameof(IsAutoTuning));
            OnPropertyChanged(nameof(AutoTuneButtonLabel));
            UpdateMinerState();
        }
    }

    private async Task ExportCsvReportAsync()
    {
        try
        {
            await RecordCsvSnapshotAsync("Ручная выгрузка отчёта CSV пользователем");
            TelemetryHistoryCsvService.RevealInExplorer();
            MinerControlStatus = $"Отчёт CSV сохранён: {TelemetryHistoryCsvService.GetCsvFilePath()}";
        }
        catch (Exception ex)
        {
            MinerControlStatus = $"Не удалось выгрузить CSV: {ex.Message}";
        }
    }

    private static void OpenLogsFolder()
    {
        try
        {
            var path = AppLogger.GetLogFilePath();
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true });
            }
            else
            {
                var dir = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{dir}\"", UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Не удалось открыть папку логов: {ex.Message}");
        }
    }

    private async Task RecordCsvSnapshotAsync(string eventNote)
    {
        try
        {
            var maxCore = Gpus.Where(g => g.TempC is not null).Select(g => g.TempC!.Value).DefaultIfEmpty().Max();
            var maxVram = Gpus.Where(g => g.VramTempC is not null).Select(g => g.VramTempC!.Value).DefaultIfEmpty().Max();
            var entry = new CsvHistoryEntry(
                DateTimeOffset.Now,
                _lastTotalHashrateTHs,
                _lastTotalPowerWatts,
                _lastEfficiencyGhPerWatt,
                maxCore > 0 ? maxCore : null,
                maxVram > 0 ? maxVram : null,
                _profitability.SessionEnergyKwh,
                _config.GetActiveElectricityTariff(DateTime.Now),
                _lastKnownTotalPrl,
                _lastKnownPaidPrl,
                _lastKnownPrlRubRate,
                _profitability.LastObservedNetRubPerDay,
                eventNote);
            await TelemetryHistoryCsvService.AppendEntryAsync(entry, _lifetime?.Token ?? CancellationToken.None);
            _lastCsvSnapshotAt = DateTimeOffset.UtcNow;
        }
        catch
        {
            // Non-critical disk logging.
        }
    }

    private void CopyCommandLineToClipboard()
    {
        try
        {
            Clipboard.SetText(MinerCommandLinePreview);
            MinerControlStatus = "Командная строка OneZeroMiner скопирована в буфер обмена";
        }
        catch (Exception ex)
        {
            MinerControlStatus = $"Не удалось скопировать команду: {ex.Message}";
        }
    }

    private void AppendWatchdogEvent(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _watchdogEvents.Add(line);
        if (_watchdogEvents.Count > 35)
            _watchdogEvents.RemoveAt(0);
        WatchdogEventsText = string.Join(Environment.NewLine, Enumerable.Reverse(_watchdogEvents));
        AppLogger.Info(message);
    }

    private async Task SendTelegramAlertIfEnabledAsync(string message, bool bypassCooldown = false)
    {
        if (!TelegramEnabled || string.IsNullOrWhiteSpace(TelegramBotTokenDraft) || string.IsNullOrWhiteSpace(TelegramChatIdDraft))
            return;
        var (ok, status) = await _telegramService.SendAsync(TelegramBotTokenDraft, TelegramChatIdDraft, message, bypassCooldown, _lifetime?.Token ?? CancellationToken.None);
        TelegramStatusText = status;
        if (!ok) AppendWatchdogEvent($"Telegram: {status}");
    }

    private async Task PollTelegramRemoteCommandsAsync()
    {
        if (!TelegramEnabled || !TelegramRemoteControlEnabled || string.IsNullOrWhiteSpace(TelegramBotTokenDraft) || string.IsNullOrWhiteSpace(TelegramChatIdDraft))
            return;
        if (DateTimeOffset.UtcNow - _lastTelegramPollAt < TimeSpan.FromSeconds(6))
            return;

        _lastTelegramPollAt = DateTimeOffset.UtcNow;
        var token = _lifetime?.Token ?? CancellationToken.None;
        var commands = await _telegramService.PollAuthorizedCommandsAsync(TelegramBotTokenDraft, TelegramChatIdDraft, token);
        foreach (var rawCmd in commands)
        {
            var normalized = rawCmd.Trim().ToLowerInvariant();
            var atIdx = normalized.IndexOf('@');
            if (atIdx > 0) normalized = normalized[..atIdx];

            AppendWatchdogEvent($"Telegram-команда: {rawCmd}");
            switch (normalized)
            {
                case "/status":
                    var gpuSummary = Gpus.Count == 0
                        ? "GPU не обнаружены"
                        : string.Join("\n", Gpus.Select(g => $"• GPU#{g.Id}: {g.HashrateText} | {g.TemperatureText} (GDDR6X {g.VramTemperatureText}) | {g.PowerText} | {g.PState ?? "P?"}"));
                    var reply =
                        $"📊 Статус фермы ({ActivePresetLabel})\n" +
                        $"Хешрейт: {TotalHashrate} ({TotalEfficiency})\n" +
                        $"{gpuSummary}\n" +
                        $"Пул: {PoolStatus} ({PoolPingText})\n" +
                        $"Баланс: {PoolBalance} ({PoolRubEstimate})\n" +
                        $"{NetProfitSummaryText}";
                    await SendTelegramAlertIfEnabledAsync(reply, bypassCooldown: true);
                    break;

                case "/start_miner":
                    await StartMinerAsync(manual: true);
                    await SendTelegramAlertIfEnabledAsync("▶️ Команда /start_miner выполнена: запуск OneZeroMiner.", bypassCooldown: true);
                    break;

                case "/stop_miner":
                    await StopMinerAsync(manual: true);
                    await SendTelegramAlertIfEnabledAsync("⏹ Команда /stop_miner выполнена: OneZeroMiner остановлен.", bypassCooldown: true);
                    break;

                case "/reboot_miner":
                    await RestartMinerAsync();
                    await SendTelegramAlertIfEnabledAsync("🔄 Команда /reboot_miner выполнена: майнер перезапущен.", bypassCooldown: true);
                    break;

                case "/preset eco":
                case "/eco":
                    await ApplyPresetAndSaveAsync("Eco");
                    await SendTelegramAlertIfEnabledAsync("🌱 Профиль переключён на ECO.", bypassCooldown: true);
                    break;

                case "/preset balance":
                case "/balance":
                    await ApplyPresetAndSaveAsync("Balance");
                    await SendTelegramAlertIfEnabledAsync("⚖️ Профиль переключён на BALANCE.", bypassCooldown: true);
                    break;

                case "/preset turbo":
                case "/turbo":
                    await ApplyPresetAndSaveAsync("Turbo");
                    await SendTelegramAlertIfEnabledAsync("🚀 Профиль переключён на TURBO.", bypassCooldown: true);
                    break;

                case "/preset silent":
                case "/silent":
                    await ApplyPresetAndSaveAsync("Silent");
                    await SendTelegramAlertIfEnabledAsync("🌙 Профиль переключён на SILENT.", bypassCooldown: true);
                    break;

                case "/autotune":
                    _ = RunAutoTuneBenchmarkAsync();
                    await SendTelegramAlertIfEnabledAsync("🔬 Запущен авто-подбор золотой точки GH/W.", bypassCooldown: true);
                    break;
            }
        }
    }

    private async Task SaveTelegramSettingsAsync()
    {
        try
        {
            await RigConfig.SaveTelegramSecretsAsync(TelegramBotTokenDraft, TelegramChatIdDraft, _lifetime?.Token ?? CancellationToken.None);
            _config = _config with
            {
                TelegramEnabled = TelegramEnabled,
                TelegramRemoteControlEnabled = TelegramRemoteControlEnabled,
                TelegramBotToken = TelegramBotTokenDraft.Trim(),
                TelegramChatId = TelegramChatIdDraft.Trim()
            };
            await RigConfig.SaveLocalSettingsAsync(_config, _lifetime?.Token ?? CancellationToken.None);
            TelegramStatusText = TelegramEnabled
                ? (TelegramRemoteControlEnabled
                    ? "Telegram зашифрован (DPAPI) и активен (/status, /preset, /start_miner)"
                    : "Настройки Telegram зашифрованы (DPAPI) и сохранены")
                : "Настройки сохранены (отправка отключена)";
        }
        catch (Exception ex)
        {
            TelegramStatusText = $"Ошибка сохранения Telegram: {ex.Message}";
        }
    }

    private async Task TestTelegramAsync()
    {
        await SaveTelegramSettingsAsync();
        var summary = $"Тестовое уведомление.\nХешрейт: {TotalHashrate}\nПул: {PoolStatus} ({PoolPingText})\nБаланс: {PoolBalance} ({PoolRubEstimate})\nДоступные команды: /status, /start_miner, /stop_miner, /reboot_miner, /eco, /balance, /turbo, /autotune";
        var (_, status) = await _telegramService.SendAsync(TelegramBotTokenDraft, TelegramChatIdDraft, summary, bypassCooldown: true, _lifetime?.Token ?? CancellationToken.None);
        TelegramStatusText = status;
    }

    private async Task ToggleWatchdogAsync()
    {
        var enabled = !WatchdogEnabled;
        _config = _config with { WatchdogEnabled = enabled };
        await RigConfig.SaveLocalSettingsAsync(_config, _lifetime?.Token ?? CancellationToken.None);
        WatchdogEnabled = enabled;
        var stateMsg = enabled
            ? $"Watchdog и термозащита включены (стоп ≥ {_config.CriticalStopTempC} °C, рестарт 0 TH/s через {_config.ZeroHashrateRestartSeconds} с)"
            : "Watchdog и автоматическая термозащита отключены пользователем";
        WatchdogStatusText = stateMsg;
        AppendWatchdogEvent(stateMsg);
    }

    private async Task ToggleSmartFanCurveAsync()
    {
        var enabled = !SmartFanCurveEnabled;
        _config = _config with { SmartFanCurveEnabled = enabled };
        await RigConfig.SaveLocalSettingsAsync(_config, _lifetime?.Token ?? CancellationToken.None);
        SmartFanCurveEnabled = enabled;
        _smartFanController.ResetState();
        if (!enabled && Gpus.Count > 0)
        {
            var resetOk = _nvidiaSmi.TryResetFansToAutoDirect(Gpus.Where(IsNvidiaGpu).Select(g => g.Id));
            AppendWatchdogEvent(resetOk
                ? "Умная кривая кулеров отключена; вентиляторы возвращены в авто-режим VBIOS."
                : "Умная кривая кулеров отключена.");
        }
        else
        {
            AppendWatchdogEvent($"Умная кривая кулеров включена (гистерезис {_config.FanHysteresisC} °C, сдвиг GDDR6X {_config.VramTempOffsetC} °C).");
        }
        MinerControlStatus = enabled
            ? "Умная кривая кулеров активна"
            : "Умная кривая кулеров отключена (авто VBIOS)";
    }

    private async Task SaveFanCurveSettingsAsync()
    {
        if (!int.TryParse(FanCurveTempLowCDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tLow)
            || !int.TryParse(FanCurveSpeedLowPctDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sLow)
            || !int.TryParse(FanCurveTempMidCDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tMid)
            || !int.TryParse(FanCurveSpeedMidPctDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sMid)
            || !int.TryParse(FanCurveTempHighCDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tHigh)
            || !int.TryParse(FanCurveSpeedHighPctDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sHigh)
            || !int.TryParse(FanHysteresisCDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hyst)
            || !int.TryParse(VramTempOffsetCDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vramOffset))
        {
            MinerControlStatus = "Проверьте числовые значения точек кривой вентиляторов.";
            return;
        }

        _config = RigConfig.Migrate(_config with
        {
            FanCurveTempLowC = tLow,
            FanCurveSpeedLowPct = sLow,
            FanCurveTempMidC = tMid,
            FanCurveSpeedMidPct = sMid,
            FanCurveTempHighC = tHigh,
            FanCurveSpeedHighPct = sHigh,
            FanHysteresisC = hyst,
            VramTempOffsetC = vramOffset
        });

        await RigConfig.SaveLocalSettingsAsync(_config, _lifetime?.Token ?? CancellationToken.None);
        _smartFanController.ResetState();
        SyncDraftsFromConfig();
        var msg = $"Кривая кулеров сохранена: {_config.FanCurveTempLowC}°/{_config.FanCurveSpeedLowPct}% → {_config.FanCurveTempMidC}°/{_config.FanCurveSpeedMidPct}% → {_config.FanCurveTempHighC}°/{_config.FanCurveSpeedHighPct}%";
        MinerControlStatus = msg;
        AppendWatchdogEvent(msg);
        ShowToast(msg, "Success");
    }

    private async Task SaveWatchdogAndScheduleSettingsAsync()
    {
        if (!string.IsNullOrEmpty(WatchdogValidationError))
        {
            ShowToast(WatchdogValidationError, "Warning");
            return;
        }
        if (!int.TryParse(WarningTempCDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var warnT)
            || !int.TryParse(CriticalStopTempCDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var critT)
            || !int.TryParse(ResumeTempCDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resT)
            || !int.TryParse(ZeroHashrateRestartSecondsDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var zeroSec)
            || !int.TryParse(PoolDropSwitchThresholdCountDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var poolDropCount)
            || !int.TryParse(AutoTuneStepSecondsDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tuneSec)
            || !int.TryParse(DayTariffStartHourDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dayHour)
            || !int.TryParse(NightTariffStartHourDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nightHour))
        {
            MinerControlStatus = "Проверьте числовые значения порогов Watchdog и часов тарифов.";
            ShowToast(MinerControlStatus, "Warning");
            return;
        }

        _config = RigConfig.Migrate(_config with
        {
            WarningTempC = warnT,
            CriticalStopTempC = critT,
            ResumeTempC = resT,
            ZeroHashrateRestartSeconds = zeroSec,
            AutoSwitchToBackupPoolOnDrop = AutoSwitchToBackupPoolOnDrop,
            PoolDropSwitchThresholdCount = poolDropCount,
            AutoTuneStepSeconds = tuneSec,
            DayTariffStartHour = dayHour,
            NightTariffStartHour = nightHour,
            DaySchedulePreset = DaySchedulePreset,
            NightSchedulePreset = NightSchedulePreset,
            PrimaryPool = PrimaryPoolDraft,
            BackupPool = BackupPoolDraft
        });

        await RigConfig.SaveLocalSettingsAsync(_config, _lifetime?.Token ?? CancellationToken.None);
        _minerControl.UpdateConfig(_config);
        _lastScheduledWindowApplied = null;
        SyncDraftsFromConfig();
        RefreshSchedulePreview();
        UpdateCommandLinePreviewFromDrafts();
        var msg = $"Параметры Watchdog (стоп {_config.CriticalStopTempC}°C / возврат {_config.ResumeTempC}°C), расписания и пулов сохранены.";
        MinerControlStatus = msg;
        AppendWatchdogEvent(msg);
        ShowToast(msg, "Success");
    }

    private void SyncDraftsFromConfig()
    {
        FanCurveTempLowCDraft = _config.FanCurveTempLowC.ToString(CultureInfo.InvariantCulture);
        FanCurveSpeedLowPctDraft = _config.FanCurveSpeedLowPct.ToString(CultureInfo.InvariantCulture);
        FanCurveTempMidCDraft = _config.FanCurveTempMidC.ToString(CultureInfo.InvariantCulture);
        FanCurveSpeedMidPctDraft = _config.FanCurveSpeedMidPct.ToString(CultureInfo.InvariantCulture);
        FanCurveTempHighCDraft = _config.FanCurveTempHighC.ToString(CultureInfo.InvariantCulture);
        FanCurveSpeedHighPctDraft = _config.FanCurveSpeedHighPct.ToString(CultureInfo.InvariantCulture);
        FanHysteresisCDraft = _config.FanHysteresisC.ToString(CultureInfo.InvariantCulture);
        VramTempOffsetCDraft = _config.VramTempOffsetC.ToString(CultureInfo.InvariantCulture);
        WarningTempCDraft = _config.WarningTempC.ToString(CultureInfo.InvariantCulture);
        CriticalStopTempCDraft = _config.CriticalStopTempC.ToString(CultureInfo.InvariantCulture);
        ResumeTempCDraft = _config.ResumeTempC.ToString(CultureInfo.InvariantCulture);
        ZeroHashrateRestartSecondsDraft = _config.ZeroHashrateRestartSeconds.ToString(CultureInfo.InvariantCulture);
        PoolDropSwitchThresholdCountDraft = _config.PoolDropSwitchThresholdCount.ToString(CultureInfo.InvariantCulture);
        AutoTuneStepSecondsDraft = _config.AutoTuneStepSeconds.ToString(CultureInfo.InvariantCulture);
        DayTariffStartHourDraft = _config.DayTariffStartHour.ToString(CultureInfo.InvariantCulture);
        NightTariffStartHourDraft = _config.NightTariffStartHour.ToString(CultureInfo.InvariantCulture);
        PrimaryPoolDraft = _config.PrimaryPool;
        BackupPoolDraft = _config.BackupPool;
    }

    private async Task UpdateOrCheckAsync()
    {
        if (_availableUpdate is null)
        {
            await CheckForUpdatesAsync();
            return;
        }
        await DownloadUpdateAsync(_availableUpdate);
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_isCheckingUpdate || _isDownloadingUpdate || _disposed) return;
        _isCheckingUpdate = true;
        OnPropertyChanged(nameof(UpdateButtonText));
        UpdateCommand.NotifyCanExecuteChanged();
        UpdateStatusText = "Проверка GitHub Releases…";
        try
        {
            var release = await _updateService.CheckAsync(_installedVersion, _lifetime?.Token ?? CancellationToken.None);
            _availableUpdate = release;
            OnPropertyChanged(nameof(UpdateAvailable));
            OnPropertyChanged(nameof(UpdateButtonText));
            UpdateStatusText = release is null
                ? $"Установлена актуальная v{_installedVersion.ToString(3)}"
                : $"Доступна v{release.Version} ({release.SizeBytes / 1024d / 1024d:0.0} МБ) — нажмите для установки";
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { UpdateStatusText = $"Проверка обновлений недоступна: {ex.Message}"; }
        finally
        {
            _isCheckingUpdate = false;
            OnPropertyChanged(nameof(UpdateButtonText));
            UpdateCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task DownloadUpdateAsync(ReleaseUpdate release)
    {
        if (_isDownloadingUpdate || _disposed) return;
        _isDownloadingUpdate = true;
        _updateProgress = 0;
        OnPropertyChanged(nameof(IsDownloadingUpdate));
        OnPropertyChanged(nameof(UpdateProgress));
        OnPropertyChanged(nameof(UpdateButtonText));
        UpdateCommand.NotifyCanExecuteChanged();
        UpdateStatusText = $"Загрузка v{release.Version}…";
        var progress = new InlineProgress<DownloadProgress>(report =>
        {
            _updateProgress = report.Percent;
            OnPropertyChanged(nameof(UpdateProgress));
            OnPropertyChanged(nameof(UpdateButtonText));
            UpdateStatusText = report.TotalBytes > 0
                ? $"Загрузка v{release.Version}: {report.Percent}% · {report.BytesReceived / 1024d / 1024d:0.0} из {report.TotalBytes / 1024d / 1024d:0.0} МБ"
                : $"Загружено {report.BytesReceived / 1024d / 1024d:0.0} МБ";
        });
        try
        {
            var installer = await _updateService.DownloadInstallerAsync(release, progress, _lifetime?.Token ?? CancellationToken.None);
            _updateProgress = 100;
            UpdateStatusText = $"Установщик v{release.Version} загружен. Запустить его?";
            InstallerDownloaded?.Invoke(installer, release.ReleaseNotes);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { UpdateStatusText = $"Ошибка загрузки обновления: {ex.Message}"; }
        finally
        {
            _isDownloadingUpdate = false;
            OnPropertyChanged(nameof(IsDownloadingUpdate));
            OnPropertyChanged(nameof(UpdateButtonText));
            UpdateCommand.NotifyCanExecuteChanged();
        }
    }

    public async Task PrepareForInstallerLaunchAsync()
    {
        await SaveSessionStateAsync();
        if (_minerControl.IsRunning)
        {
            try
            {
                await _minerControl.StopAsync(forceKillOnTimeout: true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Не удалось штатно остановить майнер перед обновлением: {ex.Message}");
            }
        }
        WindowsDiagnosticsService.SetMiningSleepPrevention(false);
    }

    private async Task RestartMinerAsync()
    {
        if (!TryBeginMinerAction()) return;
        MinerControlStatus = "Перезапуск OneZeroMiner…";
        try
        {
            var token = _lifetime?.Token ?? CancellationToken.None;
            var wallet = WalletAddressDraft.Trim().ToLowerInvariant();
            var gpuCount = GetTargetNvidiaGpuCount();
            await _minerControl.RestartAsync(wallet, gpuCount, token);
            _watchdog.MarkMinerStarted(DateTimeOffset.UtcNow);
            MinerControlStatus = "Майнер перезапущен";
            AppendWatchdogEvent("Выполнен мягкий перезапуск OneZeroMiner.");
            _ = RefreshAsync(force: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MinerControlStatus = $"Ошибка перезапуска: {ex.Message}";
            AppendWatchdogEvent($"Ошибка перезапуска майнера: {ex.Message}");
        }
        finally
        {
            EndMinerAction();
        }
    }

    private async Task StartMinerAsync(bool manual = false)
    {
        if (!TryBeginMinerAction()) return;
        if (manual)
        {
            _userManuallyStopped = false;
            _watchdog.ResetMinerTimers(clearThermalPause: true);
        }
        MinerControlStatus = "Запуск OneZeroMiner…";
        try
        {
            var token = _lifetime?.Token ?? CancellationToken.None;
            if (!_gpuDiscoveryCompleted)
            {
                var snapshot = await _telemetry.ReadAsync(token);
                Apply(snapshot);
            }

            var detectedGpus = Gpus.ToArray();
            var nvidiaGpus = detectedGpus.Where(IsNvidiaGpu).ToArray();
            if (detectedGpus.Length > 0 && nvidiaGpus.Length == 0)
            {
                var names = string.Join(", ", detectedGpus.Select(gpu => gpu.Name));
                throw new InvalidOperationException($"Обнаружены {names}. PearlHash в OneZeroMiner поддерживает только видеокарты NVIDIA.");
            }

            var wallet = WalletAddressDraft.Trim().ToLowerInvariant();
            if (!PoolEarningsClient.IsValidWallet(wallet))
                throw new InvalidOperationException("Сначала введите и сохраните корректный адрес PRL (prl1p... или prl1q...).");
            if (!string.Equals(wallet, _config.WalletAddress, StringComparison.OrdinalIgnoreCase))
            {
                await RigConfig.SaveWalletAsync(wallet, token);
                _walletAddress = wallet;
                WalletStatus = "Публичный адрес сохранён только в локальном профиле Windows";
            }

            var gpuCount = nvidiaGpus.Length > 0 ? nvidiaGpus.Length : 2;
            if (CanSaveMinerSettingsCore())
            {
                _ = TryParseTariff(ElectricityRateDraft, out var parsedDayTariff);
                _ = TryParseTariff(NightElectricityRateDraft, out var parsedNightTariff);
                _config = _config with
                {
                    WorkerName = WorkerNameDraft.Trim(),
                    CoreClock = NormalizeGpuSetting(CoreClockDraft, gpuCount),
                    MemoryClock = NormalizeGpuSetting(MemoryClockDraft, gpuCount),
                    PowerLimit = NormalizeGpuSetting(PowerLimitDraft, gpuCount),
                    Kernel = NormalizeGpuSetting(KernelDraft, gpuCount),
                    Fan = NormalizeGpuSetting(FanDraft, gpuCount),
                    ActivePreset = ActivePreset,
                    ElectricityRateRubPerKwh = parsedDayTariff,
                    NightElectricityRateRubPerKwh = parsedNightTariff
                };
                _minerControl.UpdateConfig(_config);
            }

            if (!string.IsNullOrWhiteSpace(_config.PowerLimit))
            {
                try
                {
                    var applied = await _nvidiaSmi.ApplyPowerLimitsAsync(_config.PowerLimit, token);
                    AppendWatchdogEvent($"Лимит мощности перед запуском майнера: {applied}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppendWatchdogEvent($"Предупреждение Power Limit: {ex.Message}");
                }
            }

            await _minerControl.StartAsync(wallet, gpuCount, token);
            WindowsDiagnosticsService.SetMiningSleepPrevention(_config.PreventSleepWhenMining);
            _config = _config with { LastKnownMiningActive = true };
            await RigConfig.SaveLocalSettingsAsync(_config, token);
            _watchdog.MarkMinerStarted(DateTimeOffset.UtcNow);
            var unsupported = detectedGpus.Where(gpu => !IsNvidiaGpu(gpu)).Select(gpu => gpu.Name).ToArray();
            MinerControlStatus = unsupported.Length == 0
                ? "Майнер запущен (блокировка сна Windows активна)"
                : $"Майнер запущен на NVIDIA; AMD не используется: {string.Join(", ", unsupported)}";
            AppendWatchdogEvent(manual ? "Майнер запущен пользователем." : "Майнер автоматически запущен системой защиты.");
            _ = RefreshAsync(force: true);
        }
        catch (OperationCanceledException) { MinerControlStatus = "Запуск отменён"; }
        catch (Exception ex)
        {
            MinerControlStatus = $"Не запущен: {ex.Message}";
            AppendWatchdogEvent($"Ошибка запуска майнера: {ex.Message}");
        }
        finally { EndMinerAction(); }
    }

    private static bool IsNvidiaGpu(GpuLiveMetrics gpu) =>
        gpu.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
        || gpu.Name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
        || gpu.Name.Contains("Quadro", StringComparison.OrdinalIgnoreCase)
        || gpu.Name.Contains("Tesla", StringComparison.OrdinalIgnoreCase);

    private int GetTargetNvidiaGpuCount()
    {
        var nvidiaCount = Gpus.Count(IsNvidiaGpu);
        if (nvidiaCount > 0) return nvidiaCount;
        return Gpus.Count > 0 ? Gpus.Count : 2;
    }

    private void UpdateCommandLinePreviewFromDrafts()
    {
        var count = GetTargetNvidiaGpuCount();
        var previewConfig = _config with
        {
            WorkerName = string.IsNullOrWhiteSpace(_workerNameDraft) ? _config.WorkerName : _workerNameDraft.Trim(),
            PrimaryPool = string.IsNullOrWhiteSpace(_primaryPoolDraft) ? _config.PrimaryPool : _primaryPoolDraft.Trim(),
            BackupPool = string.IsNullOrWhiteSpace(_backupPoolDraft) ? _config.BackupPool : _backupPoolDraft.Trim(),
            CoreClock = NormalizeGpuSetting(_coreClockDraft, count),
            MemoryClock = NormalizeGpuSetting(_memoryClockDraft, count),
            PowerLimit = NormalizeGpuSetting(_powerLimitDraft, count),
            Kernel = NormalizeGpuSetting(_kernelDraft, count),
            Fan = NormalizeGpuSetting(_fanDraft, count)
        };
        _minerControl.UpdateConfig(previewConfig);
        MinerCommandLinePreview = _minerControl.GetCommandLinePreview(_walletAddressDraft, count);
    }

    private async Task StopMinerAsync(bool manual = false, bool forceKillOnTimeout = false)
    {
        if (!TryBeginMinerAction()) return;
        if (manual)
        {
            _userManuallyStopped = true;
            _watchdog.ResetMinerTimers(clearThermalPause: true);
        }
        MinerControlStatus = "Остановка: отправка штатного Ctrl+C…";
        try
        {
            await _minerControl.StopAsync(forceKillOnTimeout, _lifetime?.Token ?? CancellationToken.None);
            WindowsDiagnosticsService.SetMiningSleepPrevention(false);
            if (manual)
            {
                _config = _config with { LastKnownMiningActive = false };
                await RigConfig.SaveLocalSettingsAsync(_config, _lifetime?.Token ?? CancellationToken.None);
            }
            _watchdog.ResetMinerTimers();
            MinerControlStatus = "Майнер остановлен штатно";
            if (manual) AppendWatchdogEvent("Майнер остановлен пользователем.");
            _ = RefreshAsync(force: true);
        }
        catch (OperationCanceledException) { MinerControlStatus = "Остановка отменена"; }
        catch (Exception ex) { MinerControlStatus = $"Остановка не подтверждена: {ex.Message}"; }
        finally { EndMinerAction(); }
    }

    private async Task ToggleMinerModeAsync()
    {
        if (!TryBeginMinerAction()) return;
        var enabled = !AutoStartMining;
        MinerControlStatus = enabled ? "Регистрация задачи в Планировщике Windows (с правами администратора)…" : "Удаление задачи автозапуска из Планировщика Windows…";
        try
        {
            await _minerControl.SetAutoStartAsync(enabled, _lifetime?.Token ?? CancellationToken.None);
            _config = _config with { AutoStartMining = enabled };
            await RigConfig.SaveLocalSettingsAsync(_config, _lifetime?.Token ?? CancellationToken.None);
            AutoStartMining = enabled;
            MinerControlStatus = enabled
                ? "Автозапуск с Windows включён (Планировщик задач /RL HIGHEST + автовозврат майнинга)"
                : "Ручной режим: автозапуск с Windows отключён";
            AppendWatchdogEvent(MinerControlStatus);
        }
        catch (OperationCanceledException) { MinerControlStatus = "Изменение режима отменено"; }
        catch (Exception ex) { MinerControlStatus = $"Режим не изменён: {ex.Message}"; }
        finally { EndMinerAction(); }
    }

    private bool CanSaveMinerSettings() => !_isMinerBusy && !_isAutoTuning && CanSaveMinerSettingsCore();

    private bool CanSaveMinerSettingsCore() =>
        System.Text.RegularExpressions.Regex.IsMatch((WorkerNameDraft ?? string.Empty).Trim(), "^[A-Za-z0-9._-]{1,32}$")
        && IsGpuListValid(CoreClockDraft, 0, 5000)
        && IsGpuListLengthValid(CoreClockDraft)
        && IsGpuListValid(MemoryClockDraft, 0, 30000)
        && IsGpuListLengthValid(MemoryClockDraft)
        && IsGpuListValid(PowerLimitDraft, 1, 1000)
        && IsGpuListLengthValid(PowerLimitDraft)
        && IsGpuListValid(KernelDraft, 0, 16)
        && IsGpuListLengthValid(KernelDraft)
        && IsGpuListValid(FanDraft, 0, 100, allowAuto: true)
        && IsGpuListLengthValid(FanDraft)
        && TryParseTariff(ElectricityRateDraft, out _)
        && TryParseTariff(NightElectricityRateDraft, out _);

    private static bool TryParseTariff(string text, out double rate)
    {
        var normalized = (text ?? string.Empty).Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out rate) && rate >= 0 && rate <= 100;
    }

    private bool IsGpuListLengthValid(string text)
    {
        if (IsAutoSetting(text)) return true;
        var count = GetTargetNvidiaGpuCount();
        var len = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        return len == 1 || len == count || len == Gpus.Count;
    }

    private static bool IsGpuListValid(string text, int min, int max, bool allowAuto = false)
    {
        if (IsAutoSetting(text)) return true;
        var values = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return values.Length is > 0 and <= 16 && values.All(value =>
            (allowAuto && value == "*") ||
            (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number >= min && number <= max));
    }

    private async Task SaveMinerSettingsAsync()
    {
        if (!CanSaveMinerSettings() || !TryBeginMinerAction()) return;
        var needMinerRestart = false;
        try
        {
            var token = _lifetime?.Token ?? CancellationToken.None;
            var count = GetTargetNvidiaGpuCount();
            _ = TryParseTariff(ElectricityRateDraft, out var parsedDayTariff);
            _ = TryParseTariff(NightElectricityRateDraft, out var parsedNightTariff);
            var normalizedPowerLimit = NormalizeGpuSetting(PowerLimitDraft, count);
            var previousConfig = _config;
            _config = _config with
            {
                WorkerName = WorkerNameDraft.Trim(),
                CoreClock = NormalizeGpuSetting(CoreClockDraft, count),
                MemoryClock = NormalizeGpuSetting(MemoryClockDraft, count),
                PowerLimit = normalizedPowerLimit,
                Kernel = NormalizeGpuSetting(KernelDraft, count),
                Fan = NormalizeGpuSetting(FanDraft, count),
                ActivePreset = ActivePreset,
                AutoRestartMinerOnOverclockChange = AutoRestartMinerOnOverclockChange,
                ElectricityRateRubPerKwh = parsedDayTariff,
                NightElectricityRateRubPerKwh = parsedNightTariff
            };
            await RigConfig.SaveLocalSettingsAsync(_config, token);
            _minerControl.UpdateConfig(_config);
            MinerCommandLinePreview = _minerControl.GetCommandLinePreview(_walletAddressDraft, count);

            needMinerRestart = _minerControl.IsRunning && MinerControlService.RequiresMinerRestartForSettings(previousConfig, _config);

            try
            {
                var targetForDriver = string.IsNullOrWhiteSpace(normalizedPowerLimit) ? "auto" : normalizedPowerLimit;
                var applied = await _nvidiaSmi.ApplyPowerLimitsAsync(targetForDriver, token);
                MinerControlStatus = $"Лимит мощности применён: {applied}. Тариф Т1 {parsedDayTariff:0.00} / Т2 {parsedNightTariff:0.00} ₽/кВт·ч сохранён.";
                AppendWatchdogEvent($"Применён Power Limit NVIDIA: {applied}");
            }
            catch (OperationCanceledException)
            {
                MinerControlStatus = "Профиль сохранён; применение мощности отменено в запросе Windows.";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                MinerControlStatus = $"Профиль сохранён, но лимит мощности не применён: {ex.Message}";
                AppendWatchdogEvent($"Ошибка применения Power Limit: {ex.Message}");
            }

            if (needMinerRestart)
            {
                var activeWallet = string.IsNullOrWhiteSpace(_walletAddress)
                    ? WalletAddressDraft.Trim().ToLowerInvariant()
                    : _walletAddress;
                if (_config.AutoRestartMinerOnOverclockChange && PoolEarningsClient.IsValidWallet(activeWallet))
                {
                    AppendWatchdogEvent("Изменены частоты/kernel/fan: автоматический мягкий перезапуск OneZeroMiner.");
                    await _minerControl.RestartAsync(activeWallet, count, token);
                    _watchdog.MarkMinerStarted(DateTimeOffset.UtcNow);
                    MinerControlStatus = "Профиль применён и OneZeroMiner перезапущен с новыми частотами.";
                }
                else
                {
                    MinerControlStatus += " (для применения новых частот/fan перезапустите майнер)";
                }
            }

            _ = RefreshAsync(force: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MinerControlStatus = $"Не удалось сохранить настройки: {ex.Message}";
        }
        finally { EndMinerAction(); }
    }

    private bool TryBeginMinerAction()
    {
        if (_isMinerBusy || _disposed) return false;
        _isMinerBusy = true;
        UpdateMinerState();
        return true;
    }

    private void EndMinerAction()
    {
        _isMinerBusy = false;
        UpdateMinerState();
    }

    private void UpdateMinerState()
    {
        OnPropertyChanged(nameof(IsMinerRunning));
        StartMinerCommand.NotifyCanExecuteChanged();
        StopMinerCommand.NotifyCanExecuteChanged();
        RestartMinerNowCommand.NotifyCanExecuteChanged();
        ToggleMinerModeCommand.NotifyCanExecuteChanged();
        ToggleWatchdogCommand.NotifyCanExecuteChanged();
        ToggleSmartFanCurveCommand.NotifyCanExecuteChanged();
        ToggleDualTariffCommand.NotifyCanExecuteChanged();
        ToggleNightScheduleCommand.NotifyCanExecuteChanged();
        RunAutoTuneBenchmarkCommand.NotifyCanExecuteChanged();
        PresetAutoCommand.NotifyCanExecuteChanged();
        PresetEcoCommand.NotifyCanExecuteChanged();
        PresetBalanceCommand.NotifyCanExecuteChanged();
        PresetTurboCommand.NotifyCanExecuteChanged();
        PresetSilentCommand.NotifyCanExecuteChanged();
        ApplyPowerLimitNowCommand.NotifyCanExecuteChanged();
        SaveMinerSettingsCommand.NotifyCanExecuteChanged();
        SaveFanCurveCommand.NotifyCanExecuteChanged();
        SaveWatchdogAndScheduleCommand.NotifyCanExecuteChanged();
    }

    private async Task RefreshAsync(bool force = false)
    {
        if (_disposed) return;
        if (IsRefreshing)
        {
            if (force) _manualRefreshQueued = true;
            return;
        }
        IsRefreshing = true;
        try
        {
            var token = _lifetime?.Token ?? CancellationToken.None;
            if (HasToast && DateTimeOffset.UtcNow >= _toastExpiresAt)
                DismissToast();

            var snapshot = await _telemetry.ReadAsync(token);
            Apply(snapshot);
            ApplySmartFanCurveIfEnabled(snapshot.Gpus);
            MinerLogTail = _minerControl.ReadRecentLogLines(18);
            AppLogTail = ReadRecentAppLogLines(16);
            await EvaluateDayNightScheduleAsync();
            await EvaluateWatchdogAndThermalProtectionAsync(snapshot);
            await PollTelegramRemoteCommandsAsync();

            if (force || DateTimeOffset.UtcNow - _lastPoolPingAt >= TimeSpan.FromSeconds(15))
            {
                _lastPoolPingAt = DateTimeOffset.UtcNow;
                var ping = await _poolPingService.MeasureAsync(_config.PrimaryPool, _config.BackupPool, token);
                PoolPingText = ping.SummaryText;
                var poolSwitch = _watchdog.EvaluatePoolSwitch(ping, _minerControl.UseBackupPool, _config);
                if (poolSwitch == true)
                {
                    _minerControl.UseBackupPool = true;
                    OnPropertyChanged(nameof(ActivePoolBadgeText));
                    UpdateCommandLinePreviewFromDrafts();
                    var msg = $"Сетевой монитор: основной пул не отвечает ({_config.PoolDropSwitchThresholdCount} проверок), переключение на резервный пул EU ({ping.BackupPingMs} мс).";
                    AppendWatchdogEvent(msg);
                    ShowToast(msg, "Warning", 7);
                    await SendTelegramAlertIfEnabledAsync($"🌐 {msg}");
                    if (_minerControl.IsRunning && !_isMinerBusy && !_isAutoTuning && PoolEarningsClient.IsValidWallet(_walletAddress))
                        await RestartMinerAsync();
                }
                else if (poolSwitch == false)
                {
                    _minerControl.UseBackupPool = false;
                    OnPropertyChanged(nameof(ActivePoolBadgeText));
                    UpdateCommandLinePreviewFromDrafts();
                    var msg = $"Сетевой монитор: основной пул RU снова доступен ({ping.PrimaryPingMs} мс).";
                    AppendWatchdogEvent(msg);
                    ShowToast(msg, "Success");
                    if (_minerControl.IsRunning && !_isMinerBusy && !_isAutoTuning && PoolEarningsClient.IsValidWallet(_walletAddress))
                        await RestartMinerAsync();
                }
            }

            if (PoolEarningsClient.IsValidWallet(_walletAddress) && (force || DateTimeOffset.UtcNow - _lastEarningsRead >= TimeSpan.FromSeconds(_earningsRefreshSeconds)))
            {
                var earnings = await _earningsClient.ReadAsync(_walletAddress, token);
                await ApplyEarningsAsync(earnings);
                _lastEarningsRead = DateTimeOffset.UtcNow;
            }
            else if (!PoolEarningsClient.IsValidWallet(_walletAddress))
            {
                PoolBalanceStatus = "Адрес выплат не настроен";
                PoolBalance = "Нет данных";
                PoolConfirmed = "Нет данных";
                PoolUnconfirmed = "Нет данных";
                PoolPaid = "Нет данных";
                PoolThresholdText = "Порог: —";
                PayoutProgressPercent = 0;
                PayoutProgressText = "До выплаты: введите адрес PRL";
                PoolRubEstimate = "Нет данных";
                MarketRate = "Курс PRL/RUB недоступен";
            }

            if (_minerControl.IsRunning && DateTimeOffset.UtcNow - _lastCsvSnapshotAt >= TimeSpan.FromMinutes(10))
                await RecordCsvSnapshotAsync("Периодический срез телеметрии");

            if (DateTimeOffset.UtcNow - _lastSessionSaveAt >= TimeSpan.FromMinutes(1))
                await SaveSessionStateAsync();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            ConnectionStatus = "Ошибка обновления";
            DetailStatus = $"Не удалось обновить телеметрию: {ex.Message}";
            LastUpdated = "Последний опрос завершился ошибкой";
            AppLogger.Error("Ошибка цикла RefreshAsync", ex);
        }
        finally
        {
            IsRefreshing = false;
            if (_manualRefreshQueued && !_disposed)
            {
                _manualRefreshQueued = false;
                await RefreshAsync();
            }
        }
    }

    private void ApplySmartFanCurveIfEnabled(IReadOnlyList<GpuLiveMetrics> gpus)
    {
        if (!SmartFanCurveEnabled || gpus.Count == 0) return;
        var targets = _smartFanController.EvaluateTargets(gpus, _config);
        if (targets.Count > 0)
            _ = _nvidiaSmi.TryApplySmartFansDirect(targets);
    }

    private async Task EvaluateWatchdogAndThermalProtectionAsync(TelemetrySnapshot snapshot)
    {
        var decision = _watchdog.Evaluate(
            snapshot,
            _minerControl.IsRunning,
            WatchdogEnabled,
            _isMinerBusy,
            _isAutoTuning,
            _userManuallyStopped,
            _config,
            DateTimeOffset.UtcNow);

        if (!string.IsNullOrEmpty(decision.StatusText))
            WatchdogStatusText = decision.StatusText;
        if (!string.IsNullOrEmpty(decision.EventLogMessage))
            AppendWatchdogEvent(decision.EventLogMessage);

        switch (decision.Action)
        {
            case WatchdogActionType.ThermalEmergencyStop:
                await RecordCsvSnapshotAsync(decision.EventLogMessage ?? "Аварийный термостоп");
                if (!string.IsNullOrEmpty(decision.TelegramAlertMessage))
                    await SendTelegramAlertIfEnabledAsync(decision.TelegramAlertMessage);
                await StopMinerAsync(manual: false, forceKillOnTimeout: true);
                break;

            case WatchdogActionType.ThermalResume:
                await RecordCsvSnapshotAsync(decision.EventLogMessage ?? "Возобновление после остывания");
                if (!string.IsNullOrEmpty(decision.TelegramAlertMessage))
                    await SendTelegramAlertIfEnabledAsync(decision.TelegramAlertMessage);
                await StartMinerAsync(manual: false);
                break;

            case WatchdogActionType.ZeroHashrateRestart:
                var tdrEvent = await WindowsDiagnosticsService.CheckRecentNvidiaDriverResetAsync(_lifetime?.Token ?? CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(tdrEvent))
                    AppendWatchdogEvent($"Диагностика Windows (nvlddmkm/4101): {tdrEvent}");
                await RecordCsvSnapshotAsync(decision.EventLogMessage ?? "Watchdog рестарт 0 TH/s");
                if (!string.IsNullOrEmpty(decision.TelegramAlertMessage))
                    await SendTelegramAlertIfEnabledAsync($"{decision.TelegramAlertMessage}{(string.IsNullOrWhiteSpace(tdrEvent) ? string.Empty : $"\nСобытие драйвера: {tdrEvent}")}");
                await StopMinerAsync(manual: false, forceKillOnTimeout: true);
                await Task.Delay(1500, _lifetime?.Token ?? CancellationToken.None);
                await StartMinerAsync(manual: false);
                break;
        }
    }

    private void Apply(TelemetrySnapshot snapshot)
    {
        _gpuDiscoveryCompleted = true;
        MinerApiStatus = snapshot.MinerApiMessage;
        HardwareStatus = snapshot.NvidiaMessage;
        PoolStatus = snapshot.PoolConnected switch { true => "Подключён", false => "Нет подключения", null => "Нет данных" };
        PoolConnectedState = snapshot.PoolConnected switch { true => "Connected", false => "Disconnected", null => "Unknown" };
        PoolUrl = snapshot.PoolUrl ?? "Адрес пула недоступен";
        TotalHashrate = snapshot.TotalHashrateTHs is double rate ? $"{rate:0.00} TH/s" : "Нет данных";
        _lastTotalHashrateTHs = snapshot.TotalHashrateTHs;

        if (snapshot.TotalHashrateTHs is double currentRate && double.IsFinite(currentRate))
        {
            AppendSample(_totalHashrateBuffer, currentRate);
            TotalHashrateHistory = _totalHashrateBuffer.ToArray();
        }

        var totalPowerW = snapshot.Gpus.Where(g => g.PowerWatts is not null).Sum(g => g.PowerWatts!.Value);
        var hasPower = snapshot.Gpus.Any(g => g.PowerWatts is not null);
        _lastTotalPowerWatts = hasPower ? totalPowerW : null;
        if (snapshot.TotalHashrateTHs is double totalThs && totalThs > 0 && hasPower && totalPowerW > 0)
        {
            var ghPerWatt = (totalThs * 1000d) / totalPowerW;
            _lastEfficiencyGhPerWatt = ghPerWatt;
            TotalEfficiency = $"Эффективность: {ghPerWatt:0.00} GH/W · Потребление: {totalPowerW:0} Вт";
        }
        else if (hasPower)
        {
            _lastEfficiencyGhPerWatt = null;
            TotalEfficiency = $"Эффективность: — GH/W · Потребление: {totalPowerW:0} Вт";
        }
        else
        {
            _lastEfficiencyGhPerWatt = null;
            TotalEfficiency = "Энергоэффективность: — GH/W · Потребление: — Вт";
        }

        UpdateElectricityAndNetProfit(snapshot.SampledAt, snapshot.Gpus, hasPower ? totalPowerW : null);

        AcceptedShares = snapshot.AcceptedShares?.ToString("N0") ?? "Нет данных";
        if (snapshot.RejectedShares is long rej && snapshot.AcceptedShares is long acc && acc + rej > 0)
        {
            var pct = rej * 100d / (acc + rej);
            RejectedShares = $"{rej:N0} ({pct:0.##}%)";
        }
        else
        {
            RejectedShares = snapshot.RejectedShares?.ToString("N0") ?? "Нет данных";
        }

        var uptimePart = snapshot.UptimeSeconds is long up ? FormatUptime(up) : "—";
        var latencyPart = snapshot.ApiLatencyMs is int ms ? $"{ms} мс" : "—";
        MinerUptimeText = $"Аптайм: {uptimePart} · API: {latencyPart}";
        LastUpdated = $"Опрос: {snapshot.SampledAt:HH:mm:ss}";

        var hardwareOrMinerAvailable = snapshot.MinerApiAvailable || snapshot.NvidiaAvailable;
        ConnectionStatus = snapshot.MinerApiAvailable && snapshot.NvidiaAvailable
            ? "Все источники подключены"
            : hardwareOrMinerAvailable ? "Часть источников недоступна" : "Нет связи с источниками";
        ConnectionHealthLevel = _watchdog.ThermalPaused
            ? "Warning"
            : snapshot.MinerApiAvailable && snapshot.NvidiaAvailable ? "Connected" : hardwareOrMinerAvailable ? "Partial" : "Disconnected";

        DetailStatus = string.Join("  ·  ", new[]
        {
            snapshot.MinerApiMessage,
            snapshot.NvidiaMessage,
            snapshot.Gpus.Count == 0 ? "Видеокарты не обнаружены" : $"Обнаружено GPU: {snapshot.Gpus.Count}"
        });

        var running = _minerControl.IsRunning;
        WindowsDiagnosticsService.SetMiningSleepPrevention(running && _config.PreventSleepWhenMining);
        if (!_isMinerBusy && !_isAutoTuning)
            MinerControlStatus = _watchdog.ThermalPaused
                ? $"Термо-пауза: ожидание остывания ≤ {_config.ResumeTempC} °C"
                : running ? "Майнер запущен (защита от сна активна)" : "Майнер остановлен";
        UpdateMinerState();

        var ordered = snapshot.Gpus.OrderBy(gpu => gpu.Id).Select(gpu =>
        {
            if (!_gpuHashrateBuffers.TryGetValue(gpu.Id, out var hrBuf))
            {
                hrBuf = [];
                _gpuHashrateBuffers[gpu.Id] = hrBuf;
            }
            if (gpu.HashrateTHs is double hr && double.IsFinite(hr))
                AppendSample(hrBuf, hr);

            if (!_gpuTempBuffers.TryGetValue(gpu.Id, out var tBuf))
            {
                tBuf = [];
                _gpuTempBuffers[gpu.Id] = tBuf;
            }
            if (gpu.TempC is double tc && double.IsFinite(tc))
                AppendSample(tBuf, tc);

            return gpu with
            {
                HashrateHistory = hrBuf.ToArray(),
                TemperatureHistory = tBuf.ToArray()
            };
        }).ToArray();

        for (var index = Gpus.Count - 1; index >= ordered.Length; index--) Gpus.RemoveAt(index);
        for (var index = 0; index < ordered.Length; index++)
        {
            if (index < Gpus.Count) Gpus[index] = ordered[index];
            else Gpus.Add(ordered[index]);
        }

        var nvidiaOrdered = ordered.Where(IsNvidiaGpu).ToArray();
        Gpu0EffectiveTemp = nvidiaOrdered.Length > 0 ? SmartFanController.ComputeEffectiveTemperature(nvidiaOrdered[0], _config.VramTempOffsetC) : null;
        Gpu1EffectiveTemp = nvidiaOrdered.Length > 1 ? SmartFanController.ComputeEffectiveTemperature(nvidiaOrdered[1], _config.VramTempOffsetC) : null;
        if (Gpu0EffectiveTemp <= 0) Gpu0EffectiveTemp = null;
        if (Gpu1EffectiveTemp <= 0) Gpu1EffectiveTemp = null;
        SyncOverclockRowsFromDrafts();
        UpdateDiagnosticFlagsCore(tdrEvent: null);

        var powerCards = ordered.Where(gpu => gpu.MinPowerLimitWatts is not null && gpu.MaxPowerLimitWatts is not null).ToArray();
        if (powerCards.Length > 0)
        {
            var minimum = Math.Ceiling(powerCards.Max(gpu => gpu.MinPowerLimitWatts!.Value));
            var maximum = Math.Floor(powerCards.Min(gpu => gpu.MaxPowerLimitWatts!.Value));
            if (minimum > maximum)
            {
                minimum = Math.Floor(powerCards.Min(gpu => gpu.MinPowerLimitWatts!.Value));
                maximum = Math.Ceiling(powerCards.Max(gpu => gpu.MaxPowerLimitWatts!.Value));
            }
            if (minimum <= maximum)
            {
                PowerSliderMinimum = minimum;
                PowerSliderMaximum = maximum;
                CanAdjustPowerLimit = true;
                var currentCap = powerCards[0].EnforcedPowerLimitWatts ?? powerCards[0].PowerLimitWatts;
                PowerRangeHint = $"Диапазон драйвера NVIDIA ({powerCards.Length} GPU): {minimum:0}–{maximum:0} Вт (активный лимит: {(currentCap is double c ? $"{c:0} Вт" : "—")}). Отпустите ползунок или нажмите «ПРИМЕНИТЬ ЛИМИТ».";
                if (!_powerSliderInitialized)
                {
                    var initial = TryExtractFirstWatts(PowerLimitDraft, out var parsed)
                        ? parsed
                        : currentCap ?? powerCards[0].DefaultPowerLimitWatts ?? 150d;
                    _powerSliderInitialized = true;
                    Set(ref _powerSliderValue, Math.Round(Math.Clamp(initial, minimum, maximum)), nameof(PowerSliderValue));
                }
                else
                {
                    Set(ref _powerSliderValue, Math.Round(Math.Clamp(PowerSliderValue, minimum, maximum)), nameof(PowerSliderValue));
                }
            }
        }
        else
        {
            CanAdjustPowerLimit = true;
            PowerRangeHint = "Передвиньте ползунок или введите лимит мощности в ваттах и нажмите «ПРИМЕНИТЬ ЛИМИТ».";
        }

        OnPropertyChanged(nameof(HasNoGpus));
        OnPropertyChanged(nameof(GpuCountLabel));
        OnPropertyChanged(nameof(MaxGpuHashrateTHs));
        SaveMinerSettingsCommand.NotifyCanExecuteChanged();

        var maxTemp = ordered.Where(g => g.TempC is not null).Select(g => g.TempC!.Value).DefaultIfEmpty(0).Max();
        var trayState = !running
            ? (_watchdog.ThermalPaused ? TrayHealthState.Critical : TrayHealthState.Stopped)
            : (snapshot.TotalHashrateTHs is null or <= 0.01 ? TrayHealthState.Critical : maxTemp >= _config.WarningTempC ? TrayHealthState.Warning : TrayHealthState.MiningHealthy);

        TrayStatusChanged?.Invoke($"Pearl Rig · {TotalHashrate} · Макс {maxTemp:0}°C · {(running ? "Майнит" : "Остановлен")}", trayState);
    }

    private void UpdateElectricityAndNetProfit(DateTimeOffset sampledAt, IReadOnlyList<GpuLiveMetrics> gpus, double? totalPowerWatts)
    {
        var dayTariff = TryParseTariff(ElectricityRateDraft, out var parsedDay) ? parsedDay : _config.ElectricityRateRubPerKwh;
        var nightTariff = TryParseTariff(NightElectricityRateDraft, out var parsedNight) ? parsedNight : _config.NightElectricityRateRubPerKwh;
        var snap = _profitability.UpdateEnergyAndCost(sampledAt, gpus, totalPowerWatts, _config, dayTariff, nightTariff);
        var counterTag = snap.UsedHardwareCounter ? "NVML" : "расчёт";

        if (snap.DailyElectricityCostRub is double dailyCostRub)
        {
            ElectricitySummaryText = $"Сессия ({counterTag}): {snap.SessionEnergyKwh:0.000} кВт·ч ({snap.SessionElectricityCostRub:0.00} ₽) · Сейчас: {snap.ZoneLabel} · Розетка: {dailyCostRub:0.00} ₽/сут";
            DailyElectricityKpiText = $"{dailyCostRub:0.00} ₽/сут · {snap.ZoneLabel}";
            if (_profitability.LastObservedGrossRubPerDay is double grossRubPerDay && _profitability.LastObservedNetRubPerDay is double netRubPerDay)
            {
                NetProfitSummaryText = $"Чистая прибыль: {netRubPerDay:0.00} ₽/сут (начисления {grossRubPerDay:0.00} ₽ − свет {dailyCostRub:0.00} ₽)";
                DailyNetProfitKpiText = $"{(netRubPerDay >= 0 ? "+" : string.Empty)}{netRubPerDay:0.00} ₽/сут";
            }
            else
            {
                NetProfitSummaryText = $"Расход на свет: {dailyCostRub:0.00} ₽/сут (чистая прибыль появится после расчёта темпа начислений PRL)";
                DailyNetProfitKpiText = "Ожидание темпа PRL";
            }
        }
        else
        {
            ElectricitySummaryText = $"Сессия: {snap.SessionEnergyKwh:0.000} кВт·ч ({snap.SessionElectricityCostRub:0.00} ₽) · Активный тариф: {snap.ZoneLabel}";
            DailyElectricityKpiText = $"— ₽/сут · {snap.ZoneLabel}";
            DailyNetProfitKpiText = "Нет данных Вт";
        }
        RefreshSchedulePreview();
    }

    private async Task ApplyEarningsAsync(PoolEarningsSnapshot earnings)
    {
        PoolBalanceStatus = earnings.Message;
        PoolBalance = FormatPrl(earnings.TotalPrl);
        PoolConfirmed = FormatPrl(earnings.ConfirmedPrl);
        PoolUnconfirmed = FormatPrl(earnings.UnconfirmedPrl);
        PoolPaid = FormatPrl(earnings.PaidPrl);
        _lastKnownTotalPrl = earnings.TotalPrl;
        _lastKnownPrlRubRate = earnings.PrlRub;
        PoolThresholdText = earnings.PayoutThresholdPrl is double threshold && threshold > 0
            ? $"Порог выплаты: {threshold:0.####} PRL"
            : "Порог выплаты: —";

        if (earnings.PaidPrl is double currentPaid)
        {
            if (_lastKnownPaidPrl is double previousPaid && currentPaid > previousPaid + 0.000001)
            {
                var deltaPaid = currentPaid - previousPaid;
                var payoutMsg = $"Получена выплата с пула Kryptex: +{deltaPaid:0.########} PRL (всего выплачено: {currentPaid:0.########} PRL)";
                AppendWatchdogEvent(payoutMsg);
                ShowToast(payoutMsg, "Success", 8);
                await RecordCsvSnapshotAsync(payoutMsg);
                await SendTelegramAlertIfEnabledAsync($"💰 {payoutMsg}");
            }
            _lastKnownPaidPrl = currentPaid;
        }

        if (earnings.TotalPrl is double currentTotal && earnings.PayoutThresholdPrl is double targetThreshold && targetThreshold > 0)
        {
            var pct = Math.Clamp(currentTotal * 100d / targetThreshold, 0d, 100d);
            PayoutProgressPercent = pct;
            PayoutProgressText = $"Накоплено к выплате: {pct:0.1}% ({currentTotal:0.####} из {targetThreshold:0.####} PRL)";
        }
        else
        {
            PayoutProgressPercent = 0;
            PayoutProgressText = "Прогресс выплаты: ожидание порога от Kryptex API";
        }

        var accumulation = _profitability.UpdateAccumulation(earnings);
        SessionAccumulationRate = accumulation.SummaryText;

        var etaDays = _profitability.EstimateDaysToPayout(earnings.TotalPrl, earnings.PayoutThresholdPrl);
        PayoutEtaText = etaDays switch
        {
            0d => "Порог достигнут!",
            double d when d < 1d => $"~{d * 24d:0.1} ч",
            double d => $"~{d:0.1} дн.",
            _ => "Ожидание темпа"
        };

        PoolRubEstimate = earnings.TotalPrl is double total && earnings.PrlRub is double price ? $"≈ {total * price:0.00} ₽" : "Нет данных";
        MarketRate = earnings.PrlUsd is double usd && earnings.UsdRub is double exchangeRate
            ? $"PRL/USD {usd:0.######} · USD/RUB {exchangeRate:0.####}"
            : "Официальный курс недоступен";
        PoolUpdated = $"Баланс: {earnings.FetchedAt:HH:mm:ss}";
    }

    private async Task SaveWalletAsync()
    {
        var wallet = WalletAddressDraft.Trim().ToLowerInvariant();
        if (!PoolEarningsClient.IsValidWallet(wallet))
        {
            WalletStatus = "Адрес PRL должен начинаться с prl1p... (63 симв.) или prl1q... (42–44 симв.)";
            return;
        }

        try
        {
            await RigConfig.SaveWalletAsync(wallet, _lifetime?.Token ?? CancellationToken.None);
            _walletAddress = wallet;
            _lastEarningsRead = DateTimeOffset.MinValue;
            _profitability.ResetAccumulationBaseline();
            UpdateCommandLinePreviewFromDrafts();
            WalletStatus = "Публичный адрес сохранён только в локальном профиле Windows";
            await RefreshAsync(force: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WalletStatus = $"Не удалось сохранить адрес: {ex.Message}";
        }
    }

    private async Task SaveSessionStateAsync()
    {
        _lastSessionSaveAt = DateTimeOffset.UtcNow;
        var state = new PersistedSessionState(
            _profitability.SessionEnergyKwh,
            _profitability.SessionElectricityCostRub,
            _totalHashrateBuffer.ToArray(),
            _watchdogEvents.ToArray(),
            DateTimeOffset.UtcNow);
        await SessionStateService.SaveAsync(state);
    }

    private static string ReadRecentAppLogLines(int maxLines)
    {
        try
        {
            var path = AppLogger.GetLogFilePath();
            if (!File.Exists(path)) return "Журнал приложения пока пуст.";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 12000) stream.Seek(-12000, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            return string.Join(Environment.NewLine, lines.TakeLast(maxLines));
        }
        catch
        {
            return "Не удалось прочитать журнал приложения.";
        }
    }

    private static bool TryExtractFirstWatts(string? text, out int watts)
    {
        watts = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var first = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out watts) && watts > 0;
    }

    private static bool IsAutoSetting(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
        || value.Trim().Equals("авто", StringComparison.OrdinalIgnoreCase);

    private static void AppendSample(List<double> buffer, double sample)
    {
        if (buffer.Count >= MaxHistorySamples)
            buffer.RemoveAt(0);
        buffer.Add(sample);
    }

    private static string FormatUptime(long totalSeconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return span.TotalDays >= 1
            ? $"{(int)span.TotalDays}д {span.Hours:00}ч {span.Minutes:00}м"
            : $"{span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }

    private static string FormatPrl(double? amount) => amount is double value ? $"{value:0.########} PRL" : "Нет данных";

    private static string SettingDraft(string value) => string.IsNullOrWhiteSpace(value) ? "Авто" : value;

    private static string NormalizeGpuSetting(string value, int gpuCount)
    {
        if (IsAutoSetting(value))
            return string.Empty;

        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return string.Empty;
        return parts.Length == 1 && gpuCount > 1
            ? string.Join(',', Enumerable.Repeat(parts[0], gpuCount))
            : string.Join(',', parts);
    }

    private void ShowToast(string message, string severity = "Info", int seconds = 5)
    {
        ToastSeverity = severity;
        ToastMessage = message;
        _toastExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(seconds, 2, 20));
    }

    private void DismissToast()
    {
        ToastMessage = string.Empty;
        _toastExpiresAt = DateTimeOffset.MinValue;
    }

    private async Task SwitchPoolManuallyAsync()
    {
        if (!TryBeginMinerAction()) return;
        try
        {
            _minerControl.UseBackupPool = !_minerControl.UseBackupPool;
            OnPropertyChanged(nameof(ActivePoolBadgeText));
            UpdateCommandLinePreviewFromDrafts();
            var label = _minerControl.UseBackupPool ? "резервный (EU)" : "основной (RU)";
            var msg = $"Пул переключён вручную на {label}.";
            AppendWatchdogEvent(msg);
            ShowToast(msg, "Info");

            var wallet = string.IsNullOrWhiteSpace(_walletAddress) ? WalletAddressDraft.Trim().ToLowerInvariant() : _walletAddress;
            if (_minerControl.IsRunning && PoolEarningsClient.IsValidWallet(wallet))
            {
                await _minerControl.RestartAsync(wallet, GetTargetNvidiaGpuCount(), _lifetime?.Token ?? CancellationToken.None);
                _watchdog.MarkMinerStarted(DateTimeOffset.UtcNow);
            }
            _ = RefreshAsync(force: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowToast($"Не удалось переключить пул: {ex.Message}", "Error");
        }
        finally
        {
            EndMinerAction();
        }
    }

    private async Task PingPoolsNowAsync()
    {
        var ping = await _poolPingService.MeasureAsync(PrimaryPoolDraft, BackupPoolDraft, _lifetime?.Token ?? CancellationToken.None);
        PoolPingText = ping.SummaryText;
        ShowToast(ping.SummaryText, "Info");
    }

    private void ClearWatchdogLog()
    {
        _watchdogEvents.Clear();
        WatchdogEventsText = "Лента событий Watchdog очищена пользователем.";
        _ = SaveSessionStateAsync();
        ShowToast("Журнал событий Watchdog очищен", "Info");
    }

    private void CopyFilteredLogsToClipboard()
    {
        try
        {
            var bundle = $"=== ONEZEROMINER.LOG ===\r\n{FilteredMinerLogTail}\r\n\r\n=== WATCHDOG ===\r\n{FilteredWatchdogEventsText}\r\n\r\n=== APP.LOG ===\r\n{FilteredAppLogTail}";
            Clipboard.SetText(bundle);
            ShowToast("Отфильтрованные журналы скопированы в буфер обмена", "Success");
        }
        catch (Exception ex)
        {
            ShowToast($"Ошибка копирования логов: {ex.Message}", "Error");
        }
    }

    private async Task RunDiagnosticsCheckAsync()
    {
        var tdr = await WindowsDiagnosticsService.CheckRecentNvidiaDriverResetAsync(_lifetime?.Token ?? CancellationToken.None);
        UpdateDiagnosticFlagsCore(tdr);
        ShowToast("Самодиагностика системы обновлена", "Success");
    }

    private void UpdateDiagnosticFlagsCore(string? tdrEvent)
    {
        DiagAdminOk = MinerControlService.IsCurrentProcessElevated();
        DiagAdminText = DiagAdminOk ? "Права Администратора: ОК" : "Запуск без прав Admin";

        DiagNvmlOk = Gpus.Any(g => g.Source is "nvml" or "nvidia-smi");
        DiagNvmlText = DiagNvmlOk ? "Драйвер NVML: подключён" : "NVML: ожидание/не найден";

        var minerPath = MinerControlService.ResolveMinerPath();
        DiagMinerExeOk = !string.IsNullOrWhiteSpace(minerPath);
        DiagMinerExeText = DiagMinerExeOk ? "onezerominer.exe: найден" : "onezerominer.exe: НЕ НАЙДЕН";

        DiagAutoStartOk = AutoStartMining;
        DiagAutoStartText = DiagAutoStartOk ? "Автозапуск: зарегистрирован" : "Автозапуск: ручной";

        DiagTdrOk = string.IsNullOrWhiteSpace(tdrEvent);
        DiagTdrText = DiagTdrOk ? "TDR (4101): сбросов нет" : $"TDR 4101: {tdrEvent}";
    }

    private string FilterLogText(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        var hasQuery = !string.IsNullOrWhiteSpace(LogSearchQuery);
        if (!hasQuery && !LogOnlyWarningsAndErrors) return source;

        var lines = source.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> filtered = lines;

        if (LogOnlyWarningsAndErrors)
        {
            filtered = filtered.Where(line =>
                line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                || line.Contains("WARN", StringComparison.OrdinalIgnoreCase)
                || line.Contains("АВАРИЙН", StringComparison.OrdinalIgnoreCase)
                || line.Contains("ВНИМАНИЕ", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Ошибка", StringComparison.OrdinalIgnoreCase)
                || line.Contains("fail", StringComparison.OrdinalIgnoreCase)
                || line.Contains("dropped", StringComparison.OrdinalIgnoreCase)
                || line.Contains("timeout", StringComparison.OrdinalIgnoreCase));
        }

        if (hasQuery)
        {
            var q = LogSearchQuery.Trim();
            filtered = filtered.Where(line => line.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var result = filtered.ToArray();
        return result.Length == 0 ? "Нет строк, соответствующих фильтру." : string.Join(Environment.NewLine, result);
    }

    private void NotifyFanCurveChanged()
    {
        OnPropertyChanged(nameof(FanCurveTempLowValue));
        OnPropertyChanged(nameof(FanCurveSpeedLowValue));
        OnPropertyChanged(nameof(FanCurveTempMidValue));
        OnPropertyChanged(nameof(FanCurveSpeedMidValue));
        OnPropertyChanged(nameof(FanCurveTempHighValue));
        OnPropertyChanged(nameof(FanCurveSpeedHighValue));
        OnPropertyChanged(nameof(FanCurveValidationError));
    }

    private string ValidateFanCurveDrafts()
    {
        if (!int.TryParse(FanCurveTempLowCDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t1)
            || !int.TryParse(FanCurveTempMidCDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t2)
            || !int.TryParse(FanCurveTempHighCDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t3)
            || !int.TryParse(FanCurveSpeedLowPctDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s1)
            || !int.TryParse(FanCurveSpeedMidPctDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s2)
            || !int.TryParse(FanCurveSpeedHighPctDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s3))
            return "Укажите целые числа для всех температур и процентов оборотов.";

        if (t1 < 30 || t1 >= t2 || t2 >= t3 || t3 > 95)
            return "Температуры должны возрастать: 30 ≤ T1 < T2 < T3 ≤ 95 °C.";
        if (s1 < 30 || s1 > s2 || s2 > s3 || s3 > 100)
            return "Обороты кулеров должны возрастать: 30 ≤ S1 ≤ S2 ≤ S3 ≤ 100%.";
        if (!int.TryParse(FanHysteresisCDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hyst) || hyst is < 1 or > 10)
            return "Гистерезис должен быть от 1 до 10 °C.";
        if (!int.TryParse(VramTempOffsetCDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var off) || off is < 0 or > 30)
            return "Сдвиг GDDR6X должен быть от 0 до 30 °C.";
        return string.Empty;
    }

    private string ValidateWatchdogDrafts()
    {
        if (!int.TryParse(ResumeTempCDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resT)
            || !int.TryParse(WarningTempCDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var warnT)
            || !int.TryParse(CriticalStopTempCDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var critT))
            return "Температурные пороги должны быть целыми числами.";
        if (resT < 45 || resT >= warnT || warnT >= critT || critT > 100)
            return "Проверьте порядок температур: 45 ≤ Возврат < Предупреждение < Стоп ≤ 100 °C.";
        if (!int.TryParse(ZeroHashrateRestartSecondsDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var zeroS) || zeroS is < 30 or > 600)
            return "Время до рестарта при 0 TH/s: 30–600 секунд.";
        if (!int.TryParse(PoolDropSwitchThresholdCountDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var drops) || drops is < 1 or > 10)
            return "Количество сбоев пула до переключения: 1–10.";
        if (!int.TryParse(DayTariffStartHourDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dH) || dH is < 0 or > 23
            || !int.TryParse(NightTariffStartHourDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nH) || nH is < 0 or > 23)
            return "Часы начала тарифа Т1 и Т2 должны быть от 0 до 23.";
        return string.Empty;
    }

    private void RefreshSchedulePreview()
    {
        var dayRate = TryParseTariff(ElectricityRateDraft, out var dr) ? dr : _config.ElectricityRateRubPerKwh;
        var nightRate = TryParseTariff(NightElectricityRateDraft, out var nr) ? nr : _config.NightElectricityRateRubPerKwh;
        var previewConfig = _config with
        {
            DualTariffEnabled = DualTariffEnabled,
            DayTariffStartHour = DayTariffStartHourValue,
            NightTariffStartHour = NightTariffStartHourValue,
            DaySchedulePreset = DaySchedulePreset,
            NightSchedulePreset = NightSchedulePreset
        };
        var isNight = previewConfig.DualTariffEnabled && previewConfig.IsNightTariffWindow(DateTime.Now);
        var blended = previewConfig.GetBlendedDailyTariff(dayRate, nightRate);
        CurrentScheduleStatusText = previewConfig.DualTariffEnabled
            ? $"Сейчас: {(isNight ? $"Т2 НОЧЬ ({NightSchedulePreset})" : $"Т1 ДЕНЬ ({DaySchedulePreset})")} · Ночных часов: {previewConfig.GetNightHoursPerDay()} ч/сут · Средний тариф: {blended:0.00} ₽/кВт·ч"
            : $"Единый тариф: {dayRate:0.00} ₽/кВт·ч круглосуточно (включите двухтарифный учёт для зоны Т2)";
    }

    private void SyncOverclockRowsFromDrafts()
    {
        if (_syncingPerGpuRows) return;
        _syncingPerGpuRows = true;
        try
        {
            var nvidiaGpus = Gpus.Where(IsNvidiaGpu).OrderBy(g => g.Id).ToArray();
            if (nvidiaGpus.Length == 0) return;

            static string Pick(string draft, int index)
            {
                if (IsAutoSetting(draft)) return "Авто";
                var parts = draft.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0) return "Авто";
                return index < parts.Length ? parts[index] : parts[0];
            }

            while (GpuOverclockRows.Count > nvidiaGpus.Length)
                GpuOverclockRows.RemoveAt(GpuOverclockRows.Count - 1);

            for (var i = 0; i < nvidiaGpus.Length; i++)
            {
                var gpu = nvidiaGpus[i];
                var cclk = Pick(CoreClockDraft, i);
                var mclk = Pick(MemoryClockDraft, i);
                var pl = Pick(PowerLimitDraft, i);
                var kern = Pick(KernelDraft, i);
                var fan = Pick(FanDraft, i);

                if (i >= GpuOverclockRows.Count)
                {
                    var row = new GpuOverclockRowEditor
                    {
                        Id = gpu.Id,
                        Name = gpu.Name,
                        ChangedCallback = PushOverclockRowsToDrafts
                    };
                    row.UpdateSilently(cclk, mclk, pl, kern, fan, gpu.PowerRangeBadgeText);
                    GpuOverclockRows.Add(row);
                }
                else
                {
                    GpuOverclockRows[i].UpdateSilently(cclk, mclk, pl, kern, fan, gpu.PowerRangeBadgeText);
                }
            }
        }
        finally
        {
            _syncingPerGpuRows = false;
        }
    }

    private void PushOverclockRowsToDrafts()
    {
        if (_syncingPerGpuRows || GpuOverclockRows.Count == 0) return;
        _syncingPerGpuRows = true;
        try
        {
            static string JoinField(IEnumerable<string> values)
            {
                var arr = values.Select(v => string.IsNullOrWhiteSpace(v) ? "Авто" : v.Trim()).ToArray();
                return arr.All(IsAutoSetting) ? "Авто" : string.Join(',', arr.Select(v => IsAutoSetting(v) ? "*" : v));
            }

            CoreClockDraft = JoinField(GpuOverclockRows.Select(r => r.CoreClock));
            MemoryClockDraft = JoinField(GpuOverclockRows.Select(r => r.MemoryClock));
            PowerLimitDraft = JoinField(GpuOverclockRows.Select(r => r.PowerLimit));
            KernelDraft = JoinField(GpuOverclockRows.Select(r => r.Kernel));
            FanDraft = JoinField(GpuOverclockRows.Select(r => r.Fan));
        }
        finally
        {
            _syncingPerGpuRows = false;
        }
    }

    private static double TryParseDoubleOrFallback(string text, double fallback)
    {
        var normalized = (text ?? string.Empty).Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)
            ? parsed
            : fallback;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (SmartFanCurveEnabled && Gpus.Count > 0)
            _ = _nvidiaSmi.TryResetFansToAutoDirect(Gpus.Where(IsNvidiaGpu).Select(g => g.Id));
        _ = SaveSessionStateAsync();
        WindowsDiagnosticsService.SetMiningSleepPrevention(false);
        _timer.Stop();
        _lifetime?.Cancel();
        _lifetime?.Dispose();
    }
}
