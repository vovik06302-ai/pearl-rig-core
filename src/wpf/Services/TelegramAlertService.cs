using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PearlRigCore.Services;

/// <summary>Sends direct autonomous alerts from the local rig to Telegram Bot API and polls authorized remote control commands.</summary>
public sealed class TelegramAlertService(HttpClient httpClient)
{
    private readonly HttpClient _httpClient = httpClient;
    private DateTimeOffset _lastAlertSentAt = DateTimeOffset.MinValue;
    private long _lastUpdateOffset;
    private bool _offsetInitialized;

    public async Task<(bool Ok, string Message)> SendAsync(string botToken, string chatId, string text, bool bypassCooldown = false, CancellationToken cancellationToken = default)
    {
        botToken = botToken.Trim();
        chatId = chatId.Trim();
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
            return (false, "Укажите токен бота и Chat ID Telegram.");

        if (!bypassCooldown && DateTimeOffset.UtcNow - _lastAlertSentAt < TimeSpan.FromSeconds(15))
            return (false, "Анти-спам пауза между уведомлениями Telegram.");

        var url = $"https://api.telegram.org/bot{Uri.EscapeDataString(botToken)}/sendMessage";
        var payload = JsonSerializer.Serialize(new
        {
            chat_id = chatId,
            text = $"⚡ Pearl Rig Control ({Environment.MachineName})\n{text}",
            disable_web_page_preview = true
        });

        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(url, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (false, $"Telegram API ответил HTTP {(int)response.StatusCode}");

            _lastAlertSentAt = DateTimeOffset.UtcNow;
            return (true, $"Отправлено в Telegram ({DateTime.Now:HH:mm:ss})");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "Таймаут подключения к Telegram API");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Ошибка сети Telegram: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<string>> PollAuthorizedCommandsAsync(string botToken, string authorizedChatId, CancellationToken cancellationToken = default)
    {
        botToken = botToken.Trim();
        authorizedChatId = authorizedChatId.Trim();
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(authorizedChatId))
            return [];

        var url = _offsetInitialized
            ? $"https://api.telegram.org/bot{Uri.EscapeDataString(botToken)}/getUpdates?offset={_lastUpdateOffset}&timeout=0&allowed_updates=%5B%22message%22%5D"
            : $"https://api.telegram.org/bot{Uri.EscapeDataString(botToken)}/getUpdates?limit=5&timeout=0&allowed_updates=%5B%22message%22%5D";

        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return [];

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
                return [];
            if (!doc.RootElement.TryGetProperty("result", out var resultArray) || resultArray.ValueKind != JsonValueKind.Array)
                return [];

            var commands = new List<string>();
            var wasInitialized = _offsetInitialized;
            foreach (var update in resultArray.EnumerateArray())
            {
                if (!update.TryGetProperty("update_id", out var idProp) || !idProp.TryGetInt64(out var updateId))
                    continue;
                if (updateId >= _lastUpdateOffset)
                    _lastUpdateOffset = updateId + 1;

                if (!wasInitialized)
                    continue; // Skip historical messages on first poll after app startup.

                if (!update.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
                    continue;
                if (!msg.TryGetProperty("chat", out var chat) || !chat.TryGetProperty("id", out var chatIdProp))
                    continue;

                var incomingChatId = chatIdProp.ValueKind switch
                {
                    JsonValueKind.Number when chatIdProp.TryGetInt64(out var num) => num.ToString(CultureInfo.InvariantCulture),
                    JsonValueKind.String => chatIdProp.GetString() ?? string.Empty,
                    _ => string.Empty
                };

                if (!string.Equals(incomingChatId, authorizedChatId, StringComparison.Ordinal))
                    continue;

                if (msg.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
                {
                    var text = (textProp.GetString() ?? string.Empty).Trim();
                    if (text.StartsWith('/'))
                        commands.Add(text);
                }
            }

            _offsetInitialized = true;
            return commands;
        }
        catch
        {
            return [];
        }
    }
}
