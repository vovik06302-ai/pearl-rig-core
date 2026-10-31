using System.Net.Http;
using System.IO;
using System.Diagnostics;
using System.Windows;
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

    public MainWindow()
    {
        InitializeComponent();
        var iconPath = Path.Combine(AppContext.BaseDirectory, "pearl-icon.ico");
        if (File.Exists(iconPath)) Icon = BitmapFrame.Create(new Uri(iconPath, UriKind.Absolute));
        var config = RigConfig.Load();
        _minerHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        _poolHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _updateHttpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var telemetry = new TelemetryService(new MinerApiClient(_minerHttpClient), new NvidiaSmiClient(), config.ApiPort);
        _viewModel = new MainViewModel(telemetry, new PoolEarningsClient(_poolHttpClient), new MinerControlService(config), new UpdateService(_updateHttpClient), config);
        _viewModel.InstallerDownloaded += LaunchDownloadedInstaller;
        DataContext = _viewModel;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        _viewModel.Start();
        if (Environment.GetCommandLineArgs().Any(arg => string.Equals(arg, "--auto-mine", StringComparison.OrdinalIgnoreCase)))
            _viewModel.StartMinerCommand.Execute(null);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _viewModel.Dispose();
        _minerHttpClient.Dispose();
        _poolHttpClient.Dispose();
        _updateHttpClient.Dispose();
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
