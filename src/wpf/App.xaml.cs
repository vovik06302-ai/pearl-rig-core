using System.Windows;
using System.Windows.Threading;
using PearlRigCore.Services;

namespace PearlRigCore;

public partial class App : Application
{
    private const string MutexName = @"Local\PearlRigControl_SingleInstance_Mutex";
    private const string ActivateEventName = @"Local\PearlRigControl_Activate_Event";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWaitHandle;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            try
            {
                using var existingEvent = EventWaitHandle.OpenExisting(ActivateEventName);
                existingEvent.Set();
            }
            catch
            {
                // Ignore if primary instance is closing or event unavailable.
            }

            Shutdown();
            return;
        }

        RegisterGlobalExceptionHandlers();
        AppLogger.Info("Запуск Pearl Rig Control.");

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;

        _activateWaitHandle = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => Dispatcher.BeginInvoke(mainWindow.BringToForegroundFromAnotherInstance),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.Info($"Завершение работы Pearl Rig Control (код {e.ApplicationExitCode}).");
        WindowsDiagnosticsService.SetMiningSleepPrevention(false);
        _activateWaitHandle?.Unregister(null);
        _activateEvent?.Dispose();
        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch { /* Not owned if secondary. */ }
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Необработанное исключение UI-потока", e.Exception);
        e.Handled = true;
        MessageBox.Show(
            $"Произошла внутренняя ошибка интерфейса (записана в app.log):\n{e.Exception.Message}",
            "Pearl Rig Control — ошибка",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Критическое исключение AppDomain", e.ExceptionObject as Exception);
        WindowsDiagnosticsService.SetMiningSleepPrevention(false);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Error("Неотслеженное исключение фоновой задачи", e.Exception);
        e.SetObserved();
    }
}
