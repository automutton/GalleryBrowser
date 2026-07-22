using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GalleryBrowser.Models;

namespace GalleryBrowser.Services;

public sealed class StandardNameSearchService
{
    private const string GeminiModel = "gemini-3.5-flash";
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    public async Task<IReadOnlyList<StandardNameSearchResultDto>> SearchAsync(
        SearchEngineSettingsDto settings,
        string query,
        string? contextTitle = null,
        CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("検索する名称を入力してください。", nameof(query));
        }

        return settings.Provider switch
        {
            "brave" => await SearchBraveAsync(settings.BraveApiKey, BuildSearchQuery(query, contextTitle), cancellationToken),
            "gemini" => await SearchGeminiAsync(settings.GeminiApiKey, query, contextTitle, cancellationToken),
            _ => []
        };
    }

    private static async Task<IReadOnlyList<StandardNameSearchResultDto>> SearchBraveAsync(
        string apiKey,
        string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Brave Search APIキーを Settings > 検索エンジン に設定してください。");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.search.brave.com/res/v1/web/search?q=" + Uri.EscapeDataString(query) + "&count=10&search_lang=ja");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Subscription-Token", apiKey.Trim());
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Brave Search API の検索に失敗しました ({(int)response.StatusCode}): {ExtractApiError(body)}");
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("web", out var web) ||
            !web.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return results.EnumerateArray()
            .Select(result => new StandardNameSearchResultDto(
                result.TryGetProperty("title", out var title) ? title.GetString()?.Trim() ?? string.Empty : string.Empty,
                "Brave Search",
                result.TryGetProperty("url", out var url) ? url.GetString() : null,
                result.TryGetProperty("description", out var description) ? description.GetString() : null))
            .Where(result => !string.IsNullOrWhiteSpace(result.Name))
            .DistinctBy(result => result.Name, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();
    }

    private static async Task<IReadOnlyList<StandardNameSearchResultDto>> SearchGeminiAsync(
        string apiKey,
        string query,
        string? contextTitle,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Gemini APIキーを Settings > 検索エンジン に設定してください。");
        }

        var titleContext = string.IsNullOrWhiteSpace(contextTitle)
            ? string.Empty
            : $"この名称は作品・シリーズ「{contextTitle.Trim()}」に属するCharacterです。Titleを識別条件として優先し、同名の別作品のCharacterを除外してください。\n";
        var prompt =
            "あなたは日本語のメディアライブラリ用メタデータを正規化するアシスタントです。\n" +
            "次の名称について、表記ゆれを除いた標準的な日本語名称候補を最大8件提案してください。\n" +
            titleContext +
            (string.IsNullOrWhiteSpace(contextTitle)
                ? "作品名・シリーズ名・人物名・キャラクター名のどれかは断定せず、候補名だけを返してください。\n"
                : "Characterの標準名候補だけを返してください。\n") +
            "回答は必ず JSON オブジェクト {\"candidates\":[\"候補1\",\"候補2\"]} のみとし、説明文やMarkdownは付けないでください。\n" +
            "入力: " + query;
        var payload = JsonSerializer.Serialize(new
        {
            contents = new[]
            {
                new
                {
                    parts = new[] { new { text = prompt } }
                }
            },
            generationConfig = new
            {
                responseFormat = new
                {
                    text = new
                    {
                        mimeType = "APPLICATION_JSON",
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                candidates = new
                                {
                                    type = "array",
                                    description = "標準的な日本語名称の候補。最大8件。",
                                    items = new { type = "string" }
                                }
                            },
                            required = new[] { "candidates" },
                            additionalProperties = false
                        }
                    }
                },
                thinkingConfig = new { thinkingLevel = "low" },
                maxOutputTokens = 4096
            }
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{GeminiModel}:generateContent?key={Uri.EscapeDataString(apiKey.Trim())}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Gemini API の候補生成に失敗しました ({(int)response.StatusCode}): {ExtractApiError(body)}");
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("candidates", out var responseCandidates) ||
            responseCandidates.ValueKind != JsonValueKind.Array ||
            responseCandidates.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Gemini API から候補データが返されませんでした。");
        }

        var responseCandidate = responseCandidates[0];
        if (responseCandidate.TryGetProperty("finishReason", out var finishReason) &&
            string.Equals(finishReason.GetString(), "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Gemini API の応答が出力上限で中断されました。もう一度検索してください。");
        }

        if (!responseCandidate.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array ||
            parts.GetArrayLength() == 0 ||
            !parts[0].TryGetProperty("text", out var responseText))
        {
            throw new InvalidOperationException("Gemini API の応答形式を読み取れませんでした。");
        }

        var text = responseText.GetString() ?? string.Empty;
        var candidates = ParseGeminiCandidates(text);
        return candidates
            .Select(candidate => new StandardNameSearchResultDto(candidate, "Gemini", null, "AI候補"))
            .ToArray();
    }

    private static string BuildSearchQuery(string query, string? contextTitle) =>
        string.IsNullOrWhiteSpace(contextTitle)
            ? query
            : $"{contextTitle.Trim()} {query} キャラクター 標準名";

    private static IReadOnlyList<string> ParseGeminiCandidates(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = text.Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("```", string.Empty, StringComparison.Ordinal)
                .Trim();
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
            {
                return candidates.EnumerateArray()
                    .Where(candidate => candidate.ValueKind == JsonValueKind.String)
                    .Select(candidate => candidate.GetString()?.Trim() ?? string.Empty)
                    .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToArray();
            }
        }
        catch (JsonException)
        {
            if (text.StartsWith('{') || text.StartsWith('['))
            {
                throw new InvalidOperationException("Gemini API のJSON応答が途中で切れているため、候補を読み取れませんでした。もう一度検索してください。");
            }

            // Return a useful fallback only for a complete plain-text response.
        }

        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.TrimStart('-', '*', ' ', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', '、'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    private static string ExtractApiError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? "不明なエラー";
            }
            if (document.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out message))
            {
                return message.GetString() ?? "不明なエラー";
            }
        }
        catch (JsonException)
        {
        }
        return string.IsNullOrWhiteSpace(body) ? "不明なエラー" : body[..Math.Min(body.Length, 240)];
    }
}
