using GalleryBrowser.Models;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace GalleryBrowser.Services;

internal sealed class DiscordNotificationService
{
    private const string ChannelName = "discord";
    private const int MaximumMessageLength = 1_900;
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly GalleryDatabase _database;
    private readonly StorageSettingsStore _settingsStore;

    public DiscordNotificationService(GalleryDatabase database, StorageSettingsStore settingsStore)
    {
        _database = database;
        _settingsStore = settingsStore;
    }

    public DiscordNotificationSettingsDto GetSettings() =>
        _settingsStore.GetDiscordNotificationSettings();

    public void SaveSettings(
        bool enabled,
        string? webhookUrl,
        bool notifyCreatorFollowAlert,
        bool notifySubscriptionEnding,
        bool notifySubscriptionReminder,
        bool notifyScheduledScanStarted,
        bool notifyScheduledScanCompleted)
    {
        if (!string.IsNullOrWhiteSpace(webhookUrl))
        {
            _ = NormalizeWebhookUri(webhookUrl);
        }

        _settingsStore.SaveDiscordNotificationConfiguration(
            enabled,
            webhookUrl,
            notifyCreatorFollowAlert,
            notifySubscriptionEnding,
            notifySubscriptionReminder,
            notifyScheduledScanStarted,
            notifyScheduledScanCompleted);
    }

    public void Disconnect() => _settingsStore.DisconnectDiscordNotifications();

    public async Task SendTestAsync(string? webhookUrl, CancellationToken cancellationToken)
    {
        var uri = ResolveWebhookUri(webhookUrl);
        await SendWebhookAsync(
            uri,
            "**GalleryBrowser Discord通知テスト**\nDiscord Webhookとの接続を確認しました。",
            cancellationToken);
    }

    public Task SendEventTestAsync(
        string eventType,
        string? webhookUrl,
        CancellationToken cancellationToken) =>
        SendWebhookAsync(
            ResolveWebhookUri(webhookUrl),
            NotificationTestMessageFactory.CreateDiscordMessage(eventType),
            cancellationToken);

