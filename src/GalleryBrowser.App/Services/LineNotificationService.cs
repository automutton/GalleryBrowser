using GalleryBrowser.Models;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace GalleryBrowser.Services;

internal sealed class LineNotificationService
{
    private const string ChannelName = "line";
    private const int MaximumMessageLength = 4_500;
    private static readonly Uri PushMessageUri = new("https://api.line.me/v2/bot/message/push");
    private static readonly Uri QuotaUri = new("https://api.line.me/v2/bot/message/quota");
    private static readonly Uri ConsumptionUri = new("https://api.line.me/v2/bot/message/quota/consumption");
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly GalleryDatabase _database;
    private readonly StorageSettingsStore _settingsStore;

    public LineNotificationService(GalleryDatabase database, StorageSettingsStore settingsStore)
    {
        _database = database;
        _settingsStore = settingsStore;
    }

    public LineNotificationSettingsDto GetSettings() =>
        _settingsStore.GetLineNotificationSettings();

    public void SaveSettings(
        bool enabled,
        string? channelAccessToken,
        string? recipientUserId,
        bool notifyCreatorFollowAlert,
        bool notifySubscriptionEnding,
        bool notifySubscriptionReminder,
        bool notifyScheduledScanStarted,
        bool notifyScheduledScanCompleted)
    {
        if (!string.IsNullOrWhiteSpace(channelAccessToken))
        {
            ValidateChannelAccessToken(channelAccessToken);
        }
        if (!string.IsNullOrWhiteSpace(recipientUserId))
        {
            ValidateRecipientUserId(recipientUserId);
        }

        _settingsStore.SaveLineNotificationConfiguration(
            enabled,
            channelAccessToken,
            recipientUserId,
            notifyCreatorFollowAlert,
            notifySubscriptionEnding,
            notifySubscriptionReminder,
            notifyScheduledScanStarted,
            notifyScheduledScanCompleted);
    }

    public void Disconnect() => _settingsStore.DisconnectLineNotifications();

    public async Task<LineMessageQuotaDto> GetQuotaAsync(
        string? channelAccessToken,
        CancellationToken cancellationToken)
    {
        var token = ResolveChannelAccessToken(channelAccessToken);
        using var quotaDocument = await GetJsonAsync(QuotaUri, token, cancellationToken);
        using var consumptionDocument = await GetJsonAsync(ConsumptionUri, token, cancellationToken);

        var type = quotaDocument.RootElement.TryGetProperty("type", out var typeProperty)
            ? typeProperty.GetString()?.Trim().ToLowerInvariant() ?? "unknown"
            : "unknown";
        long? limit = null;
        if (quotaDocument.RootElement.TryGetProperty("value", out var valueProperty) &&
            valueProperty.TryGetInt64(out var value))
        {
            limit = value;
        }

        var consumption = consumptionDocument.RootElement.TryGetProperty("totalUsage", out var usageProperty) &&
                          usageProperty.TryGetInt64(out var totalUsage)
            ? totalUsage
            : 0;
        return new LineMessageQuotaDto(type, limit, consumption, DateTimeOffset.Now);
    }

    public async Task<LineMessageQuotaDto> SendTestAsync(
        string? channelAccessToken,
        string? recipientUserId,
        CancellationToken cancellationToken)
    {
        var credentials = ResolveCredentials(channelAccessToken, recipientUserId);
        return await SendPushAsync(
            credentials,
            "GalleryBrowser LINE通知テスト\nLINE Messaging APIとの接続を確認しました。",
            cancellationToken);
    }

    public async Task<LineMessageQuotaDto> SendEventTestAsync(
        string eventType,
        string? channelAccessToken,
        string? recipientUserId,
        CancellationToken cancellationToken)
    {
        var credentials = ResolveCredentials(channelAccessToken, recipientUserId);
        return await SendPushAsync(
            credentials,
            NotificationTestMessageFactory.CreateLineMessage(eventType),
            cancellationToken);
    }

