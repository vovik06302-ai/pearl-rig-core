using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PearlRigCore.Services;

/// <summary>Sends direct autonomous alerts from the local rig to Telegram Bot API.</summary>
public sealed class TelegramAlertService(HttpClient httpClient)
{
    private readonly HttpClient _httpClient = httpClient;
    private DateTimeOffset _lastAlertSentAt = DateTimeOffset.MinValue;

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
}
