using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using PearlRigCore.Models;

namespace PearlRigCore.Services;

/// <summary>Starts OneZeroMiner with the configured Pearl pool, manages Windows Task Scheduler auto-start, and sends native Ctrl+C shutdown with emergency timeout fallback.</summary>
public sealed class MinerControlService
{
    private const string TaskName = "PearlRigControl-AutoStart";
    private RigConfig _config;
    private bool _useBackupPool;
    private int? _trackedMinerPid;

    public MinerControlService(RigConfig config) => _config = config;

    public void UpdateConfig(RigConfig config) => _config = config;

    public bool UseBackupPool
    {
        get => _useBackupPool;
        set => _useBackupPool = value;
    }

    public static bool RequiresMinerRestartForSettings(RigConfig previous, RigConfig next) =>
        !string.Equals(previous.WorkerName, next.WorkerName, StringComparison.Ordinal)
        || !string.Equals(previous.PrimaryPool, next.PrimaryPool, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(previous.BackupPool, next.BackupPool, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(previous.CoreClock, next.CoreClock, StringComparison.Ordinal)
        || !string.Equals(previous.MemoryClock, next.MemoryClock, StringComparison.Ordinal)
        || !string.Equals(previous.Kernel, next.Kernel, StringComparison.Ordinal)
        || !string.Equals(previous.Fan, next.Fan, StringComparison.Ordinal);

    public static string GetLogFilePath()
    {
        var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PearlRigControl", "logs");
        Directory.CreateDirectory(logDirectory);
        return Path.Combine(logDirectory, "onezerominer.log");
    }

    public string GetCommandLinePreview(string? walletOverride = null, int gpuCount = 2)
    {
        var wallet = string.IsNullOrWhiteSpace(walletOverride) ? _config.WalletAddress : walletOverride.Trim();
        var maskedWallet = PoolEarningsClient.IsValidWallet(wallet) ? wallet.ToLowerInvariant() : "<PRL_WALLET>";
        return $"onezerominer.exe {BuildArguments(_config with { WalletAddress = maskedWallet }, GetLogFilePath(), _useBackupPool, gpuCount)}";
    }

    public string ReadRecentLogLines(int maxLines = 18)
    {
        var path = GetLogFilePath();
        var excerpt = ReadLogExcerpt(path, 6000);
        if (string.IsNullOrWhiteSpace(excerpt))
            return "Лог майнера пока пуст или майнер ещё не запускался.";
        var lines = excerpt.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines.TakeLast(maxLines));
    }

    public static bool IsAutoStartTaskRegistered()
    {
        try
        {
            var schtasks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
            if (!File.Exists(schtasks)) return false;
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = schtasks,
                Arguments = $"/Query /TN \"{TaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return false;
            process.WaitForExit(2000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public bool IsRunning
    {
        get
        {
            if (_trackedMinerPid is int pid)
            {
                try
                {
                    using var tracked = Process.GetProcessById(pid);
                    if (!tracked.HasExited && tracked.ProcessName.Equals("onezerominer", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    _trackedMinerPid = null;
                }
            }

            foreach (var process in Process.GetProcessesByName("onezerominer"))
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            _trackedMinerPid = process.Id;
                            return true;
                        }
                    }
                    catch (InvalidOperationException) { }
                }
            }
            return false;
        }
    }

    public async Task StartAsync(string walletAddress, int gpuCount = 2, CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;
        walletAddress = walletAddress.Trim().ToLowerInvariant();
        if (!PoolEarningsClient.IsValidWallet(walletAddress))
            throw new InvalidOperationException("Сначала введите и сохраните корректный адрес PRL (prl1p... или prl1q...).");

        var minerPath = ResolveMinerPath();
        if (minerPath is null)
            throw new FileNotFoundException("Не найден onezerominer.exe. Поместите его в подпапку miner рядом с программой или переустановите пакет PearlRigControl-Setup.exe.");

        var logPath = GetLogFilePath();
        var arguments = BuildArguments(_config with { WalletAddress = walletAddress }, logPath, _useBackupPool, gpuCount);
        var isElevated = IsCurrentProcessElevated();

        var startInfo = new ProcessStartInfo
        {
            FileName = minerPath,
            WorkingDirectory = Path.GetDirectoryName(minerPath)!,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
            Arguments = arguments
        };
        if (!isElevated)
            startInfo.Verb = "runas";

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var started = Process.Start(startInfo);
            if (started is null) throw new InvalidOperationException("Windows не смогла запустить OneZeroMiner.");
            _trackedMinerPid = started.Id;
            AppLogger.Info($"Запущен OneZeroMiner PID {started.Id} (пул: {(_useBackupPool ? _config.BackupPool : _config.PrimaryPool)}).");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Запуск отменён в запросе контроля учётных записей Windows.", ex, cancellationToken);
        }

        await Task.Delay(2500, cancellationToken);
        if (!IsRunning)
        {
            _trackedMinerPid = null;
            var excerpt = ReadLogExcerpt(logPath, 3000);
            throw new InvalidOperationException($"OneZeroMiner завершился при запуске. Лог: {logPath}{(excerpt.Length == 0 ? string.Empty : Environment.NewLine + excerpt)}");
        }
    }

    public async Task StopAsync(bool forceKillOnTimeout = false, CancellationToken cancellationToken = default)
    {
        var miners = Process.GetProcessesByName("onezerominer");
        if (miners.Length == 0)
        {
            _trackedMinerPid = null;
            return;
        }

        var isElevated = IsCurrentProcessElevated();
        try
        {
            foreach (var miner in miners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (isElevated)
                {
                    if (!TrySendCtrlCDirect(miner.Id))
                        await SendCtrlCAsAdministratorAsync(miner.Id, cancellationToken);
                }
                else
                {
                    await SendCtrlCAsAdministratorAsync(miner.Id, cancellationToken);
                }
            }

            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < deadline && IsRunning)
                await Task.Delay(250, cancellationToken);

            if (IsRunning && forceKillOnTimeout)
            {
                AppLogger.Warn("OneZeroMiner не завершился после Ctrl+C за 12 с; выполняется принудительное завершение (аварийный режим).");
                foreach (var miner in Process.GetProcessesByName("onezerominer"))
                {
                    using (miner)
                    {
                        try
                        {
                            if (!miner.HasExited)
                                miner.Kill(entireProcessTree: true);
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn($"Не удалось завершить PID {miner.Id}: {ex.Message}");
                        }
                    }
                }
                await Task.Delay(500, cancellationToken);
            }
        }
        finally
        {
            foreach (var miner in miners) miner.Dispose();
        }

        if (IsRunning)
            throw new InvalidOperationException("OneZeroMiner не завершился после штатного Ctrl+C.");

        _trackedMinerPid = null;
        AppLogger.Info("OneZeroMiner остановлен.");
    }