    public async Task ProcessDueNotificationsAsync(
        ScheduledNotificationSnapshot snapshot,
        string occurrenceKey,
        CancellationToken cancellationToken)
    {
        var settings = GetSettings();
        if (!settings.Enabled || !settings.HasChannelAccessToken || !settings.HasRecipientUserId)
        {
            return;
        }

        var digest = ScheduledNotificationDigestBuilder.Create(
            snapshot,
            occurrenceKey,
            settings.NotifyCreatorFollowAlert,
            settings.NotifySubscriptionEnding,
            settings.NotifySubscriptionReminder,
            markdown: false,
            MaximumMessageLength);
        if (digest is null ||
            digest.Candidates.All(candidate =>
                _database.WasNotificationDelivered(ChannelName, candidate.EventKey)))
        {
            return;
        }

        try
        {
            await SendPushAsync(ResolveCredentials(null, null), digest.Message, cancellationToken);
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
        if (!settings.Enabled ||
            !settings.HasChannelAccessToken ||
            !settings.HasRecipientUserId ||
            !settings.NotifyScheduledScanStarted)
        {
            return Task.CompletedTask;
        }

        var estimate = estimatedDuration.HasValue
            ? $"\n予想所要時間: {FormatDuration(estimatedDuration.Value)}"
            : string.Empty;
        return SendOnceAsync(
            ResolveCredentials(null, null),
            CreateEventKey("scheduled-scan-start", startedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            $"🔄 定期フォルダ走査を開始しました\n対象: {string.Join(" / ", categoryLabels)}{estimate}",
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
        if (!settings.Enabled ||
            !settings.HasChannelAccessToken ||
            !settings.HasRecipientUserId ||
            !settings.NotifyScheduledScanCompleted)
        {
            return Task.CompletedTask;
        }

        var status = succeeded ? "✅ 完了" : "❌ 失敗";
        return SendOnceAsync(
            ResolveCredentials(null, null),
            CreateEventKey(
                succeeded ? "scheduled-scan-completed" : "scheduled-scan-failed",
                startedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            $"{status}: 定期フォルダ走査\n" +
            $"対象: {string.Join(" / ", categoryLabels)}\n" +
            $"経過時間: {FormatDuration(elapsed)}\n" +
            resultSummary,
            cancellationToken);
    }

    private async Task SendOnceAsync(
        LineCredentials credentials,
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
            await SendPushAsync(credentials, normalizedMessage, cancellationToken);
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

    private async Task<LineMessageQuotaDto> SendPushAsync(
        LineCredentials credentials,
        string message,
        CancellationToken cancellationToken)
    {
        var quota = await GetQuotaAsync(credentials.ChannelAccessToken, cancellationToken);
        if (string.Equals(quota.Type, "limited", StringComparison.OrdinalIgnoreCase) &&
            quota.Limit.HasValue &&
            quota.Consumption >= quota.Limit.Value)
        {
            throw new InvalidOperationException(
                $"LINE Messaging APIの今月の無料メッセージ上限に達しています（{quota.Consumption:N0}/{quota.Limit.Value:N0}通）。");
        }

        var payload = new
        {
            to = credentials.RecipientUserId,
            messages = new[]
            {
                new
                {
                    type = "text",
                    text = TrimMessage(message)
                }
            },
            notificationDisabled = false
        };
        var retryKey = Guid.NewGuid().ToString();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, PushMessageUri)
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.ChannelAccessToken);
            request.Headers.TryAddWithoutValidation("X-Line-Retry-Key", retryKey);

            using var response = await HttpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return quota with { Consumption = quota.Consumption + 1 };
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(retryAfter.TotalSeconds, 0.5, 5)), cancellationToken);
                continue;
            }

            throw CreateLineApiException(response.StatusCode, response.ReasonPhrase, responseBody);
        }

        return quota;
    }

    private static async Task<JsonDocument> GetJsonAsync(
        Uri uri,
        string channelAccessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", channelAccessToken);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateLineApiException(response.StatusCode, response.ReasonPhrase, responseBody);
        }
        try
        {
            return JsonDocument.Parse(responseBody);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("LINE Messaging APIの応答を読み取れませんでした。", exception);
        }
    }

    private LineCredentials ResolveCredentials(
        string? suppliedChannelAccessToken,
        string? suppliedRecipientUserId)
    {
        var stored = _settingsStore.GetLineCredentials();
        var token = string.IsNullOrWhiteSpace(suppliedChannelAccessToken)
            ? stored.ChannelAccessToken
            : suppliedChannelAccessToken.Trim();
        var recipientUserId = string.IsNullOrWhiteSpace(suppliedRecipientUserId)
            ? stored.RecipientUserId
            : suppliedRecipientUserId.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("LINEのチャネルアクセストークンが登録されていません。");
        }
        if (string.IsNullOrWhiteSpace(recipientUserId))
        {
            throw new InvalidOperationException("LINEの受信先User IDが登録されていません。");
        }
        ValidateChannelAccessToken(token);
        ValidateRecipientUserId(recipientUserId);
        return new LineCredentials(token, recipientUserId);
    }

    private string ResolveChannelAccessToken(string? suppliedChannelAccessToken)
    {
        var token = string.IsNullOrWhiteSpace(suppliedChannelAccessToken)
            ? _settingsStore.GetLineCredentials().ChannelAccessToken
            : suppliedChannelAccessToken.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("LINEのチャネルアクセストークンが登録されていません。");
        }
        ValidateChannelAccessToken(token);
        return token;
    }

    private static void ValidateChannelAccessToken(string channelAccessToken)
    {
        var value = channelAccessToken.Trim();
        if (value.Length < 20 || value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("LINEのチャネルアクセストークンを正しく入力してください。", nameof(channelAccessToken));
        }
    }

    private static void ValidateRecipientUserId(string recipientUserId)
    {
        var value = recipientUserId.Trim();
        if (value.Length < 20 ||
            value[0] != 'U' ||
            value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("LINEの受信先User ID（Uで始まるID）を正しく入力してください。", nameof(recipientUserId));
        }
    }

    private static HttpRequestException CreateLineApiException(
        HttpStatusCode statusCode,
        string? reasonPhrase,
        string responseBody)
    {
        var detail = string.IsNullOrWhiteSpace(responseBody)
            ? reasonPhrase ?? "応答内容なし"
            : TryReadLineError(responseBody);
        return new HttpRequestException(
            $"LINE通知に失敗しました（HTTP {(int)statusCode}）: {detail}",
            null,
            statusCode);
    }

    private static string TryReadLineError(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("message", out var messageProperty) &&
                !string.IsNullOrWhiteSpace(messageProperty.GetString()))
            {
                return messageProperty.GetString()!.Trim();
            }
        }
        catch (JsonException)
        {
            // Fall back to the raw response below.
        }
        return responseBody.Trim();
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
        Timeout = TimeSpan.FromSeconds(20)
    };

    private sealed record LineCredentials(string ChannelAccessToken, string RecipientUserId);
}
