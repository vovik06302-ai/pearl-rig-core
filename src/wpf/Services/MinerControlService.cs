using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using PearlRigCore.Models;

namespace PearlRigCore.Services;

/// <summary>Starts OneZeroMiner with the configured Pearl pool and requests a graceful Ctrl+C shutdown.</summary>
public sealed class MinerControlService
{
    private const string TaskName = "PearlRigControl-AutoStart";
    private RigConfig _config;

    public MinerControlService(RigConfig config) => _config = config;

    public void UpdateConfig(RigConfig config) => _config = config;

    public bool IsRunning
    {
        get
        {
            foreach (var process in Process.GetProcessesByName("onezerominer"))
            {
                using (process)
                {
                    try { if (!process.HasExited) return true; }
                    catch (InvalidOperationException) { }
                }
            }
            return false;
        }
    }

    public async Task StartAsync(string walletAddress, CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;
        if (!PoolEarningsClient.IsValidWallet(walletAddress))
            throw new InvalidOperationException("Сначала введите и сохраните корректный адрес PRL.");

        var minerPath = ResolveMinerPath();
        if (minerPath is null)
            throw new FileNotFoundException("Не найден onezerominer.exe. Поместите его в подпапку miner рядом с программой или в C:\\PearlRigCore.");

        var startInfo = new ProcessStartInfo
        {
            FileName = minerPath,
            WorkingDirectory = Path.GetDirectoryName(minerPath)!,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Normal,
            Arguments = BuildArguments(_config with { WalletAddress = walletAddress })
        };
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var started = Process.Start(startInfo);
            if (started is null) throw new InvalidOperationException("Windows не смогла запустить OneZeroMiner.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Запуск отменён в запросе контроля учётных записей Windows.", ex, cancellationToken);
        }
        await Task.Delay(800, cancellationToken);
        if (!IsRunning) throw new InvalidOperationException("Процесс OneZeroMiner завершился сразу после запуска. Проверьте параметры и лог майнера.");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var miners = Process.GetProcessesByName("onezerominer");
        if (miners.Length == 0) return;

        try
        {
            foreach (var miner in miners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SendCtrlCAsAdministratorAsync(miner.Id, cancellationToken);
            }
        }
        finally { foreach (var miner in miners) miner.Dispose(); }
        var deadline = DateTime.UtcNow.AddSeconds(14);
        while (DateTime.UtcNow < deadline && IsRunning)
            await Task.Delay(250, cancellationToken);
        if (IsRunning) throw new InvalidOperationException("OneZeroMiner не завершился после штатного Ctrl+C. Принудительно завершать процесс не стал.");
    }

    public async Task SetAutoStartAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь приложения.");
        var taskArgs = enabled
            ? $"$a=New-ScheduledTaskAction -Execute '{EscapePowerShell(executable)}' -Argument '--auto-mine' -WorkingDirectory '{EscapePowerShell(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))}'; $t=New-ScheduledTaskTrigger -AtLogOn -User ($env:USERDOMAIN+'\\'+$env:USERNAME); $p=New-ScheduledTaskPrincipal -UserId ($env:USERDOMAIN+'\\'+$env:USERNAME) -LogonType Interactive -RunLevel Highest; $s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable; Register-ScheduledTask -TaskName '{TaskName}' -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null"
            : $"Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false -ErrorAction SilentlyContinue";
        await RunElevatedPowerShellAsync(taskArgs, cancellationToken);
    }

    private static async Task SendCtrlCAsAdministratorAsync(int processId, CancellationToken cancellationToken)
    {
        var script = $$"""
            $id = {{processId}}
            $p = Get-Process -Id $id -ErrorAction SilentlyContinue
            if (-not $p) { exit 0 }
            if ($p.ProcessName -ine 'onezerominer') { exit 31 }
            Add-Type -TypeDefinition @'
            using System;
            using System.Runtime.InteropServices;
            public static class PearlConsoleSignal {
              [DllImport("kernel32.dll", SetLastError=true)] public static extern bool FreeConsole();
              [DllImport("kernel32.dll", SetLastError=true)] public static extern bool AttachConsole(uint processId);
              [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
              [DllImport("kernel32.dll", SetLastError=true)] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
            }
            '@
            [void][PearlConsoleSignal]::FreeConsole()
            if (-not [PearlConsoleSignal]::AttachConsole([uint32]$id)) { exit 32 }
            [void][PearlConsoleSignal]::SetConsoleCtrlHandler([IntPtr]::Zero, $true)
            $sent = [PearlConsoleSignal]::GenerateConsoleCtrlEvent(0, 0)
            [void][PearlConsoleSignal]::FreeConsole()
            if (-not $sent) { exit 33 }
            if ($p.WaitForExit(12000)) { exit 0 }
            exit 34
            """;
        await RunElevatedPowerShellAsync(script, cancellationToken);
    }

    private static async Task RunElevatedPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var info = new ProcessStartInfo
        {
            FileName = powershell,
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось выполнить операцию с повышенными правами.");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) throw new InvalidOperationException($"Windows отклонила операцию (код {process.ExitCode}).");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Операция отменена в запросе контроля учётных записей Windows.", ex, cancellationToken);
        }
    }

    private static string BuildArguments(RigConfig rig)
    {
        static string Arg(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        return $"-a pearl -w {Arg(rig.WalletAddress)} -o {Arg(rig.PrimaryPool)} --worker {Arg(rig.WorkerName)} " +
               $"--cclk {Arg(rig.CoreClock)} --mclk {Arg(rig.MemoryClock)} --pl {Arg(rig.PowerLimit)} " +
               $"--kernel {Arg(rig.Kernel)} --fan {Arg(rig.Fan)} --api-port {rig.ApiPort}";
    }

    private static string? ResolveMinerPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "miner", "onezerominer.exe"),
            Path.Combine(AppContext.BaseDirectory, "onezerominer.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PearlRigControl", "miner", "onezerominer.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PearlRigCore", "onezerominer.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "onezerominer.exe"))
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
