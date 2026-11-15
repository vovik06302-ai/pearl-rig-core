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
    private DateTimeOffset? _lastEnergySampleAt;
    private DateTimeOffset? _sessionFirstBalanceAt;
    private double? _sessionFirstBalancePrl;
    private double? _lastKnownPaidPrl;
    private double? _lastObservedGrossRubPerDay;
    private double _sessionEnergyKwh;
    private DateTimeOffset? _minerStartedOrObservedAt;
    private DateTimeOffset? _zeroHashrateSince;
    private DateTimeOffset _lastThermalWarningAt = DateTimeOffset.MinValue;
    private bool _userManuallyStopped;
    private bool _thermalPaused;
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
    private bool _telegramEnabled;
    private string _telegramBotTokenDraft;
    private string _telegramChatIdDraft;
    private string _telegramStatusText = "Уведомления Telegram не настроены";
    private double _powerSliderValue = 150;
    private double _powerSliderMinimum = 50;
    private double _powerSliderMaximum = 600;
    private bool _canAdjustPowerLimit;
    private bool _powerSliderInitialized;
    private string _powerRangeHint = "Ожидание диапазона мощности от драйвера NVIDIA…";
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
        RigConfig config)
    {
        _telemetry = telemetry;
        _earningsClient = earningsClient;
        _minerControl = minerControl;
        _updateService = updateService;
        _nvidiaSmi = nvidiaSmi;
        _telegramService = telegramService;
        _config = config;
        _installedVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(3, 6, 0);
        _autoStartMining = config.AutoStartMining;
        _watchdogEnabled = config.WatchdogEnabled;
        _smartFanCurveEnabled = config.SmartFanCurveEnabled;
        _activePreset = string.IsNullOrWhiteSpace(config.ActivePreset) ? "Auto" : config.ActivePreset;
        _workerNameDraft = config.WorkerName;
        _coreClockDraft = SettingDraft(config.CoreClock);
        _memoryClockDraft = SettingDraft(config.MemoryClock);
        _powerLimitDraft = SettingDraft(config.PowerLimit);
        _kernelDraft = SettingDraft(config.Kernel);
        _fanDraft = SettingDraft(config.Fan);
        _electricityRateDraft = config.ElectricityRateRubPerKwh.ToString("0.00", CultureInfo.InvariantCulture);
        _telegramEnabled = config.TelegramEnabled;
        _telegramBotTokenDraft = config.TelegramBotToken;
        _telegramChatIdDraft = config.TelegramChatId;
        _telegramStatusText = _telegramEnabled && !string.IsNullOrWhiteSpace(_telegramBotTokenDraft) && !string.IsNullOrWhiteSpace(_telegramChatIdDraft)
            ? "Telegram-уведомления активны"
            : "Введите токен бота и Chat ID для алертов";
        _walletAddress = config.WalletAddress;
        _walletAddressDraft = config.WalletAddress;
        _walletStatus = PoolEarningsClient.IsValidWallet(config.WalletAddress) ? "Адрес выплат загружен из локального профиля" : "Введите публичный адрес PRL";
        _minerCommandLinePreview = _minerControl.GetCommandLinePreview(_walletAddressDraft);

        SaveWalletCommand = new RelayCommand(() => _ = SaveWalletAsync(), () => PoolEarningsClient.IsValidWallet(WalletAddressDraft));
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(force: true));
        StartMinerCommand = new RelayCommand(() => _ = StartMinerAsync(manual: true), () => !_isMinerBusy && !_minerControl.IsRunning);
        StopMinerCommand = new RelayCommand(() => _ = StopMinerAsync(manual: true), () => !_isMinerBusy && _minerControl.IsRunning);
        ToggleMinerModeCommand = new RelayCommand(() => _ = ToggleMinerModeAsync(), () => !_isMinerBusy);
        ToggleWatchdogCommand = new RelayCommand(() => _ = ToggleWatchdogAsync(), () => !_isMinerBusy);
        ToggleSmartFanCurveCommand = new RelayCommand(() => _ = ToggleSmartFanCurveAsync(), () => !_isMinerBusy);
        PresetAutoCommand = new RelayCommand(() => ApplyPreset("Auto"), () => !_isMinerBusy);
        PresetEcoCommand = new RelayCommand(() => ApplyPreset("Eco"), () => !_isMinerBusy);
        PresetBalanceCommand = new RelayCommand(() => ApplyPreset("Balance"), () => !_isMinerBusy);
        PresetTurboCommand = new RelayCommand(() => ApplyPreset("Turbo"), () => !_isMinerBusy);
        PresetSilentCommand = new RelayCommand(() => ApplyPreset("Silent"), () => !_isMinerBusy);
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
    public RelayCommand PresetAutoCommand { get; }
    public RelayCommand PresetEcoCommand { get; }
    public RelayCommand PresetBalanceCommand { get; }
    public RelayCommand PresetTurboCommand { get; }
    public RelayCommand PresetSilentCommand { get; }
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
            MinerCommandLinePreview = _minerControl.GetCommandLinePreview(_walletAddressDraft);
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
    public string ActivePreset { get => _activePreset; private set { if (Set(ref _activePreset, value)) OnPropertyChanged(nameof(ActivePresetLabel)); } }
    public string ActivePresetLabel => $"ПРОФИЛЬ: {_activePreset.ToUpperInvariant()}";
    public string WatchdogStatusText { get => _watchdogStatusText; private set => Set(ref _watchdogStatusText, value); }
    public string WatchdogEventsText { get => _watchdogEventsText; private set => Set(ref _watchdogEventsText, value); }
    public string MinerLogTail { get => _minerLogTail; private set => Set(ref _minerLogTail, value); }
    public string MinerCommandLinePreview { get => _minerCommandLinePreview; private set => Set(ref _minerCommandLinePreview, value); }
    public string WorkerNameDraft { get => _workerNameDraft; set { if (Set(ref _workerNameDraft, value)) SaveMinerSettingsCommand.NotifyCanExecuteChanged(); } }
    public string CoreClockDraft { get => _coreClockDraft; set { if (Set(ref _coreClockDraft, value)) SaveMinerSettingsCommand.NotifyCanExecuteChanged(); } }
    public string MemoryClockDraft { get => _memoryClockDraft; set { if (Set(ref _memoryClockDraft, value)) SaveMinerSettingsCommand.NotifyCanExecuteChanged(); } }

    public string PowerLimitDraft
    {
        get => _powerLimitDraft;
        set
        {
            if (!Set(ref _powerLimitDraft, value)) return;
            SaveMinerSettingsCommand.NotifyCanExecuteChanged();
            if (int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var watts))
                Set(ref _powerSliderValue, Math.Clamp(watts, PowerSliderMinimum, PowerSliderMaximum), nameof(PowerSliderValue));
        }
    }

    public double PowerSliderValue
    {
        get => _powerSliderValue;
        set
        {
            var watts = Math.Clamp(value, PowerSliderMinimum, PowerSliderMaximum);
            if (!Set(ref _powerSliderValue, watts)) return;
            var valueText = Math.Round(watts).ToString(CultureInfo.InvariantCulture);
            PowerLimitDraft = string.Join(',', Gpus.OrderBy(gpu => gpu.Id).Select(_ => valueText));
        }
    }

    public double PowerSliderMinimum { get => _powerSliderMinimum; private set => Set(ref _powerSliderMinimum, value); }
    public double PowerSliderMaximum { get => _powerSliderMaximum; private set => Set(ref _powerSliderMaximum, value); }
    public bool CanAdjustPowerLimit { get => _canAdjustPowerLimit; private set => Set(ref _canAdjustPowerLimit, value); }
    public string PowerRangeHint { get => _powerRangeHint; private set => Set(ref _powerRangeHint, value); }
    public string KernelDraft { get => _kernelDraft; set { if (Set(ref _kernelDraft, value)) SaveMinerSettingsCommand.NotifyCanExecuteChanged(); } }
    public string FanDraft { get => _fanDraft; set { if (Set(ref _fanDraft, value)) SaveMinerSettingsCommand.NotifyCanExecuteChanged(); } }
    public string ElectricityRateDraft { get => _electricityRateDraft; set { if (Set(ref _electricityRateDraft, value)) SaveMinerSettingsCommand.NotifyCanExecuteChanged(); } }
    public bool TelegramEnabled { get => _telegramEnabled; set => Set(ref _telegramEnabled, value); }
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

    public void Start()
    {
        _lifetime = new CancellationTokenSource();
        _timer.Start();
        UpdateMinerState();
        _ = RefreshAsync();
        _ = CheckForUpdatesAsync();
    }

    public void ApplyPresetFromTray(string presetName)
    {
        ApplyPreset(presetName);
        _ = SaveMinerSettingsAsync();
    }

    private void ApplyPreset(string presetName)
    {
        var count = Math.Max(1, Gpus.Count);
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
        MinerControlStatus = $"Выбран пресет «{presetName}». Нажмите «СОХРАНИТЬ И ПРИМЕНИТЬ» для записи в драйвер.";
        AppendWatchdogEvent($"Подготовлен профиль разгона: {presetName}");
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
        if (_watchdogEvents.Count > 30)
            _watchdogEvents.RemoveAt(0);
        WatchdogEventsText = string.Join(Environment.NewLine, Enumerable.Reverse(_watchdogEvents));
    }

    private async Task SendTelegramAlertIfEnabledAsync(string message)
    {
        if (!TelegramEnabled || string.IsNullOrWhiteSpace(TelegramBotTokenDraft) || string.IsNullOrWhiteSpace(TelegramChatIdDraft))
            return;
        var (ok, status) = await _telegramService.SendAsync(TelegramBotTokenDraft, TelegramChatIdDraft, message, bypassCooldown: false, _lifetime?.Token ?? CancellationToken.None);
        TelegramStatusText = status;
        if (!ok) AppendWatchdogEvent($"Telegram: {status}");
    }

    private async Task SaveTelegramSettingsAsync()
    {
        try
        {
            await RigConfig.SaveTelegramSecretsAsync(TelegramBotTokenDraft, TelegramChatIdDraft, _lifetime?.Token ?? CancellationToken.None);
            _config = _config with
            {
                TelegramEnabled = TelegramEnabled,
                TelegramBotToken = TelegramBotTokenDraft.Trim(),
                TelegramChatId = TelegramChatIdDraft.Trim()
            };
            await RigConfig.SaveLocalSettingsAsync(_config, _lifetime?.Token ?? CancellationToken.None);
            TelegramStatusText = TelegramEnabled
                ? "Настройки Telegram сохранены в локальном профиле"
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
        var summary = $"Тестовое уведомление.\nХешрейт: {TotalHashrate}\nПул: {PoolStatus}\nБаланс: {PoolBalance} ({PoolRubEstimate})";
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
        UpdateStatusText = "Проверяю GitHub Releases…";
        OnPropertyChanged(nameof(UpdateButtonText));
        UpdateCommand.NotifyCanExecuteChanged();
        try
        {
            _availableUpdate = await _updateService.CheckAsync(_installedVersion, _lifetime?.Token ?? CancellationToken.None);
            OnPropertyChanged(nameof(UpdateAvailable));
            OnPropertyChanged(nameof(UpdateButtonText));
            UpdateStatusText = _availableUpdate is null
                ? $"Установлена v{_installedVersion.ToString(3)} · новых релизов нет"
                : $"Доступна новая версия v{_availableUpdate.Version}";
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            UpdateStatusText = $"Не удалось проверить обновление: {ex.Message}";
        }
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
            if (!_gpuDiscoveryCompleted)
            {
                var snapshot = await _telemetry.ReadAsync(_lifetime?.Token ?? CancellationToken.None);
                Apply(snapshot);
            }

            var detectedGpus = Gpus.ToArray();
            var nvidiaGpus = detectedGpus.Where(IsNvidiaGpu).ToArray();
            if (detectedGpus.Length > 0 && nvidiaGpus.Length == 0)
            {
                var names = string.Join(", ", detectedGpus.Select(gpu => gpu.Name));
                throw new InvalidOperationException($"Обнаружены {names}. PearlHash в OneZeroMiner поддерживает только видеокарты NVIDIA.");
            }

            var wallet = WalletAddressDraft.Trim();
            if (!PoolEarningsClient.IsValidWallet(wallet))
                throw new InvalidOperationException("Сначала введите и сохраните корректный адрес PRL.");
            if (!string.Equals(wallet, _config.WalletAddress, StringComparison.Ordinal))
            {
                await RigConfig.SaveWalletAsync(wallet, _lifetime?.Token ?? CancellationToken.None);
                WalletStatus = "Публичный адрес сохранён только в локальном профиле Windows";
            }
            await _minerControl.StartAsync(wallet, _lifetime?.Token ?? CancellationToken.None);
            WindowsDiagnosticsService.SetMiningSleepPrevention(_config.PreventSleepWhenMining);
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
        MinerControlStatus = enabled ? "Настройка запуска с Windows…" : "Отключение запуска с Windows…";
        try
        {
            await _minerControl.SetAutoStartAsync(enabled, _lifetime?.Token ?? CancellationToken.None);
            _config = _config with { AutoStartMining = enabled };
            await RigConfig.SaveLocalSettingsAsync(_config, _lifetime?.Token ?? CancellationToken.None);
            AutoStartMining = enabled;
            MinerControlStatus = enabled
                ? "Автозапуск включён: майнер стартует при входе в Windows"
                : "Ручной режим: майнер запускается только кнопкой";
        }
        catch (OperationCanceledException) { MinerControlStatus = "Изменение режима отменено"; }
        catch (Exception ex) { MinerControlStatus = $"Режим не изменён: {ex.Message}"; }
        finally { EndMinerAction(); }
    }

    private bool CanSaveMinerSettings() => !_isMinerBusy
        && System.Text.RegularExpressions.Regex.IsMatch(WorkerNameDraft.Trim(), "^[A-Za-z0-9._-]{1,32}$")
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
        && TryParseTariff(ElectricityRateDraft, out _);

    private static bool TryParseTariff(string text, out double rate)
    {
        var normalized = (text ?? string.Empty).Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out rate) && rate >= 0 && rate <= 100;
    }

    private bool IsGpuListLengthValid(string text) => string.IsNullOrWhiteSpace(text)
        || text.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
        || text.Trim().Equals("авто", StringComparison.OrdinalIgnoreCase)
        || Gpus.Count == 0
        || text.Split(',', StringSplitOptions.TrimEntries).Length == Gpus.Count;

    private static bool IsGpuListValid(string text, int min, int max, bool allowAuto = false)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) || text.Trim().Equals("авто", StringComparison.OrdinalIgnoreCase)) return true;
        var values = text.Split(',', StringSplitOptions.TrimEntries);
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
            _ = TryParseTariff(ElectricityRateDraft, out var parsedTariff);
            _config = _config with
            {
                WorkerName = WorkerNameDraft.Trim(),
                CoreClock = NormalizeGpuSetting(CoreClockDraft),
                MemoryClock = NormalizeGpuSetting(MemoryClockDraft),
                PowerLimit = NormalizeGpuSetting(PowerLimitDraft),
                Kernel = NormalizeGpuSetting(KernelDraft),
                Fan = NormalizeGpuSetting(FanDraft),
                ActivePreset = ActivePreset,
                ElectricityRateRubPerKwh = parsedTariff
            };
            await RigConfig.SaveLocalSettingsAsync(_config, _lifetime?.Token ?? CancellationToken.None);
            _minerControl.UpdateConfig(_config);
            MinerCommandLinePreview = _minerControl.GetCommandLinePreview(_walletAddressDraft);
            try
            {
                var applied = await _nvidiaSmi.ApplyPowerLimitsAsync(_config.PowerLimit, _lifetime?.Token ?? CancellationToken.None);
                MinerControlStatus = $"Лимит применён: {applied}. Тариф {parsedTariff:0.00} ₽/кВт·ч сохранён.";
                AppendWatchdogEvent($"Применён Power Limit NVIDIA: {applied}");
            }
            catch (OperationCanceledException)
            {
                MinerControlStatus = "Профиль сохранён; применение мощности отменено в запросе Windows.";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                MinerControlStatus = $"Профиль сохранён, но лимит мощности не применён: {ex.Message}";
            }
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
        StartMinerCommand.NotifyCanExecuteChanged();
        StopMinerCommand.NotifyCanExecuteChanged();
        ToggleMinerModeCommand.NotifyCanExecuteChanged();
        ToggleWatchdogCommand.NotifyCanExecuteChanged();
        ToggleSmartFanCurveCommand.NotifyCanExecuteChanged();
        SaveMinerSettingsCommand.NotifyCanExecuteChanged();
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
        ToggleWatchdogCommand.NotifyCanExecuteChanged();
        ToggleSmartFanCurveCommand.NotifyCanExecuteChanged();
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
            var snapshot = await _telemetry.ReadAsync(_lifetime?.Token ?? CancellationToken.None);
            Apply(snapshot);
            ApplySmartFanCurveIfEnabled(snapshot.Gpus);
            MinerLogTail = _minerControl.ReadRecentLogLines(14);
            await EvaluateWatchdogAndThermalProtectionAsync(snapshot);

            if (PoolEarningsClient.IsValidWallet(_walletAddress) && DateTimeOffset.UtcNow - _lastEarningsRead >= TimeSpan.FromSeconds(_earningsRefreshSeconds))
            {
                var earnings = await _earningsClient.ReadAsync(_walletAddress, _lifetime?.Token ?? CancellationToken.None);
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
                < 50 => 40,
                < 60 => 52,
                < 68 => 64,
                < 75 => 78,
                < 80 => 90,
                _ => 100
            };

            if (_lastSmartFanApplied.TryGetValue(gpu.Id, out var prevFan) && Math.Abs(desiredFan - prevFan) < 6 && desiredFan < 100)
                continue;

            _lastSmartFanApplied[gpu.Id] = desiredFan;
            targets.Add((gpu.Id, desiredFan));
        }

        if (targets.Count > 0)
            _ = _nvidiaSmi.TryApplySmartFansDirect(targets);
    }

    private async Task EvaluateWatchdogAndThermalProtectionAsync(TelemetrySnapshot snapshot)
    {
        if (!WatchdogEnabled || _isMinerBusy || _disposed) return;

        var maxCoreTemp = snapshot.Gpus.Where(g => g.TempC is not null).Select(g => g.TempC!.Value).DefaultIfEmpty(0).Max();
        var maxVramTemp = snapshot.Gpus.Where(g => g.VramTempC is not null).Select(g => g.VramTempC!.Value).DefaultIfEmpty(0).Max();
        var isRunning = _minerControl.IsRunning;

        if (isRunning && (maxCoreTemp >= _config.CriticalStopTempC || maxVramTemp >= 106))
        {
            _thermalPaused = true;
            var reason = maxVramTemp >= 106
                ? $"АВАРИЙНЫЙ СТОП: температура памяти GDDR6X достигла {maxVramTemp:0} °C"
                : $"АВАРИЙНЫЙ СТОП: температура GPU достигла {maxCoreTemp:0} °C (порог {_config.CriticalStopTempC} °C)";
            WatchdogStatusText = $"{reason}. Ожидание остывания до ≤ {_config.ResumeTempC} °C.";
            AppendWatchdogEvent(reason);
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

        if (snapshot.TotalHashrateTHs is double currentRate && double.IsFinite(currentRate))
        {
            AppendSample(_totalHashrateBuffer, currentRate);
            TotalHashrateHistory = _totalHashrateBuffer.ToArray();
        }

        var totalPowerW = snapshot.Gpus.Where(g => g.PowerWatts is not null).Sum(g => g.PowerWatts!.Value);
        var hasPower = snapshot.Gpus.Any(g => g.PowerWatts is not null);
        if (snapshot.TotalHashrateTHs is double totalThs && totalThs > 0 && hasPower && totalPowerW > 0)
        {
            var ghPerWatt = (totalThs * 1000d) / totalPowerW;
            TotalEfficiency = $"Эффективность: {ghPerWatt:0.00} GH/W · Потребление: {totalPowerW:0} Вт";
        }
        else if (hasPower)
        {
            TotalEfficiency = $"Эффективность: — GH/W · Потребление: {totalPowerW:0} Вт";
        }
        else
        {
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
        if (!_isMinerBusy)
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
        if (ordered.Length > 0 && powerCards.Length == ordered.Length)
        {
            var minimum = Math.Ceiling(powerCards.Max(gpu => gpu.MinPowerLimitWatts!.Value));
            var maximum = Math.Floor(powerCards.Min(gpu => gpu.MaxPowerLimitWatts!.Value));
            if (minimum <= maximum)
            {
                PowerSliderMinimum = minimum;
                PowerSliderMaximum = maximum;
                CanAdjustPowerLimit = true;
                PowerRangeHint = $"Диапазон драйвера для всех {ordered.Length} карт: {minimum:0}–{maximum:0} Вт. Ползунок задаёт одинаковый лимит; раздельные значения — в поле по ID GPU.";
                if (!_powerSliderInitialized)
                {
                    var firstDraft = PowerLimitDraft.Split(',', StringSplitOptions.TrimEntries).FirstOrDefault();
                    var initial = int.TryParse(firstDraft, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : ordered[0].PowerLimitWatts ?? minimum;
                    _powerSliderInitialized = true;
                    Set(ref _powerSliderValue, Math.Clamp(initial, minimum, maximum), nameof(PowerSliderValue));
                }
                else
                {
                    Set(ref _powerSliderValue, Math.Clamp(PowerSliderValue, minimum, maximum), nameof(PowerSliderValue));
                }
            }
            else
            {
                CanAdjustPowerLimit = false;
                PowerRangeHint = "У карт нет общего диапазона мощности; задайте отдельные значения в поле по ID GPU.";
            }
        }
        else
        {
            CanAdjustPowerLimit = false;
            PowerRangeHint = "Ползунок включится после получения допустимого диапазона NVIDIA для всех карт.";
        }

        OnPropertyChanged(nameof(HasNoGpus));
        OnPropertyChanged(nameof(GpuCountLabel));
        OnPropertyChanged(nameof(MaxGpuHashrateTHs));

        var maxTemp = ordered.Where(g => g.TempC is not null).Select(g => g.TempC!.Value).DefaultIfEmpty(0).Max();
        var trayState = !running
            ? (_thermalPaused ? TrayHealthState.Critical : TrayHealthState.Stopped)
            : (snapshot.TotalHashrateTHs is null or <= 0.01 ? TrayHealthState.Critical : maxTemp >= _config.WarningTempC ? TrayHealthState.Warning : TrayHealthState.MiningHealthy);

        TrayStatusChanged?.Invoke($"Pearl Rig · {TotalHashrate} · Макс {maxTemp:0}°C · {(running ? "Майнит" : "Остановлен")}", trayState);
    }

    private void UpdateElectricityAndNetProfit(DateTimeOffset sampledAt, double? totalPowerWatts)
    {
        var tariff = TryParseTariff(ElectricityRateDraft, out var parsed) ? parsed : _config.ElectricityRateRubPerKwh;
        if (_lastEnergySampleAt is DateTimeOffset prev && totalPowerWatts is double watts && watts > 0)
        {
            var dtHours = (sampledAt - prev).TotalHours;
            if (dtHours is > 0 and < 0.1)
                _sessionEnergyKwh += (watts / 1000d) * dtHours;
        }
        _lastEnergySampleAt = sampledAt;

        var sessionCostRub = _sessionEnergyKwh * tariff;
        if (totalPowerWatts is double currentWatts && currentWatts > 0)
        {
            var dailyKwh = currentWatts * 24d / 1000d;
            var dailyCostRub = dailyKwh * tariff;
            ElectricitySummaryText = $"Сессия: {_sessionEnergyKwh:0.000} кВт·ч ({sessionCostRub:0.00} ₽) · Розетка: {dailyCostRub:0.00} ₽/сут ({dailyKwh:0.1} кВт·ч/сут)";
            if (_lastObservedGrossRubPerDay is double grossRubPerDay)
            {
                var netRubPerDay = grossRubPerDay - dailyCostRub;
                NetProfitSummaryText = $"Чистая прибыль: {netRubPerDay:0.00} ₽/сут (начисления {grossRubPerDay:0.00} ₽ − свет {dailyCostRub:0.00} ₽)";
            }
            else
            {
                NetProfitSummaryText = $"Расход на свет: {dailyCostRub:0.00} ₽/сут (чистая прибыль появится после расчёта темпа начислений PRL)";
            }
        }
        else
        {
            ElectricitySummaryText = $"Сессия: {_sessionEnergyKwh:0.000} кВт·ч ({sessionCostRub:0.00} ₽) · Тариф: {tariff:0.00} ₽/кВт·ч";
        }
    }

    private async Task ApplyEarningsAsync(PoolEarningsSnapshot earnings)
    {
        PoolBalanceStatus = earnings.Message;
        PoolBalance = FormatPrl(earnings.TotalPrl);
        PoolConfirmed = FormatPrl(earnings.ConfirmedPrl);
        PoolUnconfirmed = FormatPrl(earnings.UnconfirmedPrl);
        PoolPaid = FormatPrl(earnings.PaidPrl);
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
        var wallet = WalletAddressDraft.Trim();
        if (!PoolEarningsClient.IsValidWallet(wallet))
        {
            WalletStatus = "Адрес PRL должен содержать 63 допустимых символа";
            return;
        }

        try
        {
            await RigConfig.SaveWalletAsync(wallet, _lifetime?.Token ?? CancellationToken.None);
            _walletAddress = wallet;
            _lastEarningsRead = DateTimeOffset.MinValue;
            _sessionFirstBalancePrl = null;
            _sessionFirstBalanceAt = null;
            MinerCommandLinePreview = _minerControl.GetCommandLinePreview(wallet);
            WalletStatus = "Публичный адрес сохранён только в локальном профиле Windows";
            await RefreshAsync(force: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WalletStatus = $"Не удалось сохранить адрес: {ex.Message}";
        }
    }

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

    private static string NormalizeGpuSetting(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) || value.Trim().Equals("авто", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : string.Join(',', value.Split(',', StringSplitOptions.TrimEntries));

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