    public async Task RestartAsync(string walletAddress, int gpuCount = 2, CancellationToken cancellationToken = default)
    {
        await StopAsync(forceKillOnTimeout: true, cancellationToken);
        await Task.Delay(1200, cancellationToken);
        await StartAsync(walletAddress, gpuCount, cancellationToken);
    }

    public async Task SetAutoStartAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь приложения.");
        var workDir = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var taskArgs = enabled
            ? $"$a=New-ScheduledTaskAction -Execute '{EscapePowerShell(executable)}' -Argument '--auto-mine' -WorkingDirectory '{EscapePowerShell(workDir)}'; $t=New-ScheduledTaskTrigger -AtLogOn -User ($env:USERDOMAIN+'\\'+$env:USERNAME); $p=New-ScheduledTaskPrincipal -UserId ($env:USERDOMAIN+'\\'+$env:USERNAME) -LogonType Interactive -RunLevel Highest; $s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable; Register-ScheduledTask -TaskName '{TaskName}' -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null"
            : $"Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false -ErrorAction SilentlyContinue";
        await RunElevatedPowerShellAsync(taskArgs, cancellationToken);
    }

    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySendCtrlCDirect(int processId)
    {
        try
        {
            _ = NativeConsole.FreeConsole();
            if (!NativeConsole.AttachConsole((uint)processId))
                return false;

            _ = NativeConsole.SetConsoleCtrlHandler(IntPtr.Zero, true);
            var sent = NativeConsole.GenerateConsoleCtrlEvent(0, 0);
            _ = NativeConsole.FreeConsole();
            _ = NativeConsole.SetConsoleCtrlHandler(IntPtr.Zero, false);
            return sent;
        }
        catch
        {
            return false;
        }
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

    public static string BuildArguments(RigConfig rig, string logPath, bool useBackupPool, int gpuCount)
    {
        static string Arg(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        var activePool = useBackupPool && !string.IsNullOrWhiteSpace(rig.BackupPool)
            ? rig.BackupPool.Trim()
            : rig.PrimaryPool.Trim();

        // NOTE: In OneZeroMiner v1.8.3, -o2 is for dual-mining coin #2 (--algo2), NOT a failover pool for coin #1.
        var arguments = new StringBuilder($"-a pearl -w {Arg(rig.WalletAddress)} -o {Arg(activePool)} --worker {Arg(rig.WorkerName)} --api-port {rig.ApiPort} --log-file {Arg(logPath)}");
        AddOverride(arguments, "--cclk", rig.CoreClock, gpuCount, Arg);
        AddOverride(arguments, "--mclk", rig.MemoryClock, gpuCount, Arg);
        AddOverride(arguments, "--pl", rig.PowerLimit, gpuCount, Arg);
        AddOverride(arguments, "--kernel", rig.Kernel, gpuCount, Arg);
        AddOverride(arguments, "--fan", rig.Fan, gpuCount, Arg);
        return arguments.ToString();
    }

    private static void AddOverride(StringBuilder arguments, string option, string value, int gpuCount, Func<string, string> quote)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) || value.Trim().Equals("авто", StringComparison.OrdinalIgnoreCase))
            return;

        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return;

        var normalized = parts.Length == 1 && gpuCount > 1
            ? string.Join(',', Enumerable.Repeat(parts[0], gpuCount))
            : string.Join(',', parts);

        arguments.Append(' ').Append(option).Append(' ').Append(quote(normalized));
    }

    public static string? ResolveMinerPath()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(exeDir))
        {
            candidates.Add(Path.Combine(exeDir, "miner", "onezerominer.exe"));
            candidates.Add(Path.Combine(exeDir, "onezerominer.exe"));
        }
        candidates.AddRange(
        [
            Path.Combine(baseDir, "miner", "onezerominer.exe"),
            Path.Combine(baseDir, "onezerominer.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Pearl Rig Control", "miner", "onezerominer.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PearlRigControl", "miner", "onezerominer.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pearl Rig Control", "miner", "onezerominer.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Pearl Rig Control", "miner", "onezerominer.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PearlRigCore", "onezerominer.exe"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "onezerominer.exe"))
        ]);
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string ReadLogExcerpt(string path, int maxChars)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maxChars * 2)
                stream.Seek(-maxChars * 2, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            return text.Length <= maxChars ? text : text[^maxChars..];
        }
        catch (IOException) { return string.Empty; }
        catch (UnauthorizedAccessException) { return string.Empty; }
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static class NativeConsole
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachConsole(uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);
    }
}
