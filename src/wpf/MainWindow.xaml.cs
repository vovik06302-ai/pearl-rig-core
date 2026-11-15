using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
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
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.PowerSliderMinimum) or nameof(MainViewModel.PowerSliderMaximum) or nameof(MainViewModel.PowerSliderValue))
                UpdatePowerGaugeBar(_viewModel.PowerSliderValue);
        };
        DataContext = _viewModel;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        UpdatePowerGaugeBar(_viewModel.PowerSliderValue);
        _trayService = new SystemTrayService(
            this,
            RestoreFromTray,
            () => _viewModel.StartMinerCommand.Execute(null),
            () => _viewModel.StopMinerCommand.Execute(null),
            preset => _viewModel.ApplyPresetFromTray(preset),
            () => Application.Current.Shutdown());
        _trayService.Initialize();
        var autoMineArg = Environment.GetCommandLineArgs().Any(arg => string.Equals(arg, "--auto-mine", StringComparison.OrdinalIgnoreCase));
        _viewModel.Start(autoMineArg);
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _viewModel.MinimizeToTray)
            ShowInTaskbar = false;
        else if (WindowState != WindowState.Minimized)
            ShowInTaskbar = true;
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

    private void LaunchDownloadedInstaller(string installerPath)
    {
        var answer = MessageBox.Show(this, "Установщик новой версии загружен и проверен. Запустить установку сейчас? Программа закроется перед обновлением.", "Обновление Pearl Rig Control", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            _viewModel.SetUpdateStatus("Установка отложена. Файл сохранён во временной папке Windows.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = installerPath, UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _viewModel.SetUpdateStatus($"Не удалось запустить установщик: {ex.Message}");
        }
    }
}
