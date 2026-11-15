using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PearlRigCore.Services;

/// <summary>Manages Windows power sleep prevention and inspects Windows System Event Log for NVIDIA TDR resets (Event 4101 / nvlddmkm).</summary>
public static class WindowsDiagnosticsService
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    public static void SetMiningSleepPrevention(bool preventSleep)
    {
        try
        {
            var flags = preventSleep ? (EsContinuous | EsSystemRequired) : EsContinuous;
            _ = SetThreadExecutionState(flags);
        }
        catch
        {
            // Ignore on restricted environments.
        }
    }

    public static async Task<string?> CheckRecentNvidiaDriverResetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var wevtutil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wevtutil.exe");
            if (!File.Exists(wevtutil)) return null;

            const string query = "qe System /q:\"*[System[(EventID=4101 or Provider[@Name='nvlddmkm']) and TimeCreated[timediff(@SystemTime) <= 180000]]]\" /c:1 /rd:true /f:text";
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = wevtutil,
                    Arguments = query,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                }
            };
            if (!process.Start()) return null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                var singleLine = string.Join(' ', output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                return singleLine.Length <= 220 ? singleLine : singleLine[..220] + "…";
            }
        }
        catch
        {
            // Non-critical diagnostic helper.
        }
        return null;
    }
}
