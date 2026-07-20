using GalleryBrowser.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GalleryBrowser.Services;

internal sealed class GoogleCalendarSyncService
{
    public const string OAuthRedirectUri = "http://127.0.0.1:43818/google-calendar/";
    private const string CalendarScope = "https://www.googleapis.com/auth/calendar.events";
    private const string ManagedPropertyName = "galleryBrowserManaged";
    private const string ManagedPropertyValue = "subscription";
    private const string SourceIdPropertyName = "galleryBrowserSubscriptionId";
    private const string FingerprintPropertyName = "galleryBrowserFingerprint";
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GalleryDatabase _database;
    private readonly StorageSettingsStore _settingsStore;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public GoogleCalendarSyncService(GalleryDatabase database, StorageSettingsStore settingsStore)
    {
        _database = database;
        _settingsStore = settingsStore;
    }

    public GoogleCalendarSyncSettingsDto GetSettings() => _settingsStore.GetGoogleCalendarSettings();

    public void SaveSettings(bool autoSyncEnabled, string clientId, string? clientSecret, string calendarId) =>
        _settingsStore.SaveGoogleCalendarConfiguration(autoSyncEnabled, clientId, clientSecret, calendarId);

    public void Disconnect() => _settingsStore.DisconnectGoogleCalendar();

    public async Task ConnectAsync(
        bool autoSyncEnabled,
        string clientId,
        string? clientSecret,
        string calendarId,
        CancellationToken cancellationToken)
    {
        SaveSettings(autoSyncEnabled, clientId, clientSecret, calendarId);
        var settings = GetSettings();
        if (string.IsNullOrWhiteSpace(settings.ClientId))
        {
            throw new ArgumentException("Google Cloudで作成したデスクトップアプリのOAuth Client IDを入力してください。", nameof(clientId));
        }

        var state = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var codeVerifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(64));
        var codeChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var listener = new TcpListener(IPAddress.Loopback, new Uri(OAuthRedirectUri).Port);
        listener.Start();
        try
        {
            var authorizationUri = new UriBuilder("https://accounts.google.com/o/oauth2/v2/auth")
            {
                Query = BuildQueryString(new Dictionary<string, string>
                {
                    ["client_id"] = settings.ClientId,
                    ["redirect_uri"] = OAuthRedirectUri,
                    ["response_type"] = "code",
                    ["scope"] = CalendarScope,
                    ["access_type"] = "offline",
                    ["prompt"] = "consent",
                    ["include_granted_scopes"] = "true",
                    ["state"] = state,
                    ["code_challenge"] = codeChallenge,
                    ["code_challenge_method"] = "S256"
                })
            }.Uri;

            Process.Start(new ProcessStartInfo
            {
                FileName = authorizationUri.AbsoluteUri,
                UseShellExecute = true
            });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            string authorizationCode;
            try
            {
                authorizationCode = await WaitForAuthorizationCodeAsync(listener, state, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Google CalendarのOAuth連携が5分以内に完了しませんでした。もう一度実行してください。");
            }

            var token = await RequestTokenAsync(new Dictionary<string, string>
            {
                ["client_id"] = settings.ClientId,
                ["client_secret"] = _settingsStore.GetGoogleCalendarClientSecret(),
                ["code"] = authorizationCode,
                ["code_verifier"] = codeVerifier,
                ["redirect_uri"] = OAuthRedirectUri,
                ["grant_type"] = "authorization_code"
            }, cancellationToken);
            if (string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                throw new InvalidOperationException("Googleから自動同期用の更新トークンが返されませんでした。連携を解除して再度OAuth連携を行ってください。");
            }

            _settingsStore.SaveGoogleCalendarConnection(token.RefreshToken);
            await TestConnectionAsync(cancellationToken);
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        var settings = GetSettings();
        var accessToken = await GetAccessTokenAsync(settings, cancellationToken);
        var calendarId = Uri.EscapeDataString(settings.CalendarId);
        using var response = await SendAuthorizedAsync(
            accessToken,
            HttpMethod.Get,
            $"https://www.googleapis.com/calendar/v3/calendars/{calendarId}/events?maxResults=1&showDeleted=false",
            null,
            cancellationToken);
        await EnsureSuccessAsync(response, "Google Calendarへの接続確認", cancellationToken);
    }

    public async Task<GoogleCalendarSyncResult?> SyncIfEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = GetSettings();
        return settings.AutoSyncEnabled && settings.HasRefreshToken
            ? await SyncAsync(cancellationToken)
            : null;
    }

