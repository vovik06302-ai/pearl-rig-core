using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PearlRigCore.Services;

public sealed record NvidiaGpuReading(
    int Id,
    int? BusId,
    string Name,
    double? TempC,
    double? PowerWatts,
    double? FanPercent,
    double? CclkMHz,
    double? MclkMHz,
    double? VramTempC,
    string Source = "nvidia-smi");

public sealed record NvidiaReadResult(bool Available, string Message, IReadOnlyList<NvidiaGpuReading> Gpus);

public sealed class NvidiaSmiClient
{
    // Avoid optional NVML fields such as temperature.memory: unsupported fields can make nvidia-smi fail the whole query.
    private const string Query = "--query-gpu=index,name,pci.bus_id,temperature.gpu,power.draw,fan.speed,clocks.current.graphics,clocks.current.memory --format=csv,noheader,nounits";
    private readonly object _inventoryLock = new();
    private DateTimeOffset _inventoryFetchedAt;
    private NvidiaReadResult? _inventoryCache;

    public async Task<NvidiaReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        var executable = ResolveExecutable();
        if (executable is null)
            return await ReadWindowsInventoryCachedAsync(cancellationToken);

        try
        {
            var queries = new[]
            {
                Query,
                "--query-gpu=index,name,pci.bus_id,temperature.gpu,power.draw,clocks.current.graphics,clocks.current.memory --format=csv,noheader,nounits",
                "--query-gpu=index,name,pci.bus_id,temperature.gpu,power.draw --format=csv,noheader,nounits",
                "--query-gpu=index,name,pci.bus_id --format=csv,noheader,nounits"
            };
            ProcessResult result = new(-1, string.Empty, string.Empty);
            foreach (var query in queries)
            {
                result = await RunAsync(executable, query, TimeSpan.FromSeconds(5), cancellationToken);
                if (result.ExitCode != 0) continue;
                var readings = result.Output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                    .Select(ParseNvidiaLine).Where(gpu => gpu is not null).Cast<NvidiaGpuReading>().ToArray();
                if (readings.Length > 0)
                    return new(true, $"Обнаружено GPU: {string.Join(", ", readings.Select(gpu => gpu.Name))}", readings);
            }

            var inventory = await ReadWindowsInventoryCachedAsync(cancellationToken);
            return inventory.Gpus.Count > 0
                ? inventory with { Message = $"nvidia-smi не вернул датчики; Windows обнаружила: {string.Join(", ", inventory.Gpus.Select(gpu => gpu.Name))}. Проверьте драйвер NVIDIA." }
                : new(false, string.IsNullOrWhiteSpace(result.Error) ? "nvidia-smi не вернул видеокарты. Проверьте установленный драйвер NVIDIA." : result.Error.Trim(), []);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var inventory = await ReadWindowsInventoryCachedAsync(cancellationToken);
            return inventory.Gpus.Count > 0
                ? inventory with { Message = $"Не удалось получить датчики NVIDIA: {ex.Message}. Windows обнаружила: {string.Join(", ", inventory.Gpus.Select(gpu => gpu.Name))}." }
                : new(false, $"Не удалось опросить GPU: {ex.Message}", []);
        }
    }

    private async Task<NvidiaReadResult> ReadWindowsInventoryCachedAsync(CancellationToken cancellationToken)
    {
        lock (_inventoryLock)
        {
            if (_inventoryCache is not null && DateTimeOffset.UtcNow - _inventoryFetchedAt < TimeSpan.FromSeconds(20))
                return _inventoryCache;
        }

        var result = await ReadWindowsInventoryAsync(cancellationToken);
        lock (_inventoryLock)
        {
            _inventoryCache = result;
            _inventoryFetchedAt = DateTimeOffset.UtcNow;
        }
        return result;
    }

    private static async Task<NvidiaReadResult> ReadWindowsInventoryAsync(CancellationToken cancellationToken)
    {
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) return new(false, "nvidia-smi не найден, а инвентаризация Windows недоступна. Установите драйвер видеокарты.", []);

        const string script = "$ErrorActionPreference='SilentlyContinue'; $items=@(Get-CimInstance -ClassName Win32_VideoController | Where-Object { $_.Name -and $_.PNPDeviceID -like 'PCI\\*' }); if ($items.Count -gt 0) { $items | Select-Object Name,PNPDeviceID,Status | ConvertTo-Json -Compress }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var processResult = await RunAsync(powershell, $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}", TimeSpan.FromSeconds(5), cancellationToken);
        if (processResult.ExitCode != 0 || string.IsNullOrWhiteSpace(processResult.Output))
            return new(false, "Windows не обнаружила видеокарты. Проверьте драйвер и подключение GPU.", []);

        try
        {
            using var document = JsonDocument.Parse(processResult.Output.Trim());
            var entries = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : [document.RootElement];
            var gpus = entries.Select((entry, index) =>
            {
                var name = entry.TryGetProperty("Name", out var nameElement) ? nameElement.GetString() : null;
                return string.IsNullOrWhiteSpace(name) ? null : new NvidiaGpuReading(index, null, name.Trim(), null, null, null, null, null, null, "windows-inventory");
            }).Where(gpu => gpu is not null).Cast<NvidiaGpuReading>().ToArray();
            return gpus.Length == 0
                ? new(false, "Windows не обнаружила видеокарты. Проверьте драйвер и подключение GPU.", [])
                : new(true, $"Windows обнаружила видеокарты: {string.Join(", ", gpus.Select(gpu => gpu.Name))}; датчики NVIDIA недоступны.", gpus);
        }
        catch (JsonException)
        {
            return new(false, "Не удалось прочитать список видеокарт Windows. Переустановите драйвер видеокарты.", []);
        }
    }

    private static async Task<ProcessResult> RunAsync(string executable, string arguments, TimeSpan timeoutDuration, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            }
        };
        if (!process.Start()) return new(-1, string.Empty, "Не удалось запустить службу обнаружения GPU.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutDuration);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new(-1, string.Empty, "Служба обнаружения GPU не ответила вовремя.");
        }
        return new(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string? ResolveExecutable()
    {
        var candidates = new List<string>();
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        candidates.Add(Path.Combine(system, "nvidia-smi.exe"));
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles)) candidates.Add(Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"));
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86)) candidates.Add(Path.Combine(programFilesX86, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"));
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim(), "nvidia-smi.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static NvidiaGpuReading? ParseNvidiaLine(string line)
    {
        var fields = line.Split(',').Select(field => field.Trim()).ToArray();
        if (fields.Length < 3 || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return null;
        var hasFan = fields.Length >= 8;
        var hasClocks = fields.Length >= 7;
        var hasTemperatureAndPower = fields.Length >= 5;
        return new NvidiaGpuReading(id, ParseBusId(fields[2]), fields[1],
            hasTemperatureAndPower ? ParseNumber(fields[3]) : null,
            hasTemperatureAndPower ? ParseNumber(fields[4]) : null,
            hasFan ? ParseNumber(fields[5]) : null,
            hasClocks ? ParseNumber(fields[hasFan ? 6 : 5]) : null,
            hasClocks ? ParseNumber(fields[hasFan ? 7 : 6]) : null,
            null);
    }

    private static int? ParseBusId(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value, @"^[0-9a-fA-F]+:([0-9a-fA-F]+):[0-9a-fA-F]+\.[0-9]+$");
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bus) ? bus : null;
    }

    private static double? ParseNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number >= 0 ? number : null;

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
