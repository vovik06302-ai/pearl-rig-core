using System.IO;
using System.Text;

namespace PearlRigCore.Services;

/// <summary>Thread-safe local rolling file logger stored in %LOCALAPPDATA%\PearlRigControl\logs\app.log.</summary>
public static class AppLogger
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private static readonly object Sync = new();

    public static string GetLogFilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PearlRigControl", "logs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "app.log");
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            var path = GetLogFilePath();
            var line = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [").Append(level).Append("] ")
                .Append(message);

            if (exception is not null)
            {
                line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
                if (!string.IsNullOrWhiteSpace(exception.StackTrace))
                    line.AppendLine().Append(exception.StackTrace);
            }

            lock (Sync)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length >= MaxLogBytes)
                {
                    var backup = path + ".1";
                    File.Move(path, backup, overwrite: true);
                }
                File.AppendAllText(path, line.AppendLine().ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
            // Never throw from diagnostic logger.
        }
    }
}
