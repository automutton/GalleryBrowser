using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace GalleryBrowser.Services;

internal sealed class YahooFuriganaService(GalleryDatabase database)
{
    private sealed class YahooFuriganaApiException(string code, string message)
        : InvalidOperationException($"Yahoo! JAPAN ルビ振りAPIでエラーが発生しました: {string.Join(" ", new[] { code, message }.Where(value => !string.IsNullOrWhiteSpace(value)))}")
    {
        public string Code { get; } = code;
    }

    private const string Endpoint = "https://jlp.yahooapis.jp/jsonrpc";
    private const int MaximumQueryBytes = 2_800;
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(220);
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public async Task<IReadOnlyDictionary<string, string>> RomanizeAsync(
        string clientId,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken = default)
    {
        clientId = clientId.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException(
                "Yahoo! JAPAN Client IDをSettings > Advanced > 検索エンジンに設定してください。");
        }

        var sources = values
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Where(value => Encoding.UTF8.GetByteCount(value) <= MaximumQueryBytes)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (sources.Length == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in database.GetTextRomanizationCache(sources))
        {
            result[entry.Key] = entry.Value;
        }
        var missing = sources.Where(source => !result.ContainsKey(source)).ToArray();
        if (missing.Length == 0)
        {
            return result;
        }

        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var batch in CreateBatches(missing))
            {
                var romanized = await RequestBatchWithFallbackAsync(clientId, batch, cancellationToken);
                foreach (var entry in romanized)
                {
                    result[entry.Key] = entry.Value;
                }
                database.SaveTextRomanizationCache(romanized);
            }
        }
        finally
        {
            _requestGate.Release();
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyList<string>> CreateBatches(IReadOnlyList<string> values)
    {
        var batches = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        var currentBytes = 0;
        foreach (var value in values)
        {
            var valueBytes = Encoding.UTF8.GetByteCount(value);
            var separatorBytes = current.Count == 0 ? 0 : 58;
            if (current.Count > 0 && currentBytes + separatorBytes + valueBytes > MaximumQueryBytes)
            {
                batches.Add(current.ToArray());
                current.Clear();
                currentBytes = 0;
                separatorBytes = 0;
            }

            current.Add(value);
            currentBytes += separatorBytes + valueBytes;
        }

        if (current.Count > 0)
        {
            batches.Add(current.ToArray());
        }
        return batches;
    }

    private async Task<IReadOnlyDictionary<string, string>> RequestBatchWithFallbackAsync(
        string clientId,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> result;
        try
        {
            result = await RequestBatchAsync(clientId, values, cancellationToken);
        }
        catch (YahooFuriganaApiException exception) when (exception.Code == "-32602")
        {
            if (values.Count == 1)
            {
                // A single unsupported value must not prevent every other searchable label
                // from being romanized, nor should it be retried on every filter evaluation.
                return new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [values[0]] = string.Empty
                };
            }

            var midpoint = values.Count / 2;
            var recovered = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in await RequestBatchWithFallbackAsync(
                         clientId,
                         values.Take(midpoint).ToArray(),
                         cancellationToken))
            {
                recovered[entry.Key] = entry.Value;
            }
            foreach (var entry in await RequestBatchWithFallbackAsync(
                         clientId,
                         values.Skip(midpoint).ToArray(),
                         cancellationToken))
            {
                recovered[entry.Key] = entry.Value;
            }
            return recovered;
        }

        if (result.Count == values.Count || values.Count == 1)
        {
            if (values.Count == 1 && !result.ContainsKey(values[0]))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [values[0]] = string.Empty
                };
            }
            return result;
        }

        var fallback = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var single = await RequestBatchAsync(clientId, [value], cancellationToken);
            foreach (var entry in single)
            {
                fallback[entry.Key] = entry.Value;
            }
        }
        return fallback;
    }

    private async Task<IReadOnlyDictionary<string, string>> RequestBatchAsync(
        string clientId,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        var marker = $"GBRSEP{Guid.NewGuid():N}GBR";
        var separator = $" {marker} ";
        var query = string.Join(separator, values);
        await WaitForRateLimitAsync(cancellationToken);

        var payload = JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString("N"),
            jsonrpc = "2.0",
            method = "jlp.furiganaservice.furigana",
            @params = new { q = query }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("User-Agent", $"Yahoo AppID: {clientId}");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Yahoo! JAPAN ルビ振りAPIに接続できませんでした ({(int)response.StatusCode}): {ReadErrorMessage(body)}");
        }

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            throw new YahooFuriganaApiException(
                error.TryGetProperty("code", out var codeProperty)
                    ? codeProperty.ToString()
                    : string.Empty,
                error.TryGetProperty("message", out var messageProperty)
                    ? messageProperty.GetString() ?? string.Empty
                    : string.Empty);
        }
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("word", out var words) ||
            words.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Yahoo! JAPAN ルビ振りAPIの応答を読み取れませんでした。");
        }

        var phonetic = new StringBuilder(query.Length * 2);
        var officialRoman = new StringBuilder(query.Length * 2);
        foreach (var word in words.EnumerateArray())
        {
            var surface = ReadString(word, "surface");
            phonetic.Append(ReadString(word, "furigana", surface));
            officialRoman.Append(ReadString(word, "roman", surface));
        }

        var phoneticParts = phonetic.ToString().Split(marker, StringSplitOptions.None);
        var officialParts = officialRoman.ToString().Split(marker, StringSplitOptions.None);
        if (phoneticParts.Length != values.Count || officialParts.Length != values.Count)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            var hepburn = FileNameRomanizer.Romanize(phoneticParts[index].Trim());
            var combined = NormalizeRomanizedText($"{officialParts[index].Trim()} {hepburn}");
            if (!string.IsNullOrWhiteSpace(combined))
            {
                mapped[values[index]] = combined;
            }
        }
        return mapped;
    }

    private async Task WaitForRateLimitAsync(CancellationToken cancellationToken)
    {
        var remaining = MinimumRequestInterval - (DateTimeOffset.UtcNow - _lastRequestAt);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
        _lastRequestAt = DateTimeOffset.UtcNow;
    }

    private static string NormalizeRomanizedText(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var result = new StringBuilder(normalized.Length);
        var previousWasSpace = false;
        foreach (var character in normalized)
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                result.Append(character);
                previousWasSpace = false;
            }
            else if (!previousWasSpace && result.Length > 0)
            {
                result.Append(' ');
                previousWasSpace = true;
            }
        }
        return result.ToString().Trim();
    }

    private static string ReadString(JsonElement element, string propertyName, string fallback = "") =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;

    private static string ReadErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("message", out var message)
                ? message.GetString() ?? body
                : body;
        }
        catch (JsonException)
        {
            return body;
        }
    }

}
