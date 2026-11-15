using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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
    private readonly Dictionary<int, List<double>> _gpuHashrateBuffers = [];
    private readonly Dictionary<int, List<double>> _gpuTempBuffers = [];
    private readonly Dictionary<int, int> _lastSmartFanApplied = [];
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
    private DateTimeOffset? _lastEnergySampleAt;
    private DateTimeOffset? _sessionFirstBalanceAt;
    private double? _sessionFirstBalancePrl;
    private double? _lastKnownPaidPrl;
    private double? _lastKnownTotalPrl;
    private double? _lastKnownPrlRubRate;
    private double? _lastObservedGrossRubPerDay;
    private double? _lastObservedNetRubPerDay;
    private double? _lastTotalHashrateTHs;
    private double? _lastTotalPowerWatts;
    private double? _lastEfficiencyGhPerWatt;
    private double _sessionEnergyKwh;
    private double _sessionElectricityCostRub;
    private DateTimeOffset? _minerStartedOrObservedAt;
    private DateTimeOffset? _zeroHashrateSince;
    private DateTimeOffset _lastThermalWarningAt = DateTimeOffset.MinValue;
    private bool _userManuallyStopped;
    private bool _thermalPaused;
    private bool _isAutoTuning;
    private string? _lastScheduledWindowApplied;
    private int _watchdogRestartCount;
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
    private string _netProfitSummaryText = "Чистый доход (после розетки): ожидание замеров";
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
    private string _activePreset;
    private string _watchdogStatusText = "Watchdog и термозащита активны";
    private string _watchdogEventsText = "Журнал защиты запущен. Аварийных событий нет.";
    private string _minerLogTail = "Ожидание чтения onezerominer.log…";
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
        _installedVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(3, 7, 2);
        _autoStartMining = config.AutoStartMining || MinerControlService.IsAutoStartTaskRegistered();
        _watchdogEnabled = config.WatchdogEnabled;
        _smartFanCurveEnabled = config.SmartFanCurveEnabled;
        _dualTariffEnabled = config.DualTariffEnabled;
        _nightScheduleEnabled = config.NightScheduleEnabled;
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
        _telegramEnabled = config.TelegramEnabled;
        _telegramRemoteControlEnabled = config.TelegramRemoteControlEnabled;
        _telegramBotTokenDraft = config.TelegramBotToken;
        _telegramChatIdDraft = config.TelegramChatId;
        _telegramStatusText = _telegramEnabled && !string.IsNullOrWhiteSpace(_telegramBotTokenDraft) && !string.IsNullOrWhiteSpace(_telegramChatIdDraft)
            ? (_telegramRemoteControlEnabled ? "Telegram: алерты и команды (/status, /preset) активны" : "Telegram-уведомления активны")
            : "Введите токен бота и Chat ID для алертов и управления";
        _walletAddress = config.WalletAddress;
        _walletAddressDraft = config.WalletAddress;
        _walletStatus = PoolEarningsClient.IsValidWallet(config.WalletAddress) ? "Адрес выплат загружен из локального профиля" : "Введите публичный адрес PRL";
        _minerCommandLinePreview = _minerControl.GetCommandLinePreview(_walletAddressDraft, Math.Max(2, Gpus.Count));

        SaveWalletCommand = new RelayCommand(() => _ = SaveWalletAsync(), () => PoolEarningsClient.IsValidWallet(WalletAddressDraft));
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(force: true));
        StartMinerCommand = new RelayCommand(() => _ = StartMinerAsync(manual: true), () => !_isMinerBusy && !_isAutoTuning && !_minerControl.IsRunning);
        StopMinerCommand = new RelayCommand(() => _ = StopMinerAsync(manual: true), () => !_isMinerBusy && !_isAutoTuning && _minerControl.IsRunning);
        ToggleMinerModeCommand = new RelayCommand(() => _ = ToggleMinerModeAsync(), () => !_isMinerBusy);
        ToggleWatchdogCommand = new RelayCommand(() => _ = ToggleWatchdogAsync(), () => !_isMinerBusy);
        ToggleSmartFanCurveCommand = new RelayCommand(() => _ = ToggleSmartFanCurveAsync(), () => !_isMinerBusy);
        ToggleDualTariffCommand = new RelayCommand(() => _ = ToggleDualTariffAsync(), () => !_isMinerBusy);
        ToggleNightScheduleCommand = new RelayCommand(() => _ = ToggleNightScheduleAsync(), () => !_isMinerBusy);
        RunAutoTuneBenchmarkCommand = new RelayCommand(() => _ = RunAutoTuneBenchmarkAsync(), () => !_isMinerBusy && !_isAutoTuning && _minerControl.IsRunning);
        ExportCsvReportCommand = new RelayCommand(() => _ = ExportCsvReportAsync());
        PresetAutoCommand = new RelayCommand(() => _ = ApplyPresetAndSaveAsync("Auto"), () => !_isMinerBusy && !_isAutoTuning);
        PresetEcoCommand = new RelayCommand(() => _ = ApplyPresetAndSaveAsync("Eco"), () => !_isMinerBusy && !_isAutoTuning);
        PresetBalanceCommand = new RelayCommand(() => _ = ApplyPresetAndSaveAsync("Balance"), () => !_isMinerBusy && !_isAutoTuning);
        PresetTurboCommand = new RelayCommand(() => _ = ApplyPresetAndSaveAsync("Turbo"), () => !_isMinerBusy && !_isAutoTuning);
        PresetSilentCommand = new RelayCommand(() => _ = ApplyPresetAndSaveAsync("Silent"), () => !_isMinerBusy && !_isAutoTuning);
        ApplyPowerLimitNowCommand = new RelayCommand(() => _ = ApplySliderPowerLimitAsync(), () => !_isMinerBusy && !_isAutoTuning);
        SaveMinerSettingsCommand = new RelayCommand(() => _ = SaveMinerSettingsAsync(), CanSaveMinerSettings);
        SaveTelegramCommand = new RelayCommand(() => _ = SaveTelegramSettingsAsync());
        TestTelegramCommand = new RelayCommand(() => _ = TestTelegramAsync());
        CopyCommandLineCommand = new RelayCommand(CopyCommandLineToClipboard);
        UpdateCommand = new RelayCommand(() => _ = UpdateOrCheckAsync(), () => !_isCheckingUpdate && !_isDownloadingUpdate);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        AppendWatchdogEvent($"Система v{_installedVersion.ToString(3)} инициализирована (предупреждение {_config.WarningTempC} °C, стоп {_config.CriticalStopTempC} °C, возврат {_config.ResumeTempC} °C).");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string>? InstallerDownloaded;
    public event Action<string, TrayHealthState>? TrayStatusChanged;

    public ObservableCollection<GpuLiveMetrics> Gpus { get; } = [];
    public RelayCommand RefreshCommand { get; }
    public RelayCommand SaveWalletCommand { get; }
    public RelayCommand StartMinerCommand { get; }
    public RelayCommand StopMinerCommand { get; }
    public RelayCommand ToggleMinerModeCommand { get; }
    public RelayCommand ToggleWatchdogCommand { get; }
    public RelayCommand ToggleSmartFanCurveCommand { get; }
    public RelayCommand ToggleDualTariffCommand { get; }
    public RelayCommand ToggleNightScheduleCommand { get; }
    public RelayCommand RunAutoTuneBenchmarkCommand { get; }
    public RelayCommand ExportCsvReportCommand { get; }
    public RelayCommand PresetAutoCommand { get; }
    public RelayCommand PresetEcoCommand { get; }
    public RelayCommand PresetBalanceCommand { get; }
    public RelayCommand PresetTurboCommand { get; }
    public RelayCommand PresetSilentCommand { get; }
    public RelayCommand ApplyPowerLimitNowCommand { get; }
    public RelayCommand SaveMinerSettingsCommand { get; }
    public RelayCommand SaveTelegramCommand { get; }
    public RelayCommand TestTelegramCommand { get; }
    public RelayCommand CopyCommandLineCommand { get; }
    public RelayCommand UpdateCommand { get; }

    public void SetUpdateStatus(string message) => UpdateStatusText = message;

    public bool MinimizeToTray => _config.MinimizeToTray;
    public string ConnectionStatus { get => _connectionStatus; private set => Set(ref _connectionStatus, value); }
    public string MinerApiStatus { get => _minerApiStatus; private set => Set(ref _minerApiStatus, value); }
    public string HardwareStatus { get => _hardwareStatus; private set => Set(ref _hardwareStatus, value); }
    public string PoolStatus { get => _poolStatus; private set => Set(ref _poolStatus, value); }
    public string PoolUrl { get => _poolUrl; private set => Set(ref _poolUrl, value); }
    public string PoolPingText { get => _poolPingText; private set => Set(ref _poolPingText, value); }
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
    public string AutoTuneButtonLabel => _isAutoTuning ? "АВТО-ПОДБОР…" : "АВТО-ПОДБОР GH/W";
    public string ActivePreset { get => _activePreset; private set { if (Set(ref _activePreset, value)) OnPropertyChanged(nameof(ActivePresetLabel)); } }
    public string ActivePresetLabel => $"ПРОФИЛЬ: {_activePreset.ToUpperInvariant()}";
    public string WatchdogStatusText { get => _watchdogStatusText; private set => Set(ref _watchdogStatusText, value); }
    public string WatchdogEventsText { get => _watchdogEventsText; private set => Set(ref _watchdogEventsText, value); }
    public string MinerLogTail { get => _minerLogTail; private set => Set(ref _minerLogTail, value); }
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
        }
    }

    public string ElectricityRateDraft { get => _electricityRateDraft; set { if (Set(ref _electricityRateDraft, value)) SaveMinerSettingsCommand.NotifyCanExecuteChanged(); } }
    public string NightElectricityRateDraft { get => _nightElectricityRateDraft; set { if (Set(ref _nightElectricityRateDraft, value)) SaveMinerSettingsCommand.NotifyCanExecuteChanged(); } }
    public bool TelegramEnabled { get => _telegramEnabled; set => Set(ref _telegramEnabled, value); }
    public bool TelegramRemoteControlEnabled { get => _telegramRemoteControlEnabled; set => Set(ref _telegramRemoteControlEnabled, value); }
    public string TelegramBotTokenDraft { get => _telegramBotTokenDraft; set => Set(ref _telegramBotTokenDraft, value); }
    public string TelegramChatIdDraft { get => _telegramChatIdDraft; set => Set(ref _telegramChatIdDraft, value); }
    public string TelegramStatusText { get => _telegramStatusText; private set => Set(ref _telegramStatusText, value); }
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
        var msg = enabled
            ? $"Включён двухтарифный учёт (День {_config.DayTariffStartHour:00}:00–{_config.NightTariffStartHour:00}:00 · Ночь {_config.NightTariffStartHour:00}:00–{_config.DayTariffStartHour:00}:00)"
            : "Включён единый круглосуточный тариф электроэнергии";
        MinerControlStatus = msg;
        AppendWatchdogEvent(msg);
    }

    private async Task ToggleNightScheduleAsync()
    {
        var enabled = !NightScheduleEnabled;
        _config = _config with { NightScheduleEnabled = enabled };
        await RigConfig.SaveLocalSettingsAsync(_config, _lifetime?.Token ?? CancellationToken.None);
        NightScheduleEnabled = enabled;
        _lastScheduledWindowApplied = null;
        var msg = enabled
            ? $"Авто-расписание День/Ночь включено (День → {_config.DaySchedulePreset}, Ночь → {_config.NightSchedulePreset})"
            : "Авто-расписание профилей День/Ночь отключено";
        MinerControlStatus = msg;
        AppendWatchdogEvent(msg);
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
        var nvidiaCount = Gpus.Count(IsNvidiaGpu);
        if (nvidiaCount == 0)
        {
            MinerControlStatus = "Авто-подбор требует активные видеокарты NVIDIA и запущенный майнер.";
            return;
        }

        _isAutoTuning = true;
        OnPropertyChanged(nameof(AutoTuneButtonLabel));
        UpdateMinerState();

        var minW = (int)Math.Ceiling(PowerSliderMinimum);
        var maxW = (int)Math.Floor(PowerSliderMaximum);
        var candidates = new[] { 130, 140, 150, 165, 180 }
            .Select(w => minW <= maxW ? Math.Clamp(w, minW, maxW) : w)
            .Distinct()
            .Order()
            .ToArray();

        AppendWatchdogEvent($"Старт авто-бенчмарка «Золотая точка» GH/W по ступеням: {string.Join(", ", candidates.Select(c => $"{c} Вт"))}");
        var bestWatts = candidates[0];
        var bestEfficiency = 0d;
        var token = _lifetime?.Token ?? CancellationToken.None;

        try
        {
            foreach (var candidateW in candidates)
            {
                var limitStr = string.Join(',', Enumerable.Repeat(candidateW.ToString(CultureInfo.InvariantCulture), nvidiaCount));
                MinerControlStatus = $"Авто-подбор: проверка {candidateW} Вт (стабилизация 18 с)…";
                await _nvidiaSmi.ApplyPowerLimitsAsync(limitStr, token);
                await Task.Delay(TimeSpan.FromSeconds(18), token);

                var snap = await _telemetry.ReadAsync(token);
                Apply(snap);

                var power = snap.Gpus.Where(g => g.PowerWatts is not null).Sum(g => g.PowerWatts!.Value);
                var hashrate = snap.TotalHashrateTHs ?? 0;
                var maxTemp = snap.Gpus.Where(g => g.TempC is not null).Select(g => g.TempC!.Value).DefaultIfEmpty(0).Max();
                var eff = hashrate > 0 && power > 0 ? (hashrate * 1000d) / power : 0;

                var stepResult = $"Ступень {candidateW} Вт → {hashrate:0.00} TH/s · {power:0} Вт · {eff:0.00} GH/W · макс {maxTemp:0} °C";
                AppendWatchdogEvent(stepResult);

                if (eff > bestEfficiency && maxTemp < _config.WarningTempC)
                {
                    bestEfficiency = eff;
                    bestWatts = candidateW;
                }
            }

            PowerLimitDraft = string.Join(',', Enumerable.Repeat(bestWatts.ToString(CultureInfo.InvariantCulture), nvidiaCount));
            ActivePreset = $"Tuned-{bestWatts}W";
            await SaveMinerSettingsAsync();

            var summary = $"Авто-бенчмарк завершён: выбрана золотая точка {bestWatts} Вт ({bestEfficiency:0.00} GH/W).";
            MinerControlStatus = summary;
            AppendWatchdogEvent(summary);
            await RecordCsvSnapshotAsync(summary);
            await SendTelegramAlertIfEnabledAsync($"🏆 {summary}");
        }
        catch (OperationCanceledException)
        {
            MinerControlStatus = "Авто-подбор энергоэффективности отменён.";
        }
        catch (Exception ex)
        {
            MinerControlStatus = $"Ошибка авто-подбора: {ex.Message}";
            AppendWatchdogEvent($"Авто-бенчмарк прерван: {ex.Message}");
        }
        finally
        {
            _isAutoTuning = false;
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
                _sessionEnergyKwh,
                _config.GetActiveElectricityTariff(DateTime.Now),
                _lastKnownTotalPrl,
                _lastKnownPaidPrl,
                _lastKnownPrlRubRate,
                _lastObservedNetRubPerDay,
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
                    await StopMinerAsync(manual: false);
                    await Task.Delay(1500, token);
                    await StartMinerAsync(manual: true);
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
                    ? "Telegram сохранён (уведомления и команды /status, /preset, /start_miner активны)"
                    : "Настройки Telegram сохранены в локальном профиле")
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
        _lastSmartFanApplied.Clear();
        var msg = enabled
            ? "Умная кривая кулеров включена (регулировка по пику Ядро/GDDR6X с гистерезисом 3 °C)"
            : "Умная кривая кулеров отключена";
        MinerControlStatus = msg;
        AppendWatchdogEvent(msg);
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
            var release = await _updateService.CheckForUpdateAsync(_installedVersion, _lifetime?.Token ?? CancellationToken.None);
            _availableUpdate = release;
            OnPropertyChanged(nameof(UpdateAvailable));
            OnPropertyChanged(nameof(UpdateButtonText));
            UpdateStatusText = release is null
                ? $"Установлена актуальная v{_installedVersion.ToString(3)}"
                : $"Доступна v{release.Version} ({release.AssetSize / 1024d / 1024d:0.0} МБ) — нажмите для установки";
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
            InstallerDownloaded?.Invoke(installer);
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

    private async Task StartMinerAsync(bool manual = false)
    {
        if (!TryBeginMinerAction()) return;
        if (manual)
        {
            _userManuallyStopped = false;
            _thermalPaused = false;
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
            _minerStartedOrObservedAt = DateTimeOffset.UtcNow;
            _zeroHashrateSince = null;
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
            CoreClock = NormalizeGpuSetting(_coreClockDraft, count),
            MemoryClock = NormalizeGpuSetting(_memoryClockDraft, count),
            PowerLimit = NormalizeGpuSetting(_powerLimitDraft, count),
            Kernel = NormalizeGpuSetting(_kernelDraft, count),
            Fan = NormalizeGpuSetting(_fanDraft, count)
        };
        _minerControl.UpdateConfig(previewConfig);
        MinerCommandLinePreview = _minerControl.GetCommandLinePreview(_walletAddressDraft, count);
    }

    private async Task StopMinerAsync(bool manual = false)
    {
        if (!TryBeginMinerAction()) return;
        if (manual)
        {
            _userManuallyStopped = true;
            _thermalPaused = false;
        }
        MinerControlStatus = "Остановка: отправка штатного Ctrl+C…";
        try
        {
            await _minerControl.StopAsync(_lifetime?.Token ?? CancellationToken.None);
            WindowsDiagnosticsService.SetMiningSleepPrevention(false);
            if (manual)
            {
                _config = _config with { LastKnownMiningActive = false };
                await RigConfig.SaveLocalSettingsAsync(_config, _lifetime?.Token ?? CancellationToken.None);
            }
            _minerStartedOrObservedAt = null;
            _zeroHashrateSince = null;
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
        try
        {
            var token = _lifetime?.Token ?? CancellationToken.None;
            var count = GetTargetNvidiaGpuCount();
            _ = TryParseTariff(ElectricityRateDraft, out var parsedDayTariff);
            _ = TryParseTariff(NightElectricityRateDraft, out var parsedNightTariff);
            var normalizedPowerLimit = NormalizeGpuSetting(PowerLimitDraft, count);
            _config = _config with
            {
                WorkerName = WorkerNameDraft.Trim(),
                CoreClock = NormalizeGpuSetting(CoreClockDraft, count),
                MemoryClock = NormalizeGpuSetting(MemoryClockDraft, count),
                PowerLimit = normalizedPowerLimit,
                Kernel = NormalizeGpuSetting(KernelDraft, count),
                Fan = NormalizeGpuSetting(FanDraft, count),
                ActivePreset = ActivePreset,
                ElectricityRateRubPerKwh = parsedDayTariff,
                NightElectricityRateRubPerKwh = parsedNightTariff
            };
            await RigConfig.SaveLocalSettingsAsync(_config, token);
            _minerControl.UpdateConfig(_config);
            MinerCommandLinePreview = _minerControl.GetCommandLinePreview(_walletAddressDraft, count);
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
            var snapshot = await _telemetry.ReadAsync(token);
            Apply(snapshot);
            ApplySmartFanCurveIfEnabled(snapshot.Gpus);
            MinerLogTail = _minerControl.ReadRecentLogLines(14);
            await EvaluateDayNightScheduleAsync();
            await EvaluateWatchdogAndThermalProtectionAsync(snapshot);
            await PollTelegramRemoteCommandsAsync();

            if (force || DateTimeOffset.UtcNow - _lastPoolPingAt >= TimeSpan.FromSeconds(15))
            {
                _lastPoolPingAt = DateTimeOffset.UtcNow;
                var ping = await _poolPingService.MeasureAsync(_config.PrimaryPool, _config.BackupPool, token);
                PoolPingText = ping.SummaryText;
                if (ping.PrimaryPingMs is null && ping.BackupPingMs is not null)
                {
                    if (!_minerControl.UseBackupPool)
                    {
                        _minerControl.UseBackupPool = true;
                        UpdateCommandLinePreviewFromDrafts();
                        AppendWatchdogEvent($"Сетевой монитор: основной пул RU не отвечает, выбран резервный пул EU ({ping.BackupPingMs} мс).");
                    }
                }
                else if (ping.PrimaryPingMs is not null && _minerControl.UseBackupPool)
                {
                    _minerControl.UseBackupPool = false;
                    UpdateCommandLinePreviewFromDrafts();
                    AppendWatchdogEvent($"Сетевой монитор: основной пул RU снова доступен ({ping.PrimaryPingMs} мс).");
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
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            ConnectionStatus = "Ошибка обновления";
            DetailStatus = $"Не удалось обновить телеметрию: {ex.Message}";
            LastUpdated = "Последний опрос завершился ошибкой";
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

        var targets = new List<(int GpuId, int TargetFanPercent)>();
        foreach (var gpu in gpus)
        {
            var core = gpu.TempC ?? 0;
            var vramAdjusted = (gpu.VramTempC ?? 0) - 14;
            var effectiveTemp = Math.Max(core, vramAdjusted);
            if (effectiveTemp <= 0) continue;

            var desiredFan = effectiveTemp switch
            {
                < 50 => 45,
                < 60 => 55,
                < 68 => 68,
                < 75 => 82,
                _ => 98
            };

            if (_lastSmartFanApplied.TryGetValue(gpu.Id, out var prevFan) && Math.Abs(desiredFan - prevFan) < 5)
                continue;

            _lastSmartFanApplied[gpu.Id] = desiredFan;
            targets.Add((gpu.Id, desiredFan));
        }

        if (targets.Count > 0)
            _ = _nvidiaSmi.TryApplySmartFansDirect(targets);
    }

    private async Task EvaluateWatchdogAndThermalProtectionAsync(TelemetrySnapshot snapshot)
    {
        if (!WatchdogEnabled || _isMinerBusy || _isAutoTuning || _userManuallyStopped)
            return;

        var isRunning = _minerControl.IsRunning;
        var maxCoreTemp = snapshot.Gpus.Where(g => g.TempC is not null).Select(g => g.TempC!.Value).DefaultIfEmpty(0).Max();
        var maxVramTemp = snapshot.Gpus.Where(g => g.VramTempC is not null).Select(g => g.VramTempC!.Value).DefaultIfEmpty(0).Max();

        if (isRunning && (maxCoreTemp >= _config.CriticalStopTempC || maxVramTemp >= 104))
        {
            _thermalPaused = true;
            var reason = maxVramTemp >= 104
                ? $"АВАРИЙНЫЙ СТОП: память GDDR6X достигла {maxVramTemp:0} °C"
                : $"АВАРИЙНЫЙ СТОП: температура GPU достигла {maxCoreTemp:0} °C (порог {_config.CriticalStopTempC} °C)";
            WatchdogStatusText = $"{reason}. Ожидание остывания до ≤ {_config.ResumeTempC} °C.";
            AppendWatchdogEvent(reason);
            await RecordCsvSnapshotAsync(reason);
            await SendTelegramAlertIfEnabledAsync($"🚨 {reason}\nМайнер экстренно остановлен до остывания видеокарт ≤ {_config.ResumeTempC} °C.");
            await StopMinerAsync(manual: false);
            return;
        }

        if (_thermalPaused && !isRunning && snapshot.Gpus.Count > 0 && maxCoreTemp > 0 && maxCoreTemp <= _config.ResumeTempC && maxVramTemp <= 88)
        {
            _thermalPaused = false;
            var resumeMsg = $"Карты остыли до {maxCoreTemp:0} °C (≤ {_config.ResumeTempC} °C). Автоматическое возобновление майнинга.";
            WatchdogStatusText = resumeMsg;
            AppendWatchdogEvent(resumeMsg);
            await RecordCsvSnapshotAsync(resumeMsg);
            await SendTelegramAlertIfEnabledAsync($"✅ {resumeMsg}");
            await StartMinerAsync(manual: false);
            return;
        }

        if (isRunning && maxCoreTemp >= _config.WarningTempC && DateTimeOffset.UtcNow - _lastThermalWarningAt >= TimeSpan.FromSeconds(45))
        {
            _lastThermalWarningAt = DateTimeOffset.UtcNow;
            var warnMsg = $"ВНИМАНИЕ: температура GPU {maxCoreTemp:0} °C приблизилась к критическому порогу ({_config.CriticalStopTempC} °C).";
            WatchdogStatusText = warnMsg;
            AppendWatchdogEvent(warnMsg);
        }

        if (isRunning)
        {
            _minerStartedOrObservedAt ??= DateTimeOffset.UtcNow;
            var warmupPassed = DateTimeOffset.UtcNow - _minerStartedOrObservedAt.Value >= TimeSpan.FromSeconds(45);
            var hasPositiveHashrate = snapshot.MinerApiAvailable && snapshot.TotalHashrateTHs is double rate && rate > 0.01;

            if (hasPositiveHashrate)
            {
                _zeroHashrateSince = null;
                if (!_thermalPaused && maxCoreTemp < _config.WarningTempC)
                    WatchdogStatusText = $"Watchdog: норма · пиковая темп. {maxCoreTemp:0} °C · рестартов: {_watchdogRestartCount}";
            }
            else if (warmupPassed)
            {
                _zeroHashrateSince ??= DateTimeOffset.UtcNow;
                var zeroElapsed = DateTimeOffset.UtcNow - _zeroHashrateSince.Value;
                if (zeroElapsed >= TimeSpan.FromSeconds(_config.ZeroHashrateRestartSeconds))
                {
                    _zeroHashrateSince = null;
                    _watchdogRestartCount++;
                    var tdrEvent = await WindowsDiagnosticsService.CheckRecentNvidiaDriverResetAsync(_lifetime?.Token ?? CancellationToken.None);
                    if (!string.IsNullOrWhiteSpace(tdrEvent))
                        AppendWatchdogEvent($"Диагностика Windows (nvlddmkm/4101): {tdrEvent}");

                    var restartMsg = $"Watchdog: 0 TH/s в течение {(int)zeroElapsed.TotalSeconds} с. Перезапуск OneZeroMiner (#{_watchdogRestartCount}).";
                    WatchdogStatusText = restartMsg;
                    AppendWatchdogEvent(restartMsg);
                    await RecordCsvSnapshotAsync(restartMsg);
                    await SendTelegramAlertIfEnabledAsync($"⚠️ {restartMsg}{(string.IsNullOrWhiteSpace(tdrEvent) ? string.Empty : $"\nСобытие драйвера: {tdrEvent}")}");
                    await StopMinerAsync(manual: false);
                    await Task.Delay(1500, _lifetime?.Token ?? CancellationToken.None);
                    await StartMinerAsync(manual: false);
                }
                else
                {
                    var remaining = Math.Max(0, _config.ZeroHashrateRestartSeconds - (int)zeroElapsed.TotalSeconds);
                    WatchdogStatusText = $"Watchdog: 0 TH/s · до авто-перезапуска {remaining} с";
                }
            }
        }
    }

    private void Apply(TelemetrySnapshot snapshot)
    {
        _gpuDiscoveryCompleted = true;
        MinerApiStatus = snapshot.MinerApiMessage;
        HardwareStatus = snapshot.NvidiaMessage;
        PoolStatus = snapshot.PoolConnected switch { true => "Подключён", false => "Нет подключения", null => "Нет данных" };
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

        UpdateElectricityAndNetProfit(snapshot.SampledAt, hasPower ? totalPowerW : null);

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

        DetailStatus = string.Join("  ·  ", new[]
        {
            snapshot.MinerApiMessage,
            snapshot.NvidiaMessage,
            snapshot.Gpus.Count == 0 ? "Видеокарты не обнаружены" : $"Обнаружено GPU: {snapshot.Gpus.Count}"
        });

        var running = _minerControl.IsRunning;
        WindowsDiagnosticsService.SetMiningSleepPrevention(running && _config.PreventSleepWhenMining);
        if (!_isMinerBusy && !_isAutoTuning)
            MinerControlStatus = _thermalPaused
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
            ? (_thermalPaused ? TrayHealthState.Critical : TrayHealthState.Stopped)
            : (snapshot.TotalHashrateTHs is null or <= 0.01 ? TrayHealthState.Critical : maxTemp >= _config.WarningTempC ? TrayHealthState.Warning : TrayHealthState.MiningHealthy);

        TrayStatusChanged?.Invoke($"Pearl Rig · {TotalHashrate} · Макс {maxTemp:0}°C · {(running ? "Майнит" : "Остановлен")}", trayState);
    }

    private void UpdateElectricityAndNetProfit(DateTimeOffset sampledAt, double? totalPowerWatts)
    {
        var dayTariff = TryParseTariff(ElectricityRateDraft, out var parsedDay) ? parsedDay : _config.ElectricityRateRubPerKwh;
        var nightTariff = TryParseTariff(NightElectricityRateDraft, out var parsedNight) ? parsedNight : _config.NightElectricityRateRubPerKwh;
        var isNightNow = DualTariffEnabled && _config.IsNightTariffWindow(sampledAt.LocalDateTime);
        var activeTariff = isNightNow ? nightTariff : dayTariff;
        var zoneLabel = DualTariffEnabled ? (isNightNow ? $"Т2 Ночь {activeTariff:0.00} ₽" : $"Т1 День {activeTariff:0.00} ₽") : $"{activeTariff:0.00} ₽/кВт·ч";

        if (_lastEnergySampleAt is DateTimeOffset prev && totalPowerWatts is double watts && watts > 0)
        {
            var dtHours = (sampledAt - prev).TotalHours;
            if (dtHours is > 0 and < 0.1)
            {
                var deltaKwh = (watts / 1000d) * dtHours;
                _sessionEnergyKwh += deltaKwh;
                _sessionElectricityCostRub += deltaKwh * activeTariff;
            }
        }
        _lastEnergySampleAt = sampledAt;

        if (totalPowerWatts is double currentWatts && currentWatts > 0)
        {
            var dailyKwh = currentWatts * 24d / 1000d;
            var blendedTariff = DualTariffEnabled ? (dayTariff * 16d + nightTariff * 8d) / 24d : activeTariff;
            var dailyCostRub = dailyKwh * blendedTariff;
            ElectricitySummaryText = $"Сессия: {_sessionEnergyKwh:0.000} кВт·ч ({_sessionElectricityCostRub:0.00} ₽) · Сейчас: {zoneLabel} · Розетка: {dailyCostRub:0.00} ₽/сут";
            if (_lastObservedGrossRubPerDay is double grossRubPerDay)
            {
                var netRubPerDay = grossRubPerDay - dailyCostRub;
                _lastObservedNetRubPerDay = netRubPerDay;
                NetProfitSummaryText = $"Чистая прибыль: {netRubPerDay:0.00} ₽/сут (начисления {grossRubPerDay:0.00} ₽ − свет {dailyCostRub:0.00} ₽)";
            }
            else
            {
                NetProfitSummaryText = $"Расход на свет: {dailyCostRub:0.00} ₽/сут (чистая прибыль появится после расчёта темпа начислений PRL)";
            }
        }
        else
        {
            ElectricitySummaryText = $"Сессия: {_sessionEnergyKwh:0.000} кВт·ч ({_sessionElectricityCostRub:0.00} ₽) · Активный тариф: {zoneLabel}";
        }
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

        if (earnings.TotalPrl is double observedTotal)
        {
            if (_sessionFirstBalancePrl is null || _sessionFirstBalanceAt is null || observedTotal < _sessionFirstBalancePrl.Value)
            {
                _sessionFirstBalancePrl = observedTotal;
                _sessionFirstBalanceAt = earnings.FetchedAt;
                SessionAccumulationRate = "Фактическая скорость сессии: точка отсчёта зафиксирована";
            }
            else
            {
                var elapsedHours = (earnings.FetchedAt - _sessionFirstBalanceAt.Value).TotalHours;
                var deltaPrl = observedTotal - _sessionFirstBalancePrl.Value;
                if (elapsedHours >= 0.05 && deltaPrl > 0)
                {
                    var perDayPrl = deltaPrl / elapsedHours * 24d;
                    if (earnings.PrlRub is double rubRate)
                    {
                        _lastObservedGrossRubPerDay = perDayPrl * rubRate;
                        SessionAccumulationRate = $"За сессию: +{deltaPrl:0.######} PRL · темп {perDayPrl:0.####} PRL/сут (≈ {_lastObservedGrossRubPerDay:0.00} ₽/сут)";
                    }
                    else
                    {
                        SessionAccumulationRate = $"За сессию: +{deltaPrl:0.######} PRL · темп {perDayPrl:0.####} PRL/сут";
                    }
                }
            }
        }

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
            _sessionFirstBalancePrl = null;
            _sessionFirstBalanceAt = null;
            UpdateCommandLinePreviewFromDrafts();
            WalletStatus = "Публичный адрес сохранён только в локальном профиле Windows";
            await RefreshAsync(force: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WalletStatus = $"Не удалось сохранить адрес: {ex.Message}";
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
        WindowsDiagnosticsService.SetMiningSleepPrevention(false);
        _timer.Stop();
        _lifetime?.Cancel();
        _lifetime?.Dispose();
    }
}