    public async Task ProcessDueNotificationsAsync(
        ScheduledNotificationSnapshot snapshot,
        string occurrenceKey,
        CancellationToken cancellationToken)
    {
        var settings = GetSettings();
        if (!settings.Enabled || !settings.HasWebhookUrl)
        {
            return;
        }

        var digest = ScheduledNotificationDigestBuilder.Create(
            snapshot,
            occurrenceKey,
            settings.NotifyCreatorFollowAlert,
            settings.NotifySubscriptionEnding,
            settings.NotifySubscriptionReminder,
            markdown: true,
            MaximumMessageLength);
        if (digest is null ||
            digest.Candidates.All(candidate =>
                _database.WasNotificationDelivered(ChannelName, candidate.EventKey)))
        {
            return;
        }

        try
        {
            await SendWebhookAsync(ResolveWebhookUri(null), digest.Message, cancellationToken);
            foreach (var candidate in digest.Candidates)
            {
                _database.RecordNotificationDelivery(
                    ChannelName,
                    candidate.EventKey,
                    succeeded: true,
                    digest.Message);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            foreach (var candidate in digest.Candidates)
            {
                _database.RecordNotificationDelivery(
                    ChannelName,
                    candidate.EventKey,
                    succeeded: false,
                    digest.Message,
                    exception.Message);
            }
            throw;
        }
    }

    public Task NotifyScheduledScanStartedAsync(
        IReadOnlyList<string> categoryLabels,
        DateTimeOffset startedAt,
        TimeSpan? estimatedDuration,
        CancellationToken cancellationToken)
    {
        var settings = GetSettings();
        if (!settings.Enabled || !settings.HasWebhookUrl || !settings.NotifyScheduledScanStarted)
        {
            return Task.CompletedTask;
        }

        var estimate = estimatedDuration.HasValue
            ? $"\n予想所要時間: {FormatDuration(estimatedDuration.Value)}"
            : string.Empty;
        return SendOnceAsync(
            ResolveWebhookUri(null),
            CreateEventKey("scheduled-scan-start", startedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            $"**🔄 定期フォルダ走査を開始しました**\n対象: {string.Join(" / ", categoryLabels)}{estimate}",
            cancellationToken);
    }

    public Task NotifyScheduledScanCompletedAsync(
        IReadOnlyList<string> categoryLabels,
        DateTimeOffset startedAt,
        TimeSpan elapsed,
        string resultSummary,
        bool succeeded,
        CancellationToken cancellationToken)
    {
        var settings = GetSettings();
        if (!settings.Enabled || !settings.HasWebhookUrl || !settings.NotifyScheduledScanCompleted)
        {
            return Task.CompletedTask;
        }

        var status = succeeded ? "✅ 完了" : "❌ 失敗";
        return SendOnceAsync(
            ResolveWebhookUri(null),
            CreateEventKey(
                succeeded ? "scheduled-scan-completed" : "scheduled-scan-failed",
                startedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            $"**{status}: 定期フォルダ走査**\n" +
            $"対象: {string.Join(" / ", categoryLabels)}\n" +
            $"経過時間: {FormatDuration(elapsed)}\n" +
            resultSummary,
            cancellationToken);
    }

    private async Task SendOnceAsync(
        Uri webhookUri,
        string eventKey,
        string message,
        CancellationToken cancellationToken)
    {
        if (_database.WasNotificationDelivered(ChannelName, eventKey))
        {
            return;
        }

        var normalizedMessage = TrimMessage(message);
        try
        {
            await SendWebhookAsync(webhookUri, normalizedMessage, cancellationToken);
            _database.RecordNotificationDelivery(ChannelName, eventKey, succeeded: true, normalizedMessage);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _database.RecordNotificationDelivery(
                ChannelName,
                eventKey,
                succeeded: false,
                normalizedMessage,
                exception.Message);
            throw;
        }
    }

    private static async Task SendWebhookAsync(
        Uri webhookUri,
        string message,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            content = TrimMessage(message),
            username = "GalleryBrowser",
            allowed_mentions = new { parse = Array.Empty<string>() }
        };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await HttpClient.PostAsJsonAsync(webhookUri, payload, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests &&
                attempt == 0 &&
                TryReadRetryDelay(responseBody, out var retryDelay))
            {
                await Task.Delay(retryDelay, cancellationToken);
                continue;
            }

            var detail = string.IsNullOrWhiteSpace(responseBody)
                ? response.ReasonPhrase ?? "応答内容なし"
                : responseBody.Trim();
            throw new HttpRequestException(
                $"Discord通知に失敗しました（HTTP {(int)response.StatusCode}）: {detail}",
                null,
                response.StatusCode);
        }
    }

    private Uri ResolveWebhookUri(string? suppliedWebhookUrl)
    {
        var value = string.IsNullOrWhiteSpace(suppliedWebhookUrl)
            ? _settingsStore.GetDiscordWebhookUrl()
            : suppliedWebhookUrl.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Discord Webhook URLが登録されていません。");
        }
        return NormalizeWebhookUri(value);
    }

    private static Uri NormalizeWebhookUri(string webhookUrl)
    {
        if (!Uri.TryCreate(webhookUrl.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !IsDiscordHost(uri.Host) ||
            !uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Discordの公式Webhook URLを入力してください。", nameof(webhookUrl));
        }

        var builder = new UriBuilder(uri);
        var query = builder.Query.TrimStart('?');
        if (!query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Any(part => part.StartsWith("wait=", StringComparison.OrdinalIgnoreCase)))
        {
            builder.Query = string.IsNullOrWhiteSpace(query)
                ? "wait=true"
                : $"{query}&wait=true";
        }
        return builder.Uri;
    }

    private static bool IsDiscordHost(string host) =>
        host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".discordapp.com", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadRetryDelay(string responseBody, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("retry_after", out var retryAfter) ||
                !retryAfter.TryGetDouble(out var seconds))
            {
                return false;
            }
            delay = TimeSpan.FromSeconds(Math.Clamp(seconds, 0.25, 10));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours:N0}時間{duration.Minutes:N0}分";
        }
        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes:N0}分{duration.Seconds:N0}秒";
        }
        return $"{Math.Max(1, (int)Math.Round(duration.TotalSeconds)):N0}秒";
    }

    private static string CreateEventKey(string eventType, string identity)
    {
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity.Trim())));
        return $"{eventType}:{hash[..24]}";
    }

    private static string TrimMessage(string message) =>
        message.Length <= MaximumMessageLength
            ? message
            : message[..(MaximumMessageLength - 1)] + "…";

    private static HttpClient CreateHttpClient() => new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

}
