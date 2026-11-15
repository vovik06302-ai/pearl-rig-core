using System.Diagnostics;
using System.Net.Sockets;

namespace PearlRigCore.Services;

public sealed record PoolPingResult(
    int? PrimaryPingMs,
    int? BackupPingMs,
    string SummaryText);

/// <summary>Measures real TCP handshake latency to primary and backup Kryptex Stratum pools.</summary>
public sealed class PoolPingService
{
    public async Task<PoolPingResult> MeasureAsync(string primaryPoolUrl, string backupPoolUrl, CancellationToken cancellationToken)
    {
        var primaryTask = PingEndpointAsync(primaryPoolUrl, cancellationToken);
        var backupTask = PingEndpointAsync(backupPoolUrl, cancellationToken);
        await Task.WhenAll(primaryTask, backupTask);

        var primaryMs = await primaryTask;
        var backupMs = await backupTask;

        var ruText = primaryMs is int p ? $"{p} мс" : "недоступен";
        var euText = backupMs is int b ? $"{b} мс" : "недоступен";
        var summary = $"Пинг RU: {ruText} · EU: {euText}";

        return new PoolPingResult(primaryMs, backupMs, summary);
    }

    private static async Task<int?> PingEndpointAsync(string stratumUrl, CancellationToken cancellationToken)
    {
        if (!TryParseHostAndPort(stratumUrl, out var host, out var port))
            return null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(2200));
            using var client = new TcpClient();
            var sw = Stopwatch.StartNew();
            await client.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return (int)Math.Max(1, sw.ElapsedMilliseconds);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseHostAndPort(string rawUrl, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(rawUrl)) return false;

        var trimmed = rawUrl.Trim();
        var schemeIdx = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
            trimmed = trimmed[(schemeIdx + 3)..];

        var slashIdx = trimmed.IndexOf('/');
        if (slashIdx >= 0)
            trimmed = trimmed[..slashIdx];

        var colonIdx = trimmed.LastIndexOf(':');
        if (colonIdx <= 0 || colonIdx == trimmed.Length - 1) return false;

        host = trimmed[..colonIdx].Trim();
        return !string.IsNullOrWhiteSpace(host) && int.TryParse(trimmed[(colonIdx + 1)..], out port) && port is > 0 and <= 65535;
    }
}
