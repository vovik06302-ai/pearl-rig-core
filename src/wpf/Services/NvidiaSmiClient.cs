using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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
    double? PowerLimitWatts = null,
    double? MinPowerLimitWatts = null,
    double? MaxPowerLimitWatts = null,
    double? DefaultPowerLimitWatts = null,
    string? ThrottleReasons = null,
    string? PcieLink = null,
    string? PState = null,
    double? CoreUtilizationPercent = null,
    double? MemoryUtilizationPercent = null,
    double? EnforcedPowerLimitWatts = null,
    double? HardwareEnergyKwh = null,
    string Source = "nvidia-smi");

public sealed record NvidiaReadResult(bool Available, string Message, IReadOnlyList<NvidiaGpuReading> Gpus);

public sealed class NvidiaSmiClient
{
    private const string Query = "--query-gpu=index,name,pci.bus_id,temperature.gpu,power.draw,fan.speed,clocks.current.graphics,clocks.current.memory,power.limit,power.min_limit,power.max_limit,power.default_limit,pstate,utilization.gpu,utilization.memory --format=csv,noheader,nounits";
    private static string? _cachedSmiExecutable;
    private static string? _cachedNvmlPath;
    private readonly object _inventoryLock = new();
    private DateTimeOffset _inventoryFetchedAt;
    private NvidiaReadResult? _inventoryCache;

    public async Task<NvidiaReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        if (NvmlNativeReader.TryReadAll(out var nvmlGpus, out var nvmlMessage) && nvmlGpus.Count > 0)
            return new NvidiaReadResult(true, nvmlMessage, nvmlGpus);

        var executable = ResolveExecutable();
        if (executable is null)
            return await ReadWindowsInventoryCachedAsync(cancellationToken);