    public async Task<GoogleCalendarSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            var settings = GetSettings();
            var accessToken = await GetAccessTokenAsync(settings, cancellationToken);
            var existing = await ListManagedEventsAsync(accessToken, settings.CalendarId, cancellationToken);
            var desiredEvents = _database.ListCalendarSubscriptionEvents();
            var existingBySourceId = existing
                .Where(item => !string.IsNullOrWhiteSpace(item.SourceId))
                .GroupBy(item => item.SourceId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var duplicateEvents = existing
                .Where(item => !string.IsNullOrWhiteSpace(item.SourceId))
                .GroupBy(item => item.SourceId, StringComparer.Ordinal)
                .SelectMany(group => group.Skip(1))
                .ToList();

            var created = 0;
            var updated = 0;
            var deleted = 0;
            var unchanged = 0;
            var desiredIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var calendarEvent in desiredEvents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                desiredIds.Add(calendarEvent.Id);
                var fingerprint = CreateFingerprint(calendarEvent);
                if (existingBySourceId.TryGetValue(calendarEvent.Id, out var managedEvent))
                {
                    if (string.Equals(managedEvent.Fingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        unchanged++;
                        continue;
                    }

                    await WriteEventAsync(
                        accessToken,
                        settings.CalendarId,
                        managedEvent.EventId,
                        calendarEvent,
                        fingerprint,
                        HttpMethod.Patch,
                        cancellationToken);
                    updated++;
                }
                else
                {
                    await WriteEventAsync(
                        accessToken,
                        settings.CalendarId,
                        null,
                        calendarEvent,
                        fingerprint,
                        HttpMethod.Post,
                        cancellationToken);
                    created++;
                }
            }

            foreach (var managedEvent in existing.Where(item => !desiredIds.Contains(item.SourceId)).Concat(duplicateEvents).DistinctBy(item => item.EventId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DeleteEventAsync(accessToken, settings.CalendarId, managedEvent.EventId, cancellationToken);
                deleted++;
            }

            var result = new GoogleCalendarSyncResult(created, updated, deleted, unchanged, DateTimeOffset.Now);
            _settingsStore.SaveGoogleCalendarSyncResult(result);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _settingsStore.SaveGoogleCalendarSyncError(exception.Message);
            throw;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<string> GetAccessTokenAsync(
        GoogleCalendarSyncSettingsDto settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ClientId))
        {
            throw new InvalidOperationException("Google CalendarのOAuth Client IDが設定されていません。");
        }

        var token = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId,
            ["client_secret"] = _settingsStore.GetGoogleCalendarClientSecret(),
            ["refresh_token"] = _settingsStore.GetGoogleCalendarRefreshToken(),
            ["grant_type"] = "refresh_token"
        }, cancellationToken);
        return token.AccessToken;
    }

    private static async Task<OAuthTokenResult> RequestTokenAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var content = new FormUrlEncodedContent(parameters
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value)));
        using var response = await HttpClient.PostAsync("https://oauth2.googleapis.com/token", content, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Google OAuthトークンを取得できませんでした（HTTP {(int)response.StatusCode}）: {ReadGoogleError(json)}");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var accessTokenProperty)
            ? accessTokenProperty.GetString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidDataException("Google OAuth応答にアクセストークンがありません。");
        }
        var refreshToken = root.TryGetProperty("refresh_token", out var refreshTokenProperty)
            ? refreshTokenProperty.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        return new OAuthTokenResult(accessToken, refreshToken);
    }

    private static async Task<IReadOnlyList<ManagedGoogleEvent>> ListManagedEventsAsync(
        string accessToken,
        string calendarId,
        CancellationToken cancellationToken)
    {
        var events = new List<ManagedGoogleEvent>();
        string? pageToken = null;
        do
        {
            var query = new Dictionary<string, string>
            {
                ["maxResults"] = "2500",
                ["showDeleted"] = "false",
                ["privateExtendedProperty"] = $"{ManagedPropertyName}={ManagedPropertyValue}"
            };
            if (!string.IsNullOrWhiteSpace(pageToken))
            {
                query["pageToken"] = pageToken;
            }
            var uri = $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(calendarId)}/events?{BuildQueryString(query)}";
            using var response = await SendAuthorizedAsync(accessToken, HttpMethod.Get, uri, null, cancellationToken);
            var json = await ReadSuccessJsonAsync(response, "Google Calendarの同期対象取得", cancellationToken);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var eventId = ReadJsonString(item, "id");
                    if (string.IsNullOrWhiteSpace(eventId) ||
                        !item.TryGetProperty("extendedProperties", out var extendedProperties) ||
                        !extendedProperties.TryGetProperty("private", out var privateProperties))
                    {
                        continue;
                    }
                    var sourceId = ReadJsonString(privateProperties, SourceIdPropertyName);
                    var fingerprint = ReadJsonString(privateProperties, FingerprintPropertyName);
                    events.Add(new ManagedGoogleEvent(eventId, sourceId, fingerprint));
                }
            }
            pageToken = ReadJsonString(document.RootElement, "nextPageToken");
        }
        while (!string.IsNullOrWhiteSpace(pageToken));
        return events;
    }

    private static async Task WriteEventAsync(
        string accessToken,
        string calendarId,
        string? eventId,
        CalendarSubscriptionEventDto calendarEvent,
        string fingerprint,
        HttpMethod method,
        CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParse(calendarEvent.RenewalOn, CultureInfo.InvariantCulture, DateTimeStyles.None, out var renewalDate))
        {
            throw new InvalidDataException($"サブスク更新日を解釈できません: {calendarEvent.RenewalOn}");
        }
        var platformAndPlan = string.Join(" / ", new[] { calendarEvent.Platform, calendarEvent.Plan }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var summary = string.IsNullOrWhiteSpace(platformAndPlan)
            ? $"サブスク更新: {calendarEvent.DisplayName}"
            : $"サブスク更新: {calendarEvent.DisplayName} / {platformAndPlan}";
        if (calendarEvent.EndingPlanned)
        {
            summary = $"[終了予定] {summary}";
        }
        var amount = calendarEvent.Amount > 0
            ? $"{calendarEvent.Amount:N0} {calendarEvent.Currency}".Trim()
            : "-";
        var payload = new
        {
            summary,
            description = string.Join("\n", new[]
            {
                $"Creator: {calendarEvent.Creator}",
                $"Platform: {calendarEvent.Platform}",
                $"Plan: {calendarEvent.Plan}",
                $"Amount: {amount}",
                $"Ending planned: {(calendarEvent.EndingPlanned ? "Yes" : "No")}",
                "Managed by GalleryBrowser"
            }),
            start = new { date = renewalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            end = new { date = renewalDate.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            transparency = "transparent",
            visibility = "private",
            reminders = calendarEvent.Reminder
                ? new { useDefault = true }
                : new { useDefault = false },
            extendedProperties = new
            {
                @private = new Dictionary<string, string>
                {
                    [ManagedPropertyName] = ManagedPropertyValue,
                    [SourceIdPropertyName] = calendarEvent.Id,
                    [FingerprintPropertyName] = fingerprint
                }
            }
        };
        var calendarPath = Uri.EscapeDataString(calendarId);
        var uri = eventId is null
            ? $"https://www.googleapis.com/calendar/v3/calendars/{calendarPath}/events"
            : $"https://www.googleapis.com/calendar/v3/calendars/{calendarPath}/events/{Uri.EscapeDataString(eventId)}";
        using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await SendAuthorizedAsync(accessToken, method, uri, content, cancellationToken);
        await EnsureSuccessAsync(response, eventId is null ? "Google Calendar予定の作成" : "Google Calendar予定の更新", cancellationToken);
    }

    private static async Task DeleteEventAsync(
        string accessToken,
        string calendarId,
        string eventId,
        CancellationToken cancellationToken)
    {
        var uri = $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}";
        using var response = await SendAuthorizedAsync(accessToken, HttpMethod.Delete, uri, null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Gone || response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }
        await EnsureSuccessAsync(response, "Google Calendar予定の削除", cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendAuthorizedAsync(
        string accessToken,
        HttpMethod method,
        string uri,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await HttpClient.SendAsync(request, cancellationToken);
    }

    private static async Task<string> ReadSuccessJsonAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{operation}に失敗しました（HTTP {(int)response.StatusCode}）: {ReadGoogleError(json)}");
        }
        return json;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"{operation}に失敗しました（HTTP {(int)response.StatusCode}）: {ReadGoogleError(json)}");
    }

    private static string ReadGoogleError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? "Unknown error";
                }
                if (error.TryGetProperty("message", out var message))
                {
                    return message.GetString() ?? "Unknown error";
                }
                if (error.TryGetProperty("error_description", out var description))
                {
                    return description.GetString() ?? "Unknown error";
                }
            }
        }
        catch (JsonException)
        {
            // Use the raw response below.
        }
        return string.IsNullOrWhiteSpace(json) ? "Unknown error" : json.Trim();
    }

    private static string CreateFingerprint(CalendarSubscriptionEventDto calendarEvent)
    {
        var canonical = string.Join('\n', new[]
        {
            calendarEvent.Id,
            calendarEvent.Creator,
            calendarEvent.DisplayName,
            calendarEvent.Platform,
            calendarEvent.Plan,
            calendarEvent.Currency,
            calendarEvent.Amount.ToString("R", CultureInfo.InvariantCulture),
            calendarEvent.RenewalOn,
            calendarEvent.EndingPlanned ? "1" : "0",
            calendarEvent.Reminder ? "1" : "0"
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static async Task<string> WaitForAuthorizationCodeAsync(
        TcpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                continue;
            }
            string? header;
            do
            {
                header = await reader.ReadLineAsync(cancellationToken);
            }
            while (!string.IsNullOrEmpty(header));

            var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteHttpResponseAsync(stream, HttpStatusCode.MethodNotAllowed, "OAuth callback requires GET", cancellationToken);
                continue;
            }
            if (!Uri.TryCreate(parts[1], UriKind.Absolute, out var requestUri))
            {
                requestUri = new Uri($"http://127.0.0.1:{new Uri(OAuthRedirectUri).Port}{parts[1]}");
            }
            if (!string.Equals(requestUri.AbsolutePath, new Uri(OAuthRedirectUri).AbsolutePath, StringComparison.Ordinal))
            {
                await WriteHttpResponseAsync(stream, HttpStatusCode.NotFound, "Not found", cancellationToken);
                continue;
            }
            var query = ParseQueryString(requestUri.Query);
            if (!query.TryGetValue("state", out var returnedState) ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(returnedState), Encoding.UTF8.GetBytes(expectedState)))
            {
                await WriteHttpResponseAsync(stream, HttpStatusCode.BadRequest, "OAuth state mismatch", cancellationToken);
                throw new InvalidOperationException("Google Calendar OAuthのstateが一致しませんでした。");
            }
            if (query.TryGetValue("error", out var error))
            {
                await WriteHttpResponseAsync(stream, HttpStatusCode.BadRequest, "Google Calendar authorization was cancelled", cancellationToken);
                throw new InvalidOperationException($"Google Calendar OAuth連携が拒否されました: {error}");
            }
            if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            {
                await WriteHttpResponseAsync(stream, HttpStatusCode.BadRequest, "Missing authorization code", cancellationToken);
                throw new InvalidOperationException("Google Calendar OAuthの認証コードが返されませんでした。");
            }
            await WriteHttpResponseAsync(stream, HttpStatusCode.OK, "Google Calendarとの連携を受け付けました。このタブを閉じてGalleryBrowserへ戻ってください。", cancellationToken);
            return code;
        }
    }

    private static async Task WriteHttpResponseAsync(
        NetworkStream stream,
        HttpStatusCode statusCode,
        string message,
        CancellationToken cancellationToken)
    {
        var encodedMessage = WebUtility.HtmlEncode(message);
        var body = $"<!doctype html><html lang=\"ja\"><head><meta charset=\"utf-8\"><title>GalleryBrowser Google Calendar OAuth</title></head><body style=\"font-family:Segoe UI,sans-serif;background:#171a20;color:#f4f7ff;padding:40px\"><h1>GalleryBrowser</h1><p>{encodedMessage}</p></body></html>";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int)statusCode} {statusCode}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            var key = separator >= 0 ? segment[..separator] : segment;
            var value = separator >= 0 ? segment[(separator + 1)..] : string.Empty;
            result[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return result;
    }

    private static string BuildQueryString(IEnumerable<KeyValuePair<string, string>> values) =>
        string.Join('&', values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static string ReadJsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GalleryBrowser/1.0");
        return client;
    }

    private sealed record OAuthTokenResult(string AccessToken, string RefreshToken);
    private sealed record ManagedGoogleEvent(string EventId, string SourceId, string Fingerprint);
}
