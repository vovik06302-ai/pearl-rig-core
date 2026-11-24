using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using PearlRigCore.Models;
using PearlRigCore.Services;
using PearlRigCore.ViewModels;

namespace PearlRigCore;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly HttpClient _minerHttpClient;
    private readonly HttpClient _poolHttpClient;
    private readonly HttpClient _updateHttpClient;
    private readonly HttpClient _telegramHttpClient;
    private readonly NvidiaSmiClient _nvidiaSmi;
    private SystemTrayService? _trayService;
    private bool _isSliderThumbDragging;
    private bool _syncingPasswordBox;
    private bool _forceExit;
    private bool _closingCleanupStarted;

    public MainWindow()
    {
        InitializeComponent();
        var iconPath = Path.Combine(AppContext.BaseDirectory, "pearl-icon.ico");
        if (File.Exists(iconPath)) Icon = BitmapFrame.Create(new Uri(iconPath, UriKind.Absolute));
        var config = RigConfig.Load();
        _minerHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        _poolHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _updateHttpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _telegramHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _nvidiaSmi = new NvidiaSmiClient();
        var telemetry = new TelemetryService(new MinerApiClient(_minerHttpClient), _nvidiaSmi, config.ApiPort);
        _viewModel = new MainViewModel(
            telemetry,
            new PoolEarningsClient(_poolHttpClient),
            new MinerControlService(config),
            new UpdateService(_updateHttpClient),
            _nvidiaSmi,
            new TelegramAlertService(_telegramHttpClient),
            new PoolPingService(),
            config);
        _viewModel.InstallerDownloaded += LaunchDownloadedInstaller;
        _viewModel.TrayStatusChanged += (text, state) => _trayService?.UpdateStatus(text, state);
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        DataContext = _viewModel;
    }

    public void BringToForegroundFromAnotherInstance() => RestoreFromTray();

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.PowerSliderMinimum)
            or nameof(MainViewModel.PowerSliderMaximum)
            or nameof(MainViewModel.PowerSliderValue))
        {
            UpdatePowerGaugeBar(_viewModel.PowerSliderValue);
            return;
        }

        if (!_viewModel.LogAutoScrollEnabled) return;

        ScrollViewer? target = args.PropertyName switch
        {
            nameof(MainViewModel.FilteredMinerLogTail) => MinerLogScrollViewer,
            nameof(MainViewModel.FilteredWatchdogEventsText) => WatchdogLogScrollViewer,
            nameof(MainViewModel.FilteredAppLogTail) => AppLogScrollViewer,
            _ => null
        };

        if (target is not null)
            Dispatcher.BeginInvoke(() => target.ScrollToEnd());
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
        UpdatePowerGaugeBar(_viewModel.PowerSliderValue);
        UpdateMaximizedChromePadding();

        if (TelegramTokenBox is not null)
        {
            _syncingPasswordBox = true;
            TelegramTokenBox.Password = _viewModel.TelegramBotTokenDraft;
            _syncingPasswordBox = false;
        }

        _trayService = new SystemTrayService(
            this,
            RestoreFromTray,
            () => _viewModel.StartMinerCommand.Execute(null),
            () => _viewModel.StopMinerCommand.Execute(null),
            preset => _viewModel.ApplyPresetFromTray(preset),
            () =>
            {
                _forceExit = true;
                Application.Current.Shutdown();
            });
        _trayService.Initialize();
        var autoMineArg = Environment.GetCommandLineArgs().Any(arg => string.Equals(arg, "--auto-mine", StringComparison.OrdinalIgnoreCase));
        if (autoMineArg)
        {
            // Scheduled startup: keep the dashboard out of the taskbar and avoid a visible window flash.
            // The tray icon remains available for status, restore, stop, and exit controls.
            ShowInTaskbar = false;
            Hide();
        }
        _viewModel.Start(autoMineArg);
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestoreWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void RevealTokenToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (TelegramTokenBox is null || TelegramTokenPlainBox is null || RevealTokenToggle is null) return;
        var reveal = RevealTokenToggle.IsChecked == true;
        if (reveal)
        {
            TelegramTokenBox.Visibility = Visibility.Collapsed;
            TelegramTokenPlainBox.Visibility = Visibility.Visible;
        }
        else
        {
            _syncingPasswordBox = true;
            TelegramTokenBox.Password = _viewModel.TelegramBotTokenDraft;
            _syncingPasswordBox = false;
            TelegramTokenPlainBox.Visibility = Visibility.Collapsed;
            TelegramTokenBox.Visibility = Visibility.Visible;
        }
    }

    private void TelegramTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPasswordBox || TelegramTokenBox is null) return;
        _viewModel.TelegramBotTokenDraft = TelegramTokenBox.Password;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizedChromePadding();
        if (WindowState == WindowState.Minimized && _viewModel.MinimizeToTray)
            ShowInTaskbar = false;
        else if (WindowState != WindowState.Minimized)
            ShowInTaskbar = true;
    }

    private void UpdateMaximizedChromePadding()
    {
        if (RootChromeBorder is not null)
            RootChromeBorder.Padding = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        if (MaximizeRestoreButton is not null)
            MaximizeRestoreButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_forceExit || !_viewModel.IsMinerRunning) return;

        if (_closingCleanupStarted)
        {
            e.Cancel = true;
            return;
        }

        var answer = MessageBox.Show(
            this,
            "OneZeroMiner сейчас активен.\n\n• Да — свернуть программу в системный трей (майнинг продолжится)\n• Нет — корректно остановить майнер и выйти\n• Отмена — вернуться в программу",
            "Pearl Rig Control — подтверждение выхода",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (answer == MessageBoxResult.Yes)
        {
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
            Hide();
            return;
        }

        e.Cancel = true;
        _closingCleanupStarted = true;
        try
        {
            await _viewModel.PrepareForInstallerLaunchAsync();
        }
        finally
        {
            _forceExit = true;
            Close();
        }
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _trayService?.Dispose();
        _viewModel.Dispose();
        _minerHttpClient.Dispose();
        _poolHttpClient.Dispose();
        _updateHttpClient.Dispose();
        _telegramHttpClient.Dispose();
    }

    private void PowerLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        UpdatePowerGaugeBar(e.NewValue);

    private void PowerLimitSlider_DragStarted(object sender, DragStartedEventArgs e) =>
        _isSliderThumbDragging = true;

    private async void PowerLimitSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isSliderThumbDragging = false;
        await _viewModel.ApplySliderPowerLimitAsync();
    }

    private async void PowerLimitSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isSliderThumbDragging) return;
        await _viewModel.ApplySliderPowerLimitAsync();
    }

    private async void PowerLimitSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Enter)
            await _viewModel.ApplySliderPowerLimitAsync();
    }

    private void UpdatePowerGaugeBar(double currentValue)
    {
        if (PowerGaugeScale is null || PowerLimitSlider is null) return;
        var range = PowerLimitSlider.Maximum - PowerLimitSlider.Minimum;
        var target = range <= 0 ? 0 : Math.Clamp((currentValue - PowerLimitSlider.Minimum) / range, 0, 1);
        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        PowerGaugeScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, animation);
    }

    private async void LaunchDownloadedInstaller(string installerPath, string? releaseNotes)
    {
        var notesPreview = string.IsNullOrWhiteSpace(releaseNotes)
            ? string.Empty
            : $"\n\nИзменения в версии:\n{(releaseNotes.Length > 500 ? releaseNotes[..500] + "…" : releaseNotes)}";

        var answer = MessageBox.Show(
            this,
            $"Установщик новой версии загружен и проверен по SHA-256. Остановить майнер и запустить установку сейчас?{notesPreview}",
            "Обновление Pearl Rig Control",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            _viewModel.SetUpdateStatus("Установка отложена. Файл сохранён во временной папке Windows.");
            return;
        }

        try
        {
            _forceExit = true;
            await _viewModel.PrepareForInstallerLaunchAsync();
            _trayService?.Dispose();
            Process.Start(new ProcessStartInfo { FileName = installerPath, UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _forceExit = false;
            _viewModel.SetUpdateStatus($"Не удалось запустить установщик: {ex.Message}");
        }
    }
}
