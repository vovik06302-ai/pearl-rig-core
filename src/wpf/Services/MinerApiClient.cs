using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using PearlRigCore.Models;

namespace PearlRigCore.Services;

public sealed record MinerApiResult(
    bool Available,
    string Message,
    bool? PoolConnected,
    string? PoolUrl,
    double? TotalHashrateTHs,
    long? AcceptedShares,
    long? RejectedShares,
    IReadOnlyList<MinerGpuReading> Devices);

public sealed record MinerGpuReading(
    int Id,
    int? BusId,
    string Name,
    double? HashrateTHs,
    long? AcceptedShares,
    long? RejectedShares,
    double? TempC,
    double? VramTempC,
    double? FanPercent,
    double? PowerWatts,
    double? CclkMHz,
    double? MclkMHz);

public sealed class MinerApiClient(HttpClient httpClient)
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<MinerApiResult> ReadAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"http://127.0.0.1:{port}/", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Unavailable($"API майнера ответил HTTP {(int)response.StatusCode}.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!TryNumber(root, "last_update", out var lastUpdate) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastUpdate) > 20)
                return Unavailable("API майнера недоступен или его данные устарели.");

            var devices = ReadArray(root, "devices");
            var algorithm = ReadArray(root, "algos").FirstOrDefault(item => ReadString(item, "name") == "pearlhash");
            if (algorithm.ValueKind != JsonValueKind.Object)
                return Unavailable("Свежий ответ API не содержит алгоритм pearlhash.");

            var rates = ReadArray(algorithm, "hashrates");
            var acceptedPerGpu = ReadArray(algorithm, "devices_accepted_shares");
            var rejectedPerGpu = ReadArray(algorithm, "devices_rejected_shares");
            var readings = devices.Select((device, position) => new MinerGpuReading(
                ReadInt(device, "id") ?? position,
                ReadInt(device, "bus_id"),
                ReadString(device, "name") ?? $"GPU {position}",
                At(rates, position) is double rate ? rate / 1_000_000_000_000d : null,
                At(acceptedPerGpu, position) is double accepted ? (long)accepted : null,
                At(rejectedPerGpu, position) is double rejected ? (long)rejected : null,
                ReadDouble(device, "temp"),
                ReadDouble(device, "mem_temp"),
                ReadDouble(device, "fan"),
                ReadDouble(device, "power"),
                ReadDouble(device, "cclk"),
                ReadDouble(device, "mclk"))).ToArray();

            var poolStatus = ReadString(algorithm, "pool_status")?.Trim().ToLowerInvariant();
            bool? connected = ReadBool(algorithm, "pool_connected") ?? poolStatus switch
            {
                "connected" or "online" or "active" => true,
                "disconnected" or "offline" or "inactive" or "connecting" or "reconnecting" => false,
                _ => null
            };

            return new MinerApiResult(
                true,
                "API майнера подключён; данные свежие.",
                connected,
                ReadString(algorithm, "pool"),
                ReadDouble(algorithm, "total_hashrate") is double total ? total / 1_000_000_000_000d : null,
                ReadLong(algorithm, "total_accepted_shares"),
                ReadLong(algorithm, "total_rejected_shares"),
                readings);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("Истекло время ожидания API майнера.");
        }
        catch (HttpRequestException)
        {
            return Unavailable($"Майнер не отвечает на 127.0.0.1:{port}.");
        }
        catch (JsonException)
        {
            return Unavailable("API майнера вернул некорректный JSON.");
        }
        catch (InvalidOperationException ex)
        {
            return Unavailable($"Некорректный ответ API майнера: {ex.Message}");
        }
    }

    private static MinerApiResult Unavailable(string message) => new(false, message, null, null, null, null, null, []);

    private static JsonElement[] ReadArray(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray()
            : [];

    private static double? At(JsonElement[] array, int index) => index >= 0 && index < array.Length ? Number(array[index]) : null;

    private static string? ReadString(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBool(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static double? ReadDouble(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var value) ? Number(value) : null;

    private static long? ReadLong(JsonElement parent, string property) => ReadDouble(parent, property) is double value ? (long)value : null;

    private static int? ReadInt(JsonElement parent, string property) => ReadDouble(parent, property) is double value ? (int)value : null;

    private static bool TryNumber(JsonElement parent, string property, out long value)
    {
        value = 0;
        return ReadDouble(parent, property) is double number && (value = (long)number) >= 0;
    }

    private static double? Number(JsonElement value)
    {
        double number;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) && double.IsFinite(number) && number >= 0)
            return number;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number) && number >= 0)
            return number;
        return null;
    }
}