        try
        {
            var queries = new[]
            {
                Query,
                "--query-gpu=index,name,pci.bus_id,temperature.gpu,power.draw,fan.speed,clocks.current.graphics,clocks.current.memory,power.limit,power.min_limit,power.max_limit,power.default_limit --format=csv,noheader,nounits",
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
                    return new(true, $"Обнаружено GPU (SMI): {string.Join(", ", readings.Select(gpu => gpu.Name))}", readings);
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

    public bool TryApplySmartFansDirect(IReadOnlyList<(int GpuId, int TargetFanPercent)> targets) =>
        NvmlNativeReader.TrySetFanSpeeds(targets);

    public bool TryResetFansToAutoDirect(IEnumerable<int> gpuIds) =>
        NvmlNativeReader.TryResetFanSpeedsToDefault(gpuIds);

    /// <summary>Sets per-GPU NVIDIA power caps (first via direct NVML, otherwise via elevated NVML + nvidia-smi) and verifies the values returned by the driver.</summary>
    public async Task<string> ApplyPowerLimitsAsync(string draft, CancellationToken cancellationToken)
    {
        var inventory = await ReadAsync(cancellationToken);
        var cards = inventory.Gpus.Where(gpu => gpu.Source is "nvml" or "nvidia-smi").OrderBy(gpu => gpu.Id).ToArray();
        if (cards.Length == 0)
            throw new InvalidOperationException($"Нужны доступные датчики NVIDIA для применения лимита. {inventory.Message}");

        var trimmed = (draft ?? string.Empty).Trim();
        var isAuto = string.IsNullOrWhiteSpace(trimmed)
            || trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("авто", StringComparison.OrdinalIgnoreCase);

        var rawFields = isAuto
            ? []
            : trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var fields = isAuto
            ? []
            : rawFields.Length == 1 && cards.Length > 1
                ? Enumerable.Repeat(rawFields[0], cards.Length).ToArray()
                : rawFields;

        if (!isAuto && fields.Length < cards.Length)
            throw new InvalidOperationException($"Для {cards.Length} NVIDIA GPU задайте одно общее или {cards.Length} значений лимита в ваттах через запятую.");

        var targets = new List<(NvidiaGpuReading Gpu, int Watts)>(cards.Length);
        for (var index = 0; index < cards.Length; index++)
        {
            var gpu = cards[index];
            var fallbackBase = gpu.DefaultPowerLimitWatts ?? gpu.PowerLimitWatts ?? gpu.EnforcedPowerLimitWatts ?? 285d;
            var min = gpu.MinPowerLimitWatts ?? Math.Max(90d, Math.Round(fallbackBase * 0.35));
            var max = gpu.MaxPowerLimitWatts ?? Math.Round(fallbackBase * 1.15);
            var minInt = (int)Math.Ceiling(min);
            var maxInt = (int)Math.Floor(max);

            int watts;
            if (isAuto)
            {
                var defW = gpu.DefaultPowerLimitWatts ?? gpu.PowerLimitWatts ?? gpu.EnforcedPowerLimitWatts ?? max;
                watts = (int)Math.Round(defW);
            }
            else if (!int.TryParse(fields[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out watts))
            {
                throw new InvalidOperationException($"Некорректное значение лимита мощности «{fields[index]}». Укажите целые ватты.");
            }

            if (minInt <= maxInt)
                watts = Math.Clamp(watts, minInt, maxInt);

            targets.Add((gpu, watts));
        }

        var targetPairs = targets.Select(t => (t.Gpu.Id, t.Watts)).ToArray();
        var appliedDirectNvml = NvmlNativeReader.TrySetPowerLimits(targetPairs);
        if (!appliedDirectNvml)
        {
            await RunElevatedPowerLimitsAsync(targetPairs, cancellationToken);
            await Task.Delay(250, cancellationToken);
        }

        var after = await ReadAsync(cancellationToken);
        foreach (var target in targets)
        {
            var updatedGpu = after.Gpus.FirstOrDefault(gpu => gpu.Id == target.Gpu.Id);
            var actual = updatedGpu?.PowerLimitWatts ?? updatedGpu?.EnforcedPowerLimitWatts;
            if (actual is double value && Math.Abs(value - target.Watts) > 2.5)
            {
                var enforced = updatedGpu?.EnforcedPowerLimitWatts;
                if (enforced is not double enfVal || Math.Abs(enfVal - target.Watts) > 2.5)
                    throw new InvalidOperationException($"Драйвер не подтвердил лимит {target.Watts} Вт для GPU #{target.Gpu.Id}; текущее значение: {value:0.#} Вт.");
            }
        }

        var method = appliedDirectNvml ? "прямой NVML" : "NVML/SMI Администратор";
        return $"{string.Join("; ", targets.Select(target => $"GPU #{target.Gpu.Id}: {target.Watts} Вт"))} ({method})";
    }

    private static async Task RunElevatedPowerLimitsAsync(IReadOnlyList<(int GpuId, int Watts)> targets, CancellationToken cancellationToken)
    {
        var nvmlPath = ResolveNvmlPath() ?? "nvml.dll";
        var smiPath = ResolveExecutable() ?? string.Empty;
        var pairsLiteral = string.Join("; ", targets.Select(t => $"@({t.GpuId}, {t.Watts})"));

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $nvmlPath = '{{EscapePowerShell(nvmlPath)}}'
            $smiPath = '{{EscapePowerShell(smiPath)}}'
            $targets = @({{pairsLiteral}})

            Add-Type -TypeDefinition @"
            using System;
            using System.IO;
            using System.Runtime.InteropServices;
            public static class PearlNvmlElevated {
                [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
                public static extern IntPtr LoadLibrary(string lpFileName);

                [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2", ExactSpelling = true)]
                public static extern int nvmlInit_v2();

                [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2", ExactSpelling = true)]
                public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

                [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetPowerManagementLimit", ExactSpelling = true)]
                public static extern int nvmlDeviceSetPowerManagementLimit(IntPtr device, uint limitMilliwatts);

                public static bool Apply(string dllPath, int gpuId, int watts) {
                    try {
                        if (!string.IsNullOrEmpty(dllPath) && File.Exists(dllPath)) {
                            LoadLibrary(dllPath);
                        }
                        if (nvmlInit_v2() != 0) return false;
                        IntPtr handle;
                        if (nvmlDeviceGetHandleByIndex_v2((uint)gpuId, out handle) != 0) return false;
                        return nvmlDeviceSetPowerManagementLimit(handle, (uint)(watts * 1000)) == 0;
                    } catch {
                        return false;
                    }
                }
            }
            "@

            foreach ($pair in $targets) {
                $gpuId = [int]$pair[0]
                $watts = [int]$pair[1]
                $ok = [PearlNvmlElevated]::Apply($nvmlPath, $gpuId, $watts)
                if (-not $ok) {
                    if ([string]::IsNullOrWhiteSpace($smiPath) -or -not (Test-Path -LiteralPath $smiPath)) {
                        exit 41
                    }
                    & $smiPath -i $gpuId -pl $watts
                    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                }
            }
            exit 0
            """;

        await RunElevatedAsync(script, cancellationToken);
    }

    private static async Task RunElevatedAsync(string script, CancellationToken cancellationToken)
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
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить операцию управления питанием.");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Драйвер NVIDIA отклонил изменение лимита мощности (код {process.ExitCode}). Запустите программу от имени Администратора.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Применение отменено в запросе контроля учётных записей Windows.", ex, cancellationToken);
        }
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);

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
                return string.IsNullOrWhiteSpace(name) ? null : new NvidiaGpuReading(index, null, name.Trim(), null, null, null, null, null, null, Source: "windows-inventory");
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
        if (!string.IsNullOrWhiteSpace(_cachedSmiExecutable) && File.Exists(_cachedSmiExecutable))
            return _cachedSmiExecutable;

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

        var found = candidates.FirstOrDefault(File.Exists) ?? FindInDriverStore("nvidia-smi.exe");
        if (found is not null) _cachedSmiExecutable = found;
        return found;
    }

    private static string? ResolveNvmlPath()
    {
        if (!string.IsNullOrWhiteSpace(_cachedNvmlPath) && File.Exists(_cachedNvmlPath))
            return _cachedNvmlPath;

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvml.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll")
        };

        var found = candidates.FirstOrDefault(File.Exists) ?? FindInDriverStore("nvml.dll");
        if (found is not null) _cachedNvmlPath = found;
        return found;
    }

    private static string? FindInDriverStore(string fileName)
    {
        try
        {
            var repo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");
            if (!Directory.Exists(repo)) return null;
            foreach (var dir in Directory.EnumerateDirectories(repo, "nv*"))
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch
        {
            // Ignore permission or IO errors when scanning DriverStore.
        }
        return null;
    }

    private static NvidiaGpuReading? ParseNvidiaLine(string line)
    {
        var fields = line.Split(',').Select(field => field.Trim()).ToArray();
        if (fields.Length < 3 || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return null;
        var hasFan = fields.Length >= 8;
        var hasClocks = fields.Length >= 7;
        var hasTemperatureAndPower = fields.Length >= 5;
        var limitW = fields.Length >= 12 ? ParseNumber(fields[8]) : null;
        var minW = fields.Length >= 12 ? ParseNumber(fields[9]) : null;
        var maxW = fields.Length >= 12 ? ParseNumber(fields[10]) : null;
        var defW = fields.Length >= 12 ? ParseNumber(fields[11]) : null;
        var fallbackBase = defW ?? limitW ?? 285d;
        minW ??= Math.Max(90d, Math.Round(fallbackBase * 0.35));
        maxW ??= Math.Round(fallbackBase * 1.15);

        return new NvidiaGpuReading(id, ParseBusId(fields[2]), fields[1],
            hasTemperatureAndPower ? ParseNumber(fields[3]) : null,
            hasTemperatureAndPower ? ParseNumber(fields[4]) : null,
            hasFan ? ParseNumber(fields[5]) : null,
            hasClocks ? ParseNumber(fields[hasFan ? 6 : 5]) : null,
            hasClocks ? ParseNumber(fields[hasFan ? 7 : 6]) : null,
            null,
            limitW,
            minW,
            maxW,
            defW,
            PState: fields.Length >= 15 && !string.IsNullOrWhiteSpace(fields[12]) ? fields[12] : null,
            CoreUtilizationPercent: fields.Length >= 15 ? ParseNumber(fields[13]) : null,
            MemoryUtilizationPercent: fields.Length >= 15 ? ParseNumber(fields[14]) : null);
    }

    private static int? ParseBusId(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value, @"^[0-9a-fA-F]+:([0-9a-fA-F]+):[0-9a-fA-F]+\.[0-9]+$");
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bus) ? bus : null;
    }

    private static double? ParseNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number >= 0 ? number : null;

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    /// <summary>Direct native NVML reader and controller (<0.2 ms overhead, zero child processes, reads GDDR6X VRAM temp, PCIe link, P-State, utilization, enforced power limit, hardware energy counter, and throttle bitmask).</summary>
    private static class NvmlNativeReader
    {
        private const uint NvmlTemperatureGpu = 0;
        private const uint NvmlClockGraphics = 0;
        private const uint NvmlClockMem = 2;
        private const uint NvmlFieldIdMemoryTemp = 82;

        private static readonly object Sync = new();
        private static bool _resolverRegistered;
        private static bool _initialized;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct NvmlPciInfo
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
            public string busIdLegacy;
            public uint domain;
            public uint bus;
            public uint device;
            public uint pciDeviceId;
            public uint pciSubSystemId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string busId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NvmlUtilization
        {
            public uint gpu;
            public uint memory;
        }

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        private struct NvmlValueUnion
        {
            [FieldOffset(0)] public double dVal;
            [FieldOffset(0)] public int siVal;
            [FieldOffset(0)] public long sllVal;
            [FieldOffset(0)] public uint uiVal;
            [FieldOffset(0)] public ulong ullVal;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NvmlFieldValue
        {
            public uint fieldId;
            public uint scopeId;
            public long timestamp;
            public long latencyUsec;
            public int valueType;
            public int nvmlReturn;
            public NvmlValueUnion value;
        }

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2", ExactSpelling = true)]
        private static extern int nvmlInit_v2();

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2", ExactSpelling = true)]
        private static extern int nvmlDeviceGetCount_v2(out uint deviceCount);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2", ExactSpelling = true)]
        private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPciInfo_v3", ExactSpelling = true)]
        private static extern int nvmlDeviceGetPciInfo_v3(IntPtr device, out NvmlPciInfo pci);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature", ExactSpelling = true)]
        private static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint temp);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage", ExactSpelling = true)]
        private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint powerMilliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementLimit", ExactSpelling = true)]
        private static extern int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint limitMilliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetEnforcedPowerLimit", ExactSpelling = true)]
        private static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint limitMilliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetPowerManagementLimit", ExactSpelling = true)]
        private static extern int nvmlDeviceSetPowerManagementLimit(IntPtr device, uint limitMilliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints", ExactSpelling = true)]
        private static extern int nvmlDeviceGetPowerManagementLimitConstraints(IntPtr device, out uint minLimitMilliwatts, out uint maxLimitMilliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementDefaultLimit", ExactSpelling = true)]
        private static extern int nvmlDeviceGetPowerManagementDefaultLimit(IntPtr device, out uint defaultLimitMilliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTotalEnergyConsumption", ExactSpelling = true)]
        private static extern int nvmlDeviceGetTotalEnergyConsumption(IntPtr device, out ulong energyMillijoules);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeed", ExactSpelling = true)]
        private static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint speed);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetFanSpeed_v2", ExactSpelling = true)]
        private static extern int nvmlDeviceSetFanSpeed_v2(IntPtr device, uint fan, uint speed);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetDefaultFanSpeed_v2", ExactSpelling = true)]
        private static extern int nvmlDeviceSetDefaultFanSpeed_v2(IntPtr device, uint fan);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo", ExactSpelling = true)]
        private static extern int nvmlDeviceGetClockInfo(IntPtr device, uint type, out uint clockMhz);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPerformanceState", ExactSpelling = true)]
        private static extern int nvmlDeviceGetPerformanceState(IntPtr device, out uint pState);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates", ExactSpelling = true)]
        private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons", ExactSpelling = true)]
        private static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong clocksThrottleReasons);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCurrPcieLinkGeneration", ExactSpelling = true)]
        private static extern int nvmlDeviceGetCurrPcieLinkGeneration(IntPtr device, out uint currLinkGen);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCurrPcieLinkWidth", ExactSpelling = true)]
        private static extern int nvmlDeviceGetCurrPcieLinkWidth(IntPtr device, out uint currLinkWidth);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFieldValues", ExactSpelling = true)]
        private static extern int nvmlDeviceGetFieldValues(IntPtr device, int valuesCount, [In, Out] NvmlFieldValue[] values);

        private static bool EnsureInitialized()
        {
            if (!_resolverRegistered)
            {
                _resolverRegistered = true;
                NativeLibrary.SetDllImportResolver(typeof(NvmlNativeReader).Assembly, ResolveNvmlLibrary);
            }
            if (!_initialized)
            {
                if (nvmlInit_v2() != 0) return false;
                _initialized = true;
            }
            return true;
        }

        public static bool TrySetPowerLimits(IReadOnlyList<(int GpuId, int Watts)> targets)
        {
            if (targets.Count == 0) return false;
            try
            {
                lock (Sync)
                {
                    if (!EnsureInitialized()) return false;
                    foreach (var (gpuId, watts) in targets)
                    {
                        if (gpuId < 0 || nvmlDeviceGetHandleByIndex_v2((uint)gpuId, out var handle) != 0)
                            return false;
                        if (nvmlDeviceSetPowerManagementLimit(handle, (uint)(watts * 1000)) != 0)
                            return false;
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool TrySetFanSpeeds(IReadOnlyList<(int GpuId, int TargetFanPercent)> targets)
        {
            if (targets.Count == 0) return false;
            try
            {
                lock (Sync)
                {
                    if (!EnsureInitialized()) return false;
                    var anyApplied = false;
                    foreach (var (gpuId, pct) in targets)
                    {
                        if (gpuId < 0 || nvmlDeviceGetHandleByIndex_v2((uint)gpuId, out var handle) != 0)
                            continue;
                        var clamped = (uint)Math.Clamp(pct, 30, 100);
                        if (nvmlDeviceSetFanSpeed_v2(handle, 0, clamped) == 0)
                        {
                            _ = nvmlDeviceSetFanSpeed_v2(handle, 1, clamped);
                            anyApplied = true;
                        }
                    }
                    return anyApplied;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool TryResetFanSpeedsToDefault(IEnumerable<int> gpuIds)
        {
            try
            {
                lock (Sync)
                {
                    if (!EnsureInitialized()) return false;
                    var anyReset = false;
                    foreach (var gpuId in gpuIds.Distinct())
                    {
                        if (gpuId < 0 || nvmlDeviceGetHandleByIndex_v2((uint)gpuId, out var handle) != 0)
                            continue;
                        if (nvmlDeviceSetDefaultFanSpeed_v2(handle, 0) == 0)
                        {
                            _ = nvmlDeviceSetDefaultFanSpeed_v2(handle, 1);
                            anyReset = true;
                        }
                    }
                    return anyReset;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool TryReadAll(out IReadOnlyList<NvidiaGpuReading> gpus, out string message)
        {
            gpus = [];
            message = string.Empty;
            try
            {
                lock (Sync)
                {
                    if (!EnsureInitialized()) return false;

                    if (nvmlDeviceGetCount_v2(out var count) != 0 || count == 0 || count > 32)
                        return false;

                    var list = new List<NvidiaGpuReading>((int)count);
                    for (uint i = 0; i < count; i++)
                    {
                        if (nvmlDeviceGetHandleByIndex_v2(i, out var handle) != 0) continue;

                        var nameBuilder = new StringBuilder(96);
                        var name = nvmlDeviceGetName(handle, nameBuilder, (uint)nameBuilder.Capacity) == 0
                            ? nameBuilder.ToString().Trim()
                            : $"NVIDIA GPU #{i}";

                        int? busId = nvmlDeviceGetPciInfo_v3(handle, out var pci) == 0 ? (int)pci.bus : null;
                        double? tempC = nvmlDeviceGetTemperature(handle, NvmlTemperatureGpu, out var t) == 0 && t < 200 ? t : null;
                        double? powerW = nvmlDeviceGetPowerUsage(handle, out var mw) == 0 ? Math.Round(mw / 1000d, 1) : null;
                        double? limitW = nvmlDeviceGetPowerManagementLimit(handle, out var limitMw) == 0 ? Math.Round(limitMw / 1000d, 1) : null;
                        double? enforcedW = nvmlDeviceGetEnforcedPowerLimit(handle, out var enfMw) == 0 ? Math.Round(enfMw / 1000d, 1) : null;
                        double? defaultLimitW = nvmlDeviceGetPowerManagementDefaultLimit(handle, out var defMw) == 0 ? Math.Round(defMw / 1000d, 1) : null;
                        double? minLimitW = null;
                        double? maxLimitW = null;
                        if (nvmlDeviceGetPowerManagementLimitConstraints(handle, out var minMw, out var maxMw) == 0 && minMw > 0 && maxMw >= minMw)
                        {
                            minLimitW = Math.Round(minMw / 1000d, 1);
                            maxLimitW = Math.Round(maxMw / 1000d, 1);
                        }
                        else
                        {
                            var fallbackBase = defaultLimitW ?? limitW ?? enforcedW ?? 285d;
                            minLimitW = Math.Max(90d, Math.Round(fallbackBase * 0.35));
                            maxLimitW = Math.Round(fallbackBase * 1.15);
                        }

                        double? fanPct = nvmlDeviceGetFanSpeed(handle, out var fan) == 0 && fan <= 100 ? fan : null;
                        double? cclk = nvmlDeviceGetClockInfo(handle, NvmlClockGraphics, out var gClk) == 0 ? gClk : null;
                        double? mclk = nvmlDeviceGetClockInfo(handle, NvmlClockMem, out var mClk) == 0 ? mClk : null;
                        double? vramTempC = TryReadMemoryTemp(handle);
                        string? pState = nvmlDeviceGetPerformanceState(handle, out var ps) == 0 && ps <= 15 ? $"P{ps}" : null;
                        double? coreUtil = null;
                        double? memUtil = null;
                        if (nvmlDeviceGetUtilizationRates(handle, out var util) == 0)
                        {
                            if (util.gpu <= 100) coreUtil = util.gpu;
                            if (util.memory <= 100) memUtil = util.memory;
                        }
                        double? hwEnergyKwh = nvmlDeviceGetTotalEnergyConsumption(handle, out var totalMj) == 0 && totalMj > 0
                            ? Math.Round(totalMj / 3_600_000_000d, 6)
                            : null;
                        string? throttle = nvmlDeviceGetCurrentClocksThrottleReasons(handle, out var mask) == 0
                            ? FormatThrottleMask(mask)
                            : null;
                        string? pcieLink = nvmlDeviceGetCurrPcieLinkGeneration(handle, out var gen) == 0 && nvmlDeviceGetCurrPcieLinkWidth(handle, out var width) == 0 && gen > 0 && width > 0
                            ? $"PCIe Gen{gen} x{width}"
                            : null;

                        list.Add(new NvidiaGpuReading(
                            (int)i,
                            busId,
                            name,
                            tempC,
                            powerW,
                            fanPct,
                            cclk,
                            mclk,
                            vramTempC,
                            limitW,
                            minLimitW,
                            maxLimitW,
                            defaultLimitW,
                            throttle,
                            pcieLink,
                            pState,
                            coreUtil,
                            memUtil,
                            enforcedW,
                            hwEnergyKwh,
                            "nvml"));
                    }

                    if (list.Count == 0) return false;
                    gpus = list;
                    message = $"Прямой NVML подключён ({list.Count} GPU)";
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static double? TryReadMemoryTemp(IntPtr handle)
        {
            var fields = new NvmlFieldValue[1];
            fields[0].fieldId = NvmlFieldIdMemoryTemp;
            if (nvmlDeviceGetFieldValues(handle, 1, fields) != 0 || fields[0].nvmlReturn != 0)
                return null;
            var temp = fields[0].value.uiVal;
            return temp is > 0 and < 150 ? temp : null;
        }

        private static string FormatThrottleMask(ulong mask)
        {
            if (mask == 0) return "Норма (без ограничений)";
            if ((mask & 0x0000000000000001UL) != 0) return "Простой (GPU Idle)";
            var reasons = new List<string>(4);
            if ((mask & 0x0000000000000020UL) != 0 || (mask & 0x0000000000000040UL) != 0)
                reasons.Add("Термо-троттлинг GPU");
            if ((mask & 0x0000000000000200UL) != 0)
                reasons.Add("Термо-троттлинг памяти");
            if ((mask & 0x0000000000000008UL) != 0 || (mask & 0x0000000000000080UL) != 0)
                reasons.Add("Аппаратное питание");
            if ((mask & 0x0000000000000004UL) != 0)
                reasons.Add("Лимит мощности (PL)");
            if ((mask & 0x0000000000000002UL) != 0)
                reasons.Add("Профиль частот");
            return reasons.Count == 0 ? "Штатная работа" : string.Join(", ", reasons);
        }

        private static IntPtr ResolveNvmlLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!string.Equals(libraryName, "nvml.dll", StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;

            var resolvedPath = ResolveNvmlPath();
            if (!string.IsNullOrWhiteSpace(resolvedPath) && NativeLibrary.TryLoad(resolvedPath, out var handle))
                return handle;

            return NativeLibrary.TryLoad("nvml.dll", assembly, searchPath, out var defaultHandle) ? defaultHandle : IntPtr.Zero;
        }
    }
}
