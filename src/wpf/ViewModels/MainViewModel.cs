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
    private RigConfig _config;
    private readonly int _earningsRefreshSeconds = 60;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _lifetime;
    private DateTimeOffset _lastEarningsRead = DateTimeOffset.MinValue;
    private string _walletAddress;
    private bool _isRefreshing;
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
    private bool _autoStartMining;
    private string _minerControlStatus = "Проверка майнера…";
    private string _workerNameDraft;
    private string _coreClockDraft;
    private string _memoryClockDraft;
    private string _powerLimitDraft;
    private string _kernelDraft;
    private string _fanDraft;

    public MainViewModel(TelemetryService telemetry, PoolEarningsClient earningsClient, MinerControlService minerControl, RigConfig config)
    {
        _telemetry = telemetry;
        _earningsClient = earningsClient;
        _minerControl = minerControl;
        _config = config;
        _autoStartMining = config.AutoStartMining;
        _workerNameDraft = config.WorkerName;
        _coreClockDraft = config.CoreClock;
        _memoryClockDraft = config.MemoryClock;
        _powerLimitDraft = config.PowerLimit;
        _kernelDraft = config.Kernel;
        _fanDraft = config.Fan;
        _walletAddress = config.WalletAddress;
        _walletAddressDraft = config.WalletAddress;
        _walletStatus = PoolEarningsClient.IsValidWallet(config.WalletAddress) ? "Адрес выплат загружен из локального профиля" : "Введите публичный адрес PRL";
        SaveWalletCommand = new RelayCommand(() => _ = SaveWalletAsync(), () => PoolEarningsClient.IsValidWallet(WalletAddressDraft));
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !_isRefreshing);
        StartMinerCommand = new RelayCommand(() => _ = StartMinerAsync(), () => !_isMinerBusy && !_minerControl.IsRunning);
        StopMinerCommand = new RelayCommand(() => _ = StopMinerAsync(), () => !_isMinerBusy && _minerControl.IsRunning);
        ToggleMinerModeCommand = new RelayCommand(() => _ = ToggleMinerModeAsync(), () => !_isMinerBusy);
        SaveMinerSettingsCommand = new RelayCommand(() => _ = SaveMinerSettingsAsync(), CanSaveMinerSettings);
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
    public bool IsRefreshing { get => _isRefreshing; private set { if (Set(ref _isRefreshing, value)) RefreshCommand.NotifyCanExecuteChanged(); } }
    public bool IsMinerRunning => _minerControl.IsRunning;
    public string MinerControlStatus { get => _minerControlStatus; private set => Set(ref _minerControlStatus, value); }
    public bool AutoStartMining { get => _autoStartMining; private set { if (Set(ref _autoStartMining, value)) OnPropertyChanged(nameof(MinerModeLabel)); } }
    public string MinerModeLabel => AutoStartMining ? "АВТОЗАПУСК" : "ВРУЧНУЮ";
    public string WorkerNameDraft { get => _workerNameDraft; set { if (Set(ref _workerNameDraft, value)) SaveMinerSettingsCommand.NotifyCanExecuteChanged(); } }
    public string CoreClockDraft { get => _coreClockDraft; set { if (Set(ref _coreClockDraft, value)) SaveMinerSettingsCommand.NotifyCanExecuteChanged(); } }
    public string MemoryClockDraft { get => _memoryClockDraft; set { if (Set(ref _memoryClockDraft, value)) SaveMinerSettingsCommand.NotifyCanExecuteChanged(); } }
    public string PowerLimitDraft { get => _powerLimitDraft; set { if (Set(ref _powerLimitDraft, value)) SaveMinerSettingsCommand.NotifyCanExecuteChanged(); } }
    public string KernelDraft { get => _kernelDraft; set { if (Set(ref _kernelDraft, value)) SaveMinerSettingsCommand.NotifyCanExecuteChanged(); } }
    public string FanDraft { get => _fanDraft; set { if (Set(ref _fanDraft, value)) SaveMinerSettingsCommand.NotifyCanExecuteChanged(); } }
    public bool HasNoGpus => Gpus.Count == 0;

    public void Start()
    {
        _lifetime = new CancellationTokenSource();
        _timer.Start();
        UpdateMinerState();
        _ = RefreshAsync();
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
            _ = RefreshAsync();
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
            _ = RefreshAsync();
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
        && IsGpuListValid(MemoryClockDraft, 0, 30000)
        && IsGpuListValid(PowerLimitDraft, 1, 1000)
        && IsGpuListValid(KernelDraft, 0, 16)
        && IsGpuListValid(FanDraft, 0, 100, allowAuto: true);

    private static bool IsGpuListValid(string text, int min, int max, bool allowAuto = false)
    {
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
                CoreClock = string.Join(',', CoreClockDraft.Split(',', StringSplitOptions.TrimEntries)),
                MemoryClock = string.Join(',', MemoryClockDraft.Split(',', StringSplitOptions.TrimEntries)),
                PowerLimit = string.Join(',', PowerLimitDraft.Split(',', StringSplitOptions.TrimEntries)),
                Kernel = string.Join(',', KernelDraft.Split(',', StringSplitOptions.TrimEntries)),
                Fan = string.Join(',', FanDraft.Split(',', StringSplitOptions.TrimEntries))
            };
            await RigConfig.SaveLocalSettingsAsync(_config, _lifetime?.Token ?? CancellationToken.None);
            _minerControl.UpdateConfig(_config);
            MinerControlStatus = _minerControl.IsRunning
                ? "Настройки сохранены для следующего запуска майнера"
                : "Настройки сохранены на этом компьютере";
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

    private async Task RefreshAsync()
    {
        if (IsRefreshing || _disposed) return;
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
        finally { IsRefreshing = false; }
    }

    private void Apply(TelemetrySnapshot snapshot)
    {
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
        OnPropertyChanged(nameof(HasNoGpus));
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
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WalletStatus = $"Не удалось сохранить адрес: {ex.Message}";
        }
    }

    private static string FormatPrl(double? amount) => amount is double value ? $"{value:0.########} PRL" : "Нет данных";

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _lifetime?.Cancel();
        _lifetime?.Dispose();
    }
}
