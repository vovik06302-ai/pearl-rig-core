using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using PearlRigCore.Models;
using PearlRigCore.Services;

namespace PearlRigCore.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly TelemetryService _telemetry;
    private readonly PoolEarningsClient _earningsClient;
    private readonly MinerControlService _minerControl;
    private readonly UpdateService _updateService;
    private readonly NvidiaSmiClient _nvidiaSmi;
    private RigConfig _config;
    private readonly int _earningsRefreshSeconds = 60;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _lifetime;
    private DateTimeOffset _lastEarningsRead = DateTimeOffset.MinValue;
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
    private string _acceptedShares = "Нет данных";
    private string _rejectedShares = "Нет данных";
    private string _lastUpdated = "Ожидание первого опроса";
    private string _detailStatus = "Показания появятся после ответа локальных источников.";
    private string _walletAddressDraft;
    private string _walletStatus;
    private string _poolBalanceStatus = "Баланс ещё не загружался";
    private string _poolBalance = "Нет данных";
    private string _poolConfirmed = "Нет данных";
    private string _poolUnconfirmed = "Нет данных";
    private string _poolPaid = "Нет данных";
    private string _poolRubEstimate = "Нет данных";
    private string _marketRate = "Курс PRL/RUB недоступен";
    private string _poolUpdated = "Нет данных";
    private bool _isMinerBusy;
    private bool _gpuDiscoveryCompleted;
    private bool _autoStartMining;
    private string _minerControlStatus = "Проверка майнера…";
    private string _workerNameDraft;
    private string _coreClockDraft;
    private string _memoryClockDraft;
    private string _powerLimitDraft;
    private string _kernelDraft;
    private string _fanDraft;
    private double _powerSliderValue = 150;
    private double _powerSliderMinimum = 50;
    private double _powerSliderMaximum = 600;
    private bool _canAdjustPowerLimit;
    private bool _powerSliderInitialized;
    private string _powerRangeHint = "Ожидание диапазона мощности от драйвера NVIDIA…";
    private ReleaseUpdate? _availableUpdate;
    private Version _installedVersion;
    private bool _isCheckingUpdate;
    private bool _isDownloadingUpdate;
    private double _updateProgress;
    private string _updateStatusText = "Проверка версии…";

    public MainViewModel(TelemetryService telemetry, PoolEarningsClient earningsClient, MinerControlService minerControl, UpdateService updateService, NvidiaSmiClient nvidiaSmi, RigConfig config)
    {
        _telemetry = telemetry;
        _earningsClient = earningsClient;
        _minerControl = minerControl;
        _updateService = updateService;
        _nvidiaSmi = nvidiaSmi;
        _config = config;
        _installedVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(3, 4, 1);
        _autoStartMining = config.AutoStartMining;
        _workerNameDraft = config.WorkerName;
        _coreClockDraft = SettingDraft(config.CoreClock);
        _memoryClockDraft = SettingDraft(config.MemoryClock);
        _powerLimitDraft = SettingDraft(config.PowerLimit);
        _kernelDraft = SettingDraft(config.Kernel);
        _fanDraft = SettingDraft(config.Fan);
        _walletAddress = config.WalletAddress;
        _walletAddressDraft = config.WalletAddress;
        _walletStatus = PoolEarningsClient.IsValidWallet(config.WalletAddress) ? "Адрес выплат загружен из локального профиля" : "Введите публичный адрес PRL";
        SaveWalletCommand = new RelayCommand(() => _ = SaveWalletAsync(), () => PoolEarningsClient.IsValidWallet(WalletAddressDraft));
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(force: true));
        StartMinerCommand = new RelayCommand(() => _ = StartMinerAsync(), () => !_isMinerBusy && !_minerControl.IsRunning);
        StopMinerCommand = new RelayCommand(() => _ = StopMinerAsync(), () => !_isMinerBusy && _minerControl.IsRunning);
        ToggleMinerModeCommand = new RelayCommand(() => _ = ToggleMinerModeAsync(), () => !_isMinerBusy);
        SaveMinerSettingsCommand = new RelayCommand(() => _ = SaveMinerSettingsAsync(), CanSaveMinerSettings);
        UpdateCommand = new RelayCommand(() => _ = UpdateOrCheckAsync(), () => !_isCheckingUpdate && !_isDownloadingUpdate);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<GpuLiveMetrics> Gpus { get; } = [];
    public RelayCommand RefreshCommand { get; }
    public RelayCommand SaveWalletCommand { get; }
    public RelayCommand StartMinerCommand { get; }
    public RelayCommand StopMinerCommand { get; }
    public RelayCommand ToggleMinerModeCommand { get; }
    public RelayCommand SaveMinerSettingsCommand { get; }
    public RelayCommand UpdateCommand { get; }
    public event Action<string>? InstallerDownloaded;
    public void SetUpdateStatus(string message) => UpdateStatusText = message;

    public string ConnectionStatus { get => _connectionStatus; private set => Set(ref _connectionStatus, value); }
    public string MinerApiStatus { get => _minerApiStatus; private set => Set(ref _minerApiStatus, value); }
    public string HardwareStatus { get => _hardwareStatus; private set => Set(ref _hardwareStatus, value); }
    public string PoolStatus { get => _poolStatus; private set => Set(ref _poolStatus, value); }
    public string PoolUrl { get => _poolUrl; private set => Set(ref _poolUrl, value); }
    public string TotalHashrate { get => _totalHashrate; private set => Set(ref _totalHashrate, value); }
    public string AcceptedShares { get => _acceptedShares; private set => Set(ref _acceptedShares, value); }
    public string RejectedShares { get => _rejectedShares; private set => Set(ref _rejectedShares, value); }
    public string LastUpdated { get => _lastUpdated; private set => Set(ref _lastUpdated, value); }
    public string DetailStatus { get => _detailStatus; private set => Set(ref _detailStatus, value); }
    public string WalletAddressDraft { get => _walletAddressDraft; set { if (Set(ref _walletAddressDraft, value)) SaveWalletCommand.NotifyCanExecuteChanged(); } }
    public string WalletStatus { get => _walletStatus; private set => Set(ref _walletStatus, value); }
    public string PoolBalanceStatus { get => _poolBalanceStatus; private set => Set(ref _poolBalanceStatus, value); }
    public string PoolBalance { get => _poolBalance; private set => Set(ref _poolBalance, value); }
    public string PoolConfirmed { get => _poolConfirmed; private set => Set(ref _poolConfirmed, value); }
    public string PoolUnconfirmed { get => _poolUnconfirmed; private set => Set(ref _poolUnconfirmed, value); }
    public string PoolPaid { get => _poolPaid; private set => Set(ref _poolPaid, value); }
    public string PoolRubEstimate { get => _poolRubEstimate; private set => Set(ref _poolRubEstimate, value); }
    public string MarketRate { get => _marketRate; private set => Set(ref _marketRate, value); }
    public string PoolUpdated { get => _poolUpdated; private set => Set(ref _poolUpdated, value); }
    public bool IsRefreshing { get => _isRefreshing; private set => Set(ref _isRefreshing, value); }
    public bool IsMinerRunning => _minerControl.IsRunning;
    public string MinerControlStatus { get => _minerControlStatus; private set => Set(ref _minerControlStatus, value); }
    public bool AutoStartMining { get => _autoStartMining; private set { if (Set(ref _autoStartMining, value)) OnPropertyChanged(nameof(MinerModeLabel)); } }
    public string MinerModeLabel => AutoStartMining ? "АВТОЗАПУСК" : "ВРУЧНУЮ";
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
            if (int.TryParse(value.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var watts))
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
            var valueText = Math.Round(watts).ToString(System.Globalization.CultureInfo.InvariantCulture);
            PowerLimitDraft = string.Join(',', Gpus.OrderBy(gpu => gpu.Id).Select(_ => valueText));
        }
    }
    public double PowerSliderMinimum { get => _powerSliderMinimum; private set => Set(ref _powerSliderMinimum, value); }
    public double PowerSliderMaximum { get => _powerSliderMaximum; private set => Set(ref _powerSliderMaximum, value); }
    public bool CanAdjustPowerLimit { get => _canAdjustPowerLimit; private set => Set(ref _canAdjustPowerLimit, value); }
    public string PowerRangeHint { get => _powerRangeHint; private set => Set(ref _powerRangeHint, value); }
    public string KernelDraft { get => _kernelDraft; set { if (Set(ref _kernelDraft, value)) SaveMinerSettingsCommand.NotifyCanExecuteChanged(); } }
    public string FanDraft { get => _fanDraft; set { if (Set(ref _fanDraft, value)) SaveMinerSettingsCommand.NotifyCanExecuteChanged(); } }
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

    private async Task StartMinerAsync()
    {
        if (!TryBeginMinerAction()) return;
        MinerControlStatus = "Запуск OneZeroMiner…";
        try
        {
            var wallet = WalletAddressDraft.Trim();
            if (!PoolEarningsClient.IsValidWallet(wallet))
                throw new InvalidOperationException("Сначала введите и сохраните корректный адрес PRL.");
            if (!string.Equals(wallet, _config.WalletAddress, StringComparison.Ordinal))
            {
                await RigConfig.SaveWalletAsync(wallet, _lifetime?.Token ?? CancellationToken.None);
                WalletStatus = "Публичный адрес сохранён только в локальном профиле Windows";
            }
            await _minerControl.StartAsync(wallet, _lifetime?.Token ?? CancellationToken.None);
            MinerControlStatus = "Майнер запущен";
            _ = RefreshAsync(force: true);
        }
        catch (OperationCanceledException) { MinerControlStatus = "Запуск отменён"; }
        catch (Exception ex) { MinerControlStatus = $"Не запущен: {ex.Message}"; }
        finally { EndMinerAction(); }
    }

    private async Task StopMinerAsync()
    {
        if (!TryBeginMinerAction()) return;
        MinerControlStatus = "Остановка: отправка штатного Ctrl+C…";
        try
        {
            await _minerControl.StopAsync(_lifetime?.Token ?? CancellationToken.None);
            MinerControlStatus = "Майнер остановлен штатно";
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
        && IsGpuListLengthValid(FanDraft);

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
            (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                && number >= min && number <= max));
    }

    private async Task SaveMinerSettingsAsync()
    {
        if (!CanSaveMinerSettings() || !TryBeginMinerAction()) return;
        try
        {
            _config = _config with
            {
                WorkerName = WorkerNameDraft.Trim(),
                CoreClock = NormalizeGpuSetting(CoreClockDraft),
                MemoryClock = NormalizeGpuSetting(MemoryClockDraft),
                PowerLimit = NormalizeGpuSetting(PowerLimitDraft),
                Kernel = NormalizeGpuSetting(KernelDraft),
                Fan = NormalizeGpuSetting(FanDraft)
            };
            await RigConfig.SaveLocalSettingsAsync(_config, _lifetime?.Token ?? CancellationToken.None);
            _minerControl.UpdateConfig(_config);
            try
            {
                var applied = await _nvidiaSmi.ApplyPowerLimitsAsync(_config.PowerLimit, _lifetime?.Token ?? CancellationToken.None);
                MinerControlStatus = $"Лимит применён драйвером: {applied}. Фактическое потребление зависит от нагрузки; остальные настройки майнера применятся при следующем запуске.";
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

            if (PoolEarningsClient.IsValidWallet(_walletAddress) && DateTimeOffset.UtcNow - _lastEarningsRead >= TimeSpan.FromSeconds(_earningsRefreshSeconds))
            {
                var earnings = await _earningsClient.ReadAsync(_walletAddress, _lifetime?.Token ?? CancellationToken.None);
                ApplyEarnings(earnings);
                _lastEarningsRead = DateTimeOffset.UtcNow;
            }
            else if (!PoolEarningsClient.IsValidWallet(_walletAddress))
            {
                PoolBalanceStatus = "Адрес выплат не настроен";
                PoolBalance = "Нет данных";
                PoolConfirmed = "Нет данных";
                PoolUnconfirmed = "Нет данных";
                PoolPaid = "Нет данных";
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

    private void Apply(TelemetrySnapshot snapshot)
    {
        _gpuDiscoveryCompleted = true;
        MinerApiStatus = snapshot.MinerApiMessage;
        HardwareStatus = snapshot.NvidiaMessage;
        PoolStatus = snapshot.PoolConnected switch { true => "Подключён", false => "Нет подключения", null => "Нет данных" };
        PoolUrl = snapshot.PoolUrl ?? "Адрес пула недоступен";
        TotalHashrate = snapshot.TotalHashrateTHs is double rate ? $"{rate:0.00} TH/s" : "Нет данных";
        AcceptedShares = snapshot.AcceptedShares?.ToString("N0") ?? "Нет данных";
        RejectedShares = snapshot.RejectedShares?.ToString("N0") ?? "Нет данных";
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
        if (!_isMinerBusy)
            MinerControlStatus = _minerControl.IsRunning ? "Майнер запущен" : "Майнер остановлен";
        UpdateMinerState();

        var ordered = snapshot.Gpus.OrderBy(gpu => gpu.Id).ToArray();
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
                    var initial = int.TryParse(firstDraft, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
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
    }

    private void ApplyEarnings(PoolEarningsSnapshot earnings)
    {
        PoolBalanceStatus = earnings.Message;
        PoolBalance = FormatPrl(earnings.TotalPrl);
        PoolConfirmed = FormatPrl(earnings.ConfirmedPrl);
        PoolUnconfirmed = FormatPrl(earnings.UnconfirmedPrl);
        PoolPaid = FormatPrl(earnings.PaidPrl);
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
            WalletStatus = "Публичный адрес сохранён только в локальном профиле Windows";
            await RefreshAsync(force: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WalletStatus = $"Не удалось сохранить адрес: {ex.Message}";
        }
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
        _timer.Stop();
        _lifetime?.Cancel();
        _lifetime?.Dispose();
    }
}
