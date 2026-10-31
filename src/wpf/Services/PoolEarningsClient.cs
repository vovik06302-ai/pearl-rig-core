using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PearlRigCore.Models;

namespace PearlRigCore.Services;

public sealed class PoolEarningsClient(HttpClient httpClient)
{
    private const string PoolApi = "https://pool.kryptex.com/prl/api/v1/miner/";
    private const string PriceApi = "https://pool.kryptex.com/api/v1/coin/prl/price/chart";
    private const string CbrApi = "https://www.cbr.ru/scripts/XML_daily.asp";
    private static readonly Regex WalletPattern = new(@"^prl1p[qpzry9x8gf2tvdw0s3jn54khce6mua7l]{58}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly HttpClient _httpClient = httpClient;

    public static bool IsValidWallet(string? value) => value is not null && WalletPattern.IsMatch(value.Trim());

    public async Task<PoolEarningsSnapshot> ReadAsync(string wallet, CancellationToken cancellationToken)
    {
        wallet = wallet.Trim();
        if (!IsValidWallet(wallet))
            return Empty("Сохраните корректный публичный адрес PRL, чтобы получить баланс из Kryptex.");

        var requests = new[]
        {
            FetchJsonAsync(PoolApi + "balance/" + Uri.EscapeDataString(wallet), cancellationToken),
            FetchJsonAsync(PoolApi + "payouts/" + Uri.EscapeDataString(wallet) + "/stats", cancellationToken),
            FetchJsonAsync(PriceApi, cancellationToken),
            FetchTextAsync(CbrApi, cancellationToken)
        };

        var results = await Task.WhenAll(requests);
        var balance = results[0].Json;
        var stats = results[1].Json;
        var priceChart = results[2].Json;
        var cbrXml = results[3].Text;

        var confirmed = ReadNumber(balance, "confirmed");
        var unconfirmed = ReadNumber(balance, "unconfirmed");
        var total = ReadNumber(balance, "total");
        var threshold = ReadNumber(balance, "threshold");
        var paid = ReadNumber(stats, "paid");
        var balanceAvailable = confirmed is not null || unconfirmed is not null || total is not null;

        var prlUsd = FindLatestPrice(priceChart);
        var usdRub = ParseUsdRub(cbrXml);
        var ratesAvailable = prlUsd is not null && usdRub is not null;
        var message = balanceAvailable
            ? results.Take(2).Any(result => result.Error is not null) ? "Баланс загружен частично из Kryptex." : "Баланс получен из Kryptex Pool API."
            : "Kryptex не вернул баланс. Проверьте адрес и подключение к сети.";

        return new PoolEarningsSnapshot(
            balanceAvailable,
            ratesAvailable,
            results.Any(result => result.Error is not null),
            message,
            DateTimeOffset.Now,
            confirmed,
            unconfirmed,
            total,
            threshold,
            paid,
            prlUsd,
            usdRub,
            ratesAvailable ? prlUsd * usdRub : null);
    }

    private async Task<FetchResult> FetchJsonAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return new(null, null, $"HTTP {(int)response.StatusCode}");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return new(document.RootElement.Clone(), null, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(null, null, "Таймаут запроса"); }
        catch (HttpRequestException ex) { return new(null, null, ex.Message); }
        catch (InvalidOperationException ex) { return new(null, null, ex.Message); }
        catch (JsonException) { return new(null, null, "Некорректный JSON"); }
    }

    private async Task<FetchResult> FetchTextAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return new(null, null, $"HTTP {(int)response.StatusCode}");
            // Parse the XML from bytes so a malformed/unsupported HTTP charset header
            // cannot prevent us reading the ASCII XML tags and numeric exchange rate.
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return new(null, System.Text.Encoding.UTF8.GetString(bytes), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(null, null, "Таймаут запроса"); }
        catch (HttpRequestException ex) { return new(null, null, ex.Message); }
        catch (InvalidOperationException ex) { return new(null, null, ex.Message); }
    }

    private static double? ReadNumber(JsonElement? element, string name)
    {
        if (element is not JsonElement root || root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)) return null;
        return Number(value);
    }

    private static double? FindLatestPrice(JsonElement? element)
    {
        if (element is not JsonElement array || array.ValueKind != JsonValueKind.Array) return null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double? latestPrice = null;
        long latestTimestamp = -1;
        foreach (var point in array.EnumerateArray())
        {
            var timestamp = ReadNumber(point, "timestamp");
            var price = ReadNumber(point, "price");
            if (timestamp is double t && price is double p && t <= now && t > latestTimestamp)
            {
                latestTimestamp = (long)t;
                latestPrice = p;
            }
        }
        return latestPrice;
    }

    private static double? ParseUsdRub(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var document = XDocument.Parse(xml);
            var value = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Valute" && element.Elements().Any(child => child.Name.LocalName == "CharCode" && child.Value == "USD"));
            var text = value?.Elements().FirstOrDefault(element => element.Name.LocalName == "Value")?.Value.Replace(',', '.');
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && result > 0 && double.IsFinite(result) ? result : null;
        }
        catch (System.Xml.XmlException) { return null; }
    }

    private static double? Number(JsonElement value)
    {
        double number;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) && double.IsFinite(number) && number >= 0) return number;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number) && number >= 0) return number;
        return null;
    }

    private static PoolEarningsSnapshot Empty(string message) => new(false, false, false, message, DateTimeOffset.Now, null, null, null, null, null, null, null, null);

    private sealed record FetchResult(JsonElement? Json, string? Text, string? Error);
}
