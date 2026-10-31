using System.Diagnostics;
using System.Globalization;
using System.IO;

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
    double? VramTempC);

public sealed record NvidiaReadResult(bool Available, string Message, IReadOnlyList<NvidiaGpuReading> Gpus);

public sealed class NvidiaSmiClient
{
    private const string Query = "--query-gpu=index,name,pci.bus_id,temperature.gpu,power.draw,fan.speed,clocks.current.graphics,clocks.current.memory,temperature.memory --format=csv,noheader,nounits";

    public async Task<NvidiaReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        var executable = ResolveExecutable();
        if (executable is null)
            return new(false, "nvidia-smi не найден. Проверьте драйвер NVIDIA.", []);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = Query,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            }
        };

        try
        {
            if (!process.Start()) return new(false, "Не удалось запустить nvidia-smi.", []);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return new(false, "nvidia-smi не ответил за 4 секунды.", []);
            }

            var output = await stdoutTask;
            var error = await stderrTask;
            if (process.ExitCode != 0)
                return new(false, string.IsNullOrWhiteSpace(error) ? "nvidia-smi завершился с ошибкой." : error.Trim(), []);

            var readings = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseLine)
                .Where(reading => reading is not null)
                .Cast<NvidiaGpuReading>()
                .ToArray();

            return readings.Length == 0
                ? new(false, "nvidia-smi не вернул видеокарты.", [])
                : new(true, $"Получены датчики {readings.Length} видеокарт из nvidia-smi.", readings);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new(false, $"Не удалось прочитать датчики NVIDIA: {ex.Message}", []);
        }
    }

    private static string? ResolveExecutable()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var systemCopy = Path.Combine(system, "nvidia-smi.exe");
        if (File.Exists(systemCopy)) return systemCopy;

        var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim(), "nvidia-smi.exe"))
            .FirstOrDefault(File.Exists);
        return path;
    }

    private static NvidiaGpuReading? ParseLine(string line)
    {
        var fields = line.Split(',').Select(field => field.Trim()).ToArray();
        if (fields.Length != 9 || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return null;
        return new NvidiaGpuReading(
            id,
            ParseBusId(fields[2]),
            fields[1],
            ParseNumber(fields[3]),
            ParseNumber(fields[4]),
            ParseNumber(fields[5]),
            ParseNumber(fields[6]),
            ParseNumber(fields[7]),
            ParseNumber(fields[8]));
    }

    private static int? ParseBusId(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value, @"^[0-9a-fA-F]+:([0-9a-fA-F]+):[0-9a-fA-F]+\.[0-9]+$");
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bus) ? bus : null;
    }

    private static double? ParseNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number >= 0 ? number : null;
}
