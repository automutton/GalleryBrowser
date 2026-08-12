using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GalleryBrowser.Models;

namespace GalleryBrowser.Services;

internal sealed class AiConciergeService : IDisposable
{
    private sealed record TurnCompletion(string Status, string Text, string ErrorMessage);
    private sealed record CodexLaunch(string FileName, IReadOnlyList<string> Arguments, string DisplayName);
    private sealed class VisualInferenceQueueArguments
    {
        public string Section { get; set; } = string.Empty;
        public List<string> Paths { get; set; } = [];
    }

    private sealed class PersistedSession
    {
        public int Version { get; set; } = 5;
        public string ThreadId { get; set; } = string.Empty;
        public List<AiConciergeMessageDto> Messages { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions ProtocolJsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly object[] DynamicTools =
    [
        new
        {
            type = "function",
            name = "gallerybrowser_navigate",
            description =
                "GalleryBrowser内の画面へ移動します。Creator Trackingを開く場合はcreatorも指定できます。" +
                "ユーザーが移動を依頼したときだけ使用してください。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    destination = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "gallery", "explorer", "creators", "creatorTracking",
                            "bookmarks", "filters", "tags", "userMetrics", "board",
                            "calendar", "settings", "userGuide"
                        },
                        description = "移動先"
                    },
                    section = new
                    {
                        type = "string",
                        description = "Gallery、Creators、User Metricsなどの区分。省略時は現在値を維持します。"
                    },
                    creator = new
                    {
                        type = "string",
                        description = "Creator Trackingで開くCreator名"
                    }
                },
                required = new[] { "destination" },
                additionalProperties = false
            }
        },
        new
        {
            type = "function",
            name = "gallerybrowser_set_gallery_filters",
            description =
                "Galleryの区分、検索語、Rating、Creator、Title、Character、Tagフィルタをまとめて設定します。" +
                "指定を省略した項目は現在値を維持し、空配列はそのフィルタを解除します。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    section = new { type = "string" },
                    search = new { type = "string" },
                    ratings = new { type = "array", items = new { type = "integer", minimum = 0, maximum = 6 } },
                    creators = new { type = "array", items = new { type = "string" } },
                    titles = new { type = "array", items = new { type = "string" } },
                    characters = new { type = "array", items = new { type = "string" } },
                    tags = new { type = "array", items = new { type = "string" } }
                },
                additionalProperties = false
            }
        },
        new
        {
            type = "function",
            name = "gallerybrowser_search",
            description =
                "GalleryBrowser内の検索欄へ検索語を設定します。scope=autoなら現在の画面を対象にします。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string" },
                    scope = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "auto", "gallery", "explorer", "creators",
                            "creatorTrackingIndex", "filters", "tags"
                        }
                    }
                },
                required = new[] { "query" },
                additionalProperties = false
            }
        },
        new
        {
            type = "function",
            name = "gallerybrowser_refresh",
            description = "現在の画面、または指定したGalleryBrowser画面のデータを再読み込みします。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    target = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "auto", "gallery", "explorer", "creators",
                            "creatorTracking", "userMetrics", "calendar"
                        }
                    }
                },
                additionalProperties = false
            }
        },
        new
        {
            type = "function",
            name = "gallerybrowser_manage_tab",
            description =
                "ExplorerまたはCreator Trackingのタブを開く、選択する、閉じる操作を行います。" +
                "Explorerのopenにはフォルダパス、Creator TrackingのopenにはCreator名をtargetへ指定します。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    area = new
                    {
                        type = "string",
                        @enum = new[] { "explorer", "creatorTracking" }
                    },
                    operation = new
                    {
                        type = "string",
                        @enum = new[] { "open", "activate", "close" }
                    },
                    target = new { type = "string" },
                    section = new
                    {
                        type = "string",
                        description = "Creator Trackingで使う区分"
                    }
                },
                required = new[] { "area", "operation", "target" },
                additionalProperties = false
            }
        },
        new
        {
            type = "function",
            name = "gallerybrowser_open_attribute_editor",
            description =
                "Galleryで選択中の作品に対するTitle、Character、Tagの登録・解除画面を開きます。" +
                "属性の確定操作は開いた画面でユーザーが行います。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    attribute = new
                    {
                        type = "string",
                        @enum = new[] { "title", "character", "tag" }
                    }
                },
                required = new[] { "attribute" },
                additionalProperties = false
            }
        },
        new
        {
            type = "function",
            name = "gallerybrowser_get_attribute_inference_batch",
            description =
                "Title未付与の作品を、既存Title候補、過去のAI付与後にユーザーが手動修正した学習例、" +
                "必要に応じて作品サムネイルとともに最大20件取得します。cursorは直前の応答のnextCursorを使います。" +
                "大量処理では取得、Character候補取得、付与を反復してください。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    section = new
                    {
                        type = "string",
                        description = "対象のGallery区分"
                    },
                    creator = new
                    {
                        type = "string",
                        description = "特定Creatorだけに絞る場合のCreator名。全Creatorなら省略"
                    },
                    cursor = new
                    {
                        type = "string",
                        description = "前回応答のnextCursor。最初は省略"
                    },
                    batchSize = new
                    {
                        type = "integer",
                        minimum = 1,
                        maximum = 20,
                        description = "一度に取得する件数。既定8件"
                    },
                    includeImages = new
                    {
                        type = "boolean",
                        description = "推論用サムネイルを含めるか。既定true"
                    },
                    conditions = new
                    {
                        type = "object",
                        description =
                            "自然言語の対象条件を構造化したもの。指定のない項目は省略",
                        properties = new
                        {
                            creators = new
                            {
                                type = "array",
                                maxItems = 100,
                                items = new { type = "string" }
                            },
                            paths = new
                            {
                                type = "array",
                                maxItems = 20,
                                description =
                                    "自動仕分け後の画像推論で対象作品を完全一致指定する内部用パス",
                                items = new { type = "string" }
                            },
                            tags = new
                            {
                                type = "array",
                                maxItems = 100,
                                items = new { type = "string" }
                            },
                            tagMatch = new
                            {
                                type = "string",
                                @enum = new[] { "any", "all" },
                                description = "tagsのいずれか、またはすべてに一致"
                            },
                            pathContains = new { type = "string" },
                            fileNameContains = new { type = "string" },
                            minimumRating = new { type = "integer", minimum = 0 },
                            maximumRating = new { type = "integer", minimum = 0 },
                            minimumImageCount = new { type = "integer", minimum = 0 },
                            maximumImageCount = new { type = "integer", minimum = 0 }
                        },
                        additionalProperties = false
                    }
                },
                required = new[] { "section" },
                additionalProperties = false
            }
        },
        new
        {
            type = "function",
            name = "gallerybrowser_defer_visual_attribute_inference",
            description =
                "属性推論の自動仕分け時に、文字情報だけではTitleを十分な確度で判断できない作品を" +
                "画像推論ステージへ保留します。保留した作品は現在のターン完了後にSol・高推論で自動処理されます。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    section = new
                    {
                        type = "string",
                        description = "対象のGallery区分"
                    },
                    paths = new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 20,
                        items = new { type = "string" }
                    }
                },
                required = new[] { "section", "paths" },
                additionalProperties = false
            }
        },
        new
        {
            type = "function",
            name = "gallerybrowser_get_character_candidates_for_inference",
            description =
                "推論したTitleごとに、そのTitleへ登録済みで付与可能な既存Character候補を取得します。" +
                "Characterを推論する前に使用してください。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    section = new { type = "string" },
                    works = new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 20,
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new { type = "string" },
                                titleId = new { type = "integer", minimum = 1 }
                            },
                            required = new[] { "path", "titleId" },
                            additionalProperties = false
                        }
                    }
                },
                required = new[] { "section", "works" },
                additionalProperties = false
            }
        },
        new
        {
            type = "function",
            name = "gallerybrowser_queue_attribute_inference_review",
            description =
                "画像と文字情報を確認しても既存Titleを十分な確度で判断できなかった作品を、" +
                "ユーザーが既存属性の付与または新規属性追加を行う要確認リストへ登録します。" +
                "Titleを付与できた作品は含めないでください。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    section = new
                    {
                        type = "string",
                        description = "対象のGallery区分"
                    },
                    works = new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 50,
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new { type = "string" },
                                reason = new
                                {
                                    type = "string",
                                    description = "判断できなかった理由を短く記載"
                                }
                            },
                            required = new[] { "path", "reason" },
                            additionalProperties = false
                        }
                    }
                },
                required = new[] { "section", "works" },
                additionalProperties = false
            }
        },
        new
        {
            type = "function",
            name = "gallerybrowser_apply_inferred_attributes",
            description =
                "Title未付与作品へ、推論した1件以上の既存Titleと0件以上の既存Characterを一括付与します。" +
                "新しい属性の作成、既存属性の削除・置換は行いません。" +
                "根拠が弱い作品はassignmentsへ含めず、Characterだけ不確かな場合はcharacterIdsを空にしてください。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    assignments = new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 50,
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new { type = "string" },
                                titleIds = new
                                {
                                    type = "array",
                                    minItems = 1,
                                    maxItems = 8,
                                    items = new { type = "integer", minimum = 1 }
                                },
                                characterIds = new
                                {
                                    type = "array",
                                    maxItems = 32,
                                    items = new { type = "integer", minimum = 1 }
                                },
                                confidence = new
                                {
                                    type = "number",
                                    minimum = 0,
                                    maximum = 1
                                },
                                reason = new { type = "string" }
                            },
                            required = new[]
                            {
                                "path", "titleIds", "characterIds", "confidence", "reason"
                            },
                            additionalProperties = false
                        }
                    }
                },
                required = new[] { "assignments" },
                additionalProperties = false
            }
        },
        new
        {
            type = "function",
            name = "gallerybrowser_update_creator_tracking",
            description =
                "現在開いているCreator Trackingの基本情報やメモを変更し、保存要求を送信します。" +
                "指定を省略した項目は変更しません。作者データやファイルの削除には使用できません。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    displayName = new { type = "string" },
                    alternateName = new { type = "string" },
                    followUpStatus = new { type = "string" },
                    lastCheckedOn = new
                    {
                        type = "string",
                        description = "YYYY-MM-DD形式"
                    },
                    lastActivityOn = new
                    {
                        type = "string",
                        description = "YYYY-MM-DD形式"
                    },
                    activitySummary = new { type = "string" },
                    evaluationMemo = new { type = "string" },
                    supportMemo = new { type = "string" }
                },
                additionalProperties = false
            }
        },
        new
        {
            type = "function",
            name = "gallerybrowser_assign_missing_folder_thumbnails",
            description =
                "指定区分の各【作者名】フォルダ配下を走査し、サムネイル未設定のサブフォルダへ" +
                "代表サムネイルを自動設定します。直下の書庫・動画では画像枚数が最多の作品を選び、" +
                "直下に作品がなければ子フォルダの代表を上へ伝播します。" +
                "既存のフォルダサムネイルは上書きせず最優先し、複数ある場合は配下作品数、" +
                "同数ならフォルダ作成日時の古い順で選びます。" +
                "生成AI作品と同人アニメの一括整備を依頼された場合に使用してください。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    sections = new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 2,
                        uniqueItems = true,
                        items = new
                        {
                            type = "string",
                            @enum = new[] { "ai", "doujin_anime" }
                        },
                        description =
                            "ai=生成AI作品、doujin_anime=同人アニメ"
                    }
                },
                required = new[] { "sections" },
                additionalProperties = false
            }
        }
    ];

    private const int MaximumMessages = 100;
    private readonly object _sessionLock = new();
    private readonly object _diagnosticLock = new();
    private readonly object _visualInferenceQueueLock = new();
    private readonly string _sessionPath;
    private readonly string _diagnosticPath;
    private readonly string _workspaceDirectory;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private PersistedSession _session;
    private Process? _process;
    private StreamWriter? _standardInput;
    private long _nextRequestId;
    private string _activeTurnId = string.Empty;
    private TaskCompletionSource<TurnCompletion>? _activeTurnCompletion;
    private StringBuilder? _activeTurnDelta;
    private string _activeCompletedAgentText = string.Empty;
    private IProgress<AiConciergeProgress>? _activeProgress;
    private TaskCompletionSource<string>? _threadStartedCompletion;
    private TaskCompletionSource<string>? _turnStartedCompletion;
    private string _lastStandardError = string.Empty;
    private string _runtimeThreadId = string.Empty;
    private readonly List<AiConciergeVisualInferenceBatch> _visualInferenceQueue = [];
    private bool _visualInferenceQueueEnabled;
    private bool _disposed;

    public Func<AiConciergeToolRequest, CancellationToken, Task<AiConciergeToolResult>>?
        ToolHandler { get; set; }

    public AiConciergeService(string conciergeDirectory)
    {
        conciergeDirectory = Path.GetFullPath(conciergeDirectory);
        Directory.CreateDirectory(conciergeDirectory);
        DataDirectory = conciergeDirectory;
        _sessionPath = Path.Combine(conciergeDirectory, "session.json");
        _diagnosticPath = Path.Combine(conciergeDirectory, "diagnostic.log");
        _workspaceDirectory = Path.Combine(conciergeDirectory, "workspace");
        Directory.CreateDirectory(_workspaceDirectory);
        ResetOversizedDiagnosticLog();
        WriteWorkspaceInstructions();
        WriteWorkspaceSkill();
        _session = LoadSession();
        if (_session.Version < 5 || !string.IsNullOrWhiteSpace(_session.ThreadId))
        {
            // GalleryBrowser owns the visible conversation in session.json. Codex
            // threads are ephemeral so that changing this directory moves all
            // concierge conversation data without leaving rollout logs elsewhere.
            _session.Version = 5;
            _session.ThreadId = string.Empty;
            SaveSessionLocked();
        }
    }

    public string DataDirectory { get; }

    public bool IsBusy => _turnGate.CurrentCount == 0;

    public void BeginAdaptiveAttributeInference()
    {
        lock (_visualInferenceQueueLock)
        {
            _visualInferenceQueue.Clear();
            _visualInferenceQueueEnabled = true;
        }
    }

    public void CancelAdaptiveAttributeInference()
    {
        lock (_visualInferenceQueueLock)
        {
            _visualInferenceQueue.Clear();
            _visualInferenceQueueEnabled = false;
        }
    }

    public IReadOnlyList<AiConciergeVisualInferenceBatch>
        DrainVisualInferenceQueue()
    {
        lock (_visualInferenceQueueLock)
        {
            var queued = _visualInferenceQueue.ToArray();
            _visualInferenceQueue.Clear();
            _visualInferenceQueueEnabled = false;
            return queued;
        }
    }

    public void AppendAssistantMessage(string text)
    {
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        AddMessage(new AiConciergeMessageDto(
            Guid.NewGuid().ToString("N"),
            "assistant",
            text,
            DateTimeOffset.Now));
    }

    public AiConciergeSessionDto GetSession()
    {
        lock (_sessionLock)
        {
            return new AiConciergeSessionDto(
                _session.Version,
                _runtimeThreadId,
                _session.Messages.ToArray());
        }
    }

    public async Task<IReadOnlyList<AiConciergeModelOptionDto>> GetModelOptionsAsync(
        CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken);
        var result = await RequestAsync(
            "model/list",
            new { limit = 100, includeHidden = false },
            cancellationToken);
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var models = new List<AiConciergeModelOptionDto>();
        foreach (var model in data.EnumerateArray())
        {
            var id = ReadString(model, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                id = ReadString(model, "model");
            }
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var efforts = new List<AiConciergeReasoningEffortDto>();
            if (model.TryGetProperty("supportedReasoningEfforts", out var effortItems) &&
                effortItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var effort in effortItems.EnumerateArray())
                {
                    var value = ReadString(effort, "reasoningEffort");
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        efforts.Add(new AiConciergeReasoningEffortDto(
                            value,
                            ReadString(effort, "description")));
                    }
                }
            }

            var serviceTiers = new List<AiConciergeServiceTierDto>();
            if (model.TryGetProperty("serviceTiers", out var serviceTierItems) &&
                serviceTierItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var serviceTier in serviceTierItems.EnumerateArray())
                {
                    var tierId = ReadString(serviceTier, "id");
                    if (!string.IsNullOrWhiteSpace(tierId))
                    {
                        serviceTiers.Add(new AiConciergeServiceTierDto(
                            tierId,
                            ReadString(serviceTier, "name"),
                            ReadString(serviceTier, "description")));
                    }
                }
            }

            models.Add(new AiConciergeModelOptionDto(
                id,
                string.IsNullOrWhiteSpace(ReadString(model, "displayName"))
                    ? id
                    : ReadString(model, "displayName"),
                model.TryGetProperty("isDefault", out var isDefault) &&
                isDefault.ValueKind is JsonValueKind.True,
                ReadString(model, "defaultReasoningEffort"),
                efforts,
                ReadString(model, "defaultServiceTier"),
                serviceTiers));
        }
        return models;
    }

    public async Task<string> SendAsync(
        string userText,
        string liveContextJson,
        string? model,
        string? reasoningEffort,
        string? serviceTier,
        IProgress<AiConciergeProgress> progress,
        CancellationToken cancellationToken,
        bool adaptiveAttributeInference = false,
        bool persistUserMessage = true,
        bool persistAssistantMessage = true)
    {
        if (string.IsNullOrWhiteSpace(userText))
        {
            throw new ArgumentException("メッセージを入力してください。", nameof(userText));
        }
        if (!await _turnGate.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("AIコンシェルジュは応答を生成中です。");
        }

        try
        {
            var userMessage = new AiConciergeMessageDto(
                Guid.NewGuid().ToString("N"),
                "user",
                userText.Trim(),
                DateTimeOffset.Now);
            if (persistUserMessage)
            {
                AddMessage(userMessage);
                progress.Report(new AiConciergeProgress("history"));
            }
            progress.Report(new AiConciergeProgress("status", "Codexを準備しています..."));

            await EnsureConnectedAsync(cancellationToken);
            progress.Report(new AiConciergeProgress("status", "会話を準備しています..."));
            var startedNewThread = await EnsureThreadAsync(cancellationToken);
            var prompt = BuildTurnPrompt(
                userMessage.Content,
                liveContextJson,
                startedNewThread,
                adaptiveAttributeInference);

            _activeTurnDelta = new StringBuilder();
            _activeCompletedAgentText = string.Empty;
            _activeProgress = progress;
            _activeTurnCompletion = new TaskCompletionSource<TurnCompletion>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            progress.Report(new AiConciergeProgress("status", "回答を考えています..."));
            var turnParameters = new Dictionary<string, object?>
            {
                ["threadId"] = GetThreadId(),
                ["input"] = new[]
                {
                    new { type = "text", text = prompt }
                },
                ["cwd"] = _workspaceDirectory,
                ["approvalPolicy"] = "never",
                ["sandboxPolicy"] = new { type = "readOnly" },
                ["personality"] = "friendly"
            };
            if (!string.IsNullOrWhiteSpace(model))
            {
                turnParameters["model"] = model.Trim();
            }
            if (!string.IsNullOrWhiteSpace(reasoningEffort))
            {
                turnParameters["effort"] = reasoningEffort.Trim();
            }
            turnParameters["serviceTier"] = string.IsNullOrWhiteSpace(serviceTier)
                ? null
                : serviceTier.Trim();

            LogDiagnostic(
                $"turn/start request: model={model?.Trim() ?? "(default)"}, " +
                $"effort={reasoningEffort?.Trim() ?? "(default)"}, " +
                $"serviceTier={serviceTier?.Trim() ?? "(default)"}");
            _activeTurnId = await RequestLifecycleWithNotificationFallbackAsync(
                "turn/start",
                turnParameters,
                TimeSpan.FromSeconds(30),
                "turn",
                cancellationToken);
            LogDiagnostic($"turn/start accepted: {_activeTurnId}");

            var completion = await _activeTurnCompletion.Task.WaitAsync(cancellationToken);
            LogDiagnostic($"turn completed: {completion.Status}");
            if (string.Equals(completion.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(completion.ErrorMessage)
                        ? "Codexが回答を生成できませんでした。"
                        : completion.ErrorMessage);
            }

            var assistantText = completion.Text.Trim();
            if (string.Equals(completion.Status, "interrupted", StringComparison.OrdinalIgnoreCase))
            {
                progress.Report(new AiConciergeProgress("interrupted"));
                return string.Empty;
            }
            if (string.IsNullOrWhiteSpace(assistantText))
            {
                assistantText = "回答を取得できませんでした。もう一度お試しください。";
            }
            if (persistAssistantMessage)
            {
                AddMessage(new AiConciergeMessageDto(
                    Guid.NewGuid().ToString("N"),
                    "assistant",
                    assistantText,
                    DateTimeOffset.Now));
            }
            progress.Report(new AiConciergeProgress("completed"));
            return assistantText;
        }
        finally
        {
            _activeTurnId = string.Empty;
            _activeTurnCompletion = null;
            _activeTurnDelta = null;
            _activeCompletedAgentText = string.Empty;
            _activeProgress = null;
            _turnGate.Release();
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        var threadId = GetThreadId();
        var turnId = _activeTurnId;
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId))
        {
            return;
        }

        await RequestAsync(
            "turn/interrupt",
            new { threadId, turnId },
            cancellationToken);
    }

    public void ResetSession()
    {
        if (IsBusy)
        {
            throw new InvalidOperationException("回答の生成中は会話をリセットできません。");
        }
        lock (_sessionLock)
        {
            _session = new PersistedSession();
            _runtimeThreadId = string.Empty;
            SaveSessionLocked();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _standardInput is not null)
        {
            return;
        }

        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false } && _standardInput is not null)
            {
                return;
            }

            DisposeProcess();
            Exception? lastError = null;
            var launchErrors = new List<string>();
            foreach (var launch in GetLaunchCandidates())
            {
                try
                {
                    LogDiagnostic($"starting: {launch.DisplayName}");
                    _lastStandardError = string.Empty;
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = launch.FileName,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                        CreateNoWindow = true,
                        WorkingDirectory = _workspaceDirectory
                    };
                    foreach (var argument in launch.Arguments)
                    {
                        startInfo.ArgumentList.Add(argument);
                    }
                    var userCodexHome = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".codex");
                    if (File.Exists(Path.Combine(userCodexHome, "auth.json")))
                    {
                        startInfo.Environment["CODEX_HOME"] = userCodexHome;
                    }

                    var process = Process.Start(startInfo)
                        ?? throw new InvalidOperationException($"{launch.DisplayName}を起動できませんでした。");
                    _process = process;
                    LogDiagnostic(
                        $"process started: {launch.DisplayName}, pid={process.Id}, " +
                        $"CODEX_HOME={startInfo.Environment["CODEX_HOME"] ?? "(default)"}");
                    _standardInput = process.StandardInput;
                    _standardInput.AutoFlush = true;
                    _ = Task.Run(() => ReadStandardOutputAsync(process, _lifetimeCancellation.Token));
                    _ = Task.Run(() => ReadStandardErrorAsync(process, _lifetimeCancellation.Token));
                    _ = Task.Run(() => ObserveProcessExitAsync(process));

                    var initializeResult = await RequestWithTimeoutAsync(
                        "initialize",
                        new
                        {
                            clientInfo = new
                            {
                                name = "gallerybrowser_ai_concierge",
                                title = "GalleryBrowser AI Concierge",
                                version = "0.1.0"
                            },
                            capabilities = new { experimentalApi = true }
                        },
                        TimeSpan.FromSeconds(20),
                        cancellationToken);
                    await SendNotificationAsync("initialized", new { }, cancellationToken);
                    LogDiagnostic(
                        $"initialized: {launch.DisplayName}, " +
                        $"server CODEX_HOME={ReadString(initializeResult, "codexHome")}");
                    return;
                }
                catch (TimeoutException ex)
                {
                    lastError = ex;
                    launchErrors.Add($"{launch.DisplayName}: 起動確認がタイムアウトしました。");
                    LogDiagnostic($"startup timeout: {launch.DisplayName}: {ex.Message}");
                    DisposeProcess();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                    launchErrors.Add($"{launch.DisplayName}: {ex.Message}");
                    LogDiagnostic($"startup failed: {launch.DisplayName}: {ex.Message}");
                    DisposeProcess();
                }
            }

            var detail = string.IsNullOrWhiteSpace(_lastStandardError)
                ? string.Join(Environment.NewLine, launchErrors.Take(4))
                : _lastStandardError;
            throw new InvalidOperationException(
                "Codex CLIを起動できませんでした。個人用実験版では独立したCodex CLIが必要です。" +
                "「npm install -g @openai/codex」でインストールした後、Codexへログインしてください。" +
                (string.IsNullOrWhiteSpace(detail) ? string.Empty : $"\n詳細: {detail}"),
                lastError);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task<bool> EnsureThreadAsync(CancellationToken cancellationToken)
    {
        var threadId = GetThreadId();
        if (!string.IsNullOrWhiteSpace(threadId))
        {
            try
            {
                LogDiagnostic($"thread/resume request: {threadId}");
                var resumedThreadId = await RequestLifecycleWithNotificationFallbackAsync(
                    "thread/resume",
                    new
                    {
                        threadId,
                        cwd = _workspaceDirectory,
                        approvalPolicy = "never",
                        sandbox = "read-only",
                        personality = "friendly"
                    },
                    TimeSpan.FromSeconds(30),
                    "thread",
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(resumedThreadId) &&
                    !string.Equals(resumedThreadId, threadId, StringComparison.Ordinal))
                {
                    SetThreadId(resumedThreadId);
                    threadId = resumedThreadId;
                }
                LogDiagnostic($"thread/resume completed: {threadId}");
                return false;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                LogDiagnostic($"thread/resume failed: {ex.Message}");
                SetThreadId(string.Empty);
            }
        }

        LogDiagnostic("thread/start request");
        threadId = await RequestLifecycleWithNotificationFallbackAsync(
            "thread/start",
            new
            {
                cwd = _workspaceDirectory,
                approvalPolicy = "never",
                sandbox = "read-only",
                personality = "friendly",
                serviceName = "gallerybrowser_ai_concierge",
                ephemeral = true,
                dynamicTools = DynamicTools
            },
            TimeSpan.FromSeconds(45),
            "thread",
            cancellationToken);
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new InvalidOperationException("Codexの会話IDを取得できませんでした。");
        }
        SetThreadId(threadId);
        LogDiagnostic($"thread/start completed: {threadId}");
        return true;
    }

    private string BuildTurnPrompt(
        string userText,
        string liveContextJson,
        bool startedNewThread,
        bool adaptiveAttributeInference)
    {
        var builder = new StringBuilder();
        builder.AppendLine("以下はGalleryBrowserが提供した現在のライブコンテキストです。");
        builder.AppendLine("```json");
        builder.AppendLine(string.IsNullOrWhiteSpace(liveContextJson) ? "{}" : liveContextJson);
        builder.AppendLine("```");
        builder.AppendLine(
            "GalleryBrowserの操作を依頼された場合は、利用可能なgallerybrowser_*ツールを使用してください。" +
            "操作できない内容を実行済みと説明しないでください。" +
            "ファイル削除、作者削除、外部コマンド実行など、提供されていない操作は実行できません。");
        builder.AppendLine(
            "チャットへ表示する作業中の報告は、結論と現在件数を中心に2～4行へ要約してください。" +
            "長い説明を1段落へ連結せず、話題が変わる箇所で改行してください。" +
            "最終回答も要点を優先し、長くなる場合は最大6項目の箇条書きへ要約してください。");
        builder.AppendLine(
            "属性推論付与の依頼では$infer-gallery-attributesスキルを使用してください。" +
            "自然言語の条件をget_attribute_inference_batchのconditionsへ変換し、" +
            "同じ条件とnextCursorで作品がなくなるまで小さなバッチを反復してください。" +
            "アプリ独自の総件数上限はありません。");
        builder.AppendLine(
            "生成AI作品・同人アニメの作者フォルダ配下について、未設定フォルダの代表サムネイルを" +
            "自動整備する依頼ではgallerybrowser_assign_missing_folder_thumbnailsを使用してください。");
        if (adaptiveAttributeInference)
        {
            builder.AppendLine(
                "このターンは属性推論の自動難易度仕分けが有効です。" +
                "最初はincludeImages=falseで取得し、文字情報だけで十分な根拠がある作品だけを処理してください。" +
                "画像がなければ判断できない作品はgallerybrowser_defer_visual_attribute_inferenceへ渡してください。" +
                "保留作品は現在のターン完了後にGalleryBrowserがSol・高推論の別ターンで自動処理します。");
        }
        if (startedNewThread)
        {
            var currentMessageId = GetLastMessageId();
            var recentMessages = GetRecentMessages()
                .Where(message => !string.Equals(message.Id, currentMessageId, StringComparison.Ordinal))
                .TakeLast(12)
                .ToArray();
            if (recentMessages.Length > 0)
            {
                builder.AppendLine("以前の会話を復元できなかった場合に備えた直近履歴です。事実の参照だけに使ってください。");
                foreach (var message in recentMessages)
                {
                    builder.Append(message.Role).Append(": ").AppendLine(message.Content);
                }
            }
        }
        builder.AppendLine("ユーザーの依頼:");
        builder.AppendLine(userText);
        return builder.ToString();
    }

    private async Task<JsonElement> RequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Codex要求IDを登録できませんでした。");
        }

        try
        {
            await WriteMessageAsync(new { method, id, @params = parameters }, cancellationToken);
            using var cancellationRegistration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            return await completion.Task;
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    private async Task<JsonElement> RequestWithTimeoutAsync(
        string method,
        object parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            return await RequestAsync(method, parameters, timeoutCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogDiagnostic($"{method} timeout after {timeout.TotalSeconds:0}s");
            throw new TimeoutException(
                $"Codexの「{method}」が{timeout.TotalSeconds:0}秒以内に応答しませんでした。" +
                $"診断ログ: {_diagnosticPath}");
        }
    }

    private async Task<string> RequestLifecycleWithNotificationFallbackAsync(
        string method,
        object parameters,
        TimeSpan timeout,
        string lifecycleType,
        CancellationToken cancellationToken)
    {
        var notification = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (string.Equals(lifecycleType, "thread", StringComparison.Ordinal))
        {
            _threadStartedCompletion = notification;
        }
        else
        {
            _turnStartedCompletion = notification;
        }

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        requestCancellation.CancelAfter(timeout);
        try
        {
            var responseTask = RequestAsync(method, parameters, requestCancellation.Token);
            var winner = await Task.WhenAny(responseTask, notification.Task);
            if (winner == notification.Task)
            {
                var lifecycleId = await notification.Task;
                requestCancellation.Cancel();
                try
                {
                    await responseTask;
                }
                catch (OperationCanceledException)
                {
                    // The lifecycle notification already confirmed success. A missing
                    // matching response must not keep the UI waiting indefinitely.
                }
                LogDiagnostic(
                    $"{method} accepted from {lifecycleType}/started notification: " +
                    lifecycleId);
                return lifecycleId;
            }

            var result = await responseTask;
            var responseId = ReadNestedString(result, lifecycleType, "id");
            if (string.IsNullOrWhiteSpace(responseId))
            {
                throw new InvalidOperationException(
                    $"Codexの「{method}」応答から{lifecycleType} IDを取得できませんでした。");
            }
            return responseId;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogDiagnostic($"{method} timeout after {timeout.TotalSeconds:0}s");
            throw new TimeoutException(
                $"Codexの「{method}」が{timeout.TotalSeconds:0}秒以内に応答しませんでした。" +
                $"診断ログ: {_diagnosticPath}");
        }
        finally
        {
            if (string.Equals(lifecycleType, "thread", StringComparison.Ordinal) &&
                ReferenceEquals(_threadStartedCompletion, notification))
            {
                _threadStartedCompletion = null;
            }
            else if (string.Equals(lifecycleType, "turn", StringComparison.Ordinal) &&
                     ReferenceEquals(_turnStartedCompletion, notification))
            {
                _turnStartedCompletion = null;
            }
        }
    }

    private Task SendNotificationAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken) =>
        WriteMessageAsync(new { method, @params = parameters }, cancellationToken);

    private async Task WriteMessageAsync(object payload, CancellationToken cancellationToken)
    {
        var writer = _standardInput
            ?? throw new InvalidOperationException("Codex App Serverへ接続されていません。");
        // The App Server stdio transport is JSONL: each RPC message must occupy exactly one line.
        var json = JsonSerializer.Serialize(payload, ProtocolJsonOptions);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadStandardOutputAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }
                if (!string.IsNullOrWhiteSpace(line))
                {
                    HandleServerMessage(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal during application shutdown.
        }
        catch (Exception ex)
        {
            FailConnection(ex);
        }
    }

    private async Task ReadStandardErrorAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _lastStandardError = line.Trim();
                    LogDiagnostic($"stderr: {_lastStandardError}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal during application shutdown.
        }
        catch
        {
            // Stderr is diagnostic only.
        }
    }

    private void HandleServerMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var method = root.TryGetProperty("method", out var serverMethodElement)
                ? serverMethodElement.GetString()
                : null;
            if (string.Equals(method, "item/tool/call", StringComparison.Ordinal) &&
                root.TryGetProperty("id", out var toolRequestIdElement))
            {
                var toolParameters = root.TryGetProperty("params", out var toolParams)
                    ? toolParams.Clone()
                    : default;
                _ = HandleDynamicToolCallAsync(toolRequestIdElement.Clone(), toolParameters);
                return;
            }

            if (root.TryGetProperty("id", out var idElement) &&
                idElement.TryGetInt64(out var id) &&
                _pendingRequests.TryGetValue(id, out var request))
            {
                LogDiagnostic(
                    $"stdout response: id={id}, " +
                    $"error={root.TryGetProperty("error", out _)}");
                if (root.TryGetProperty("error", out var error))
                {
                    request.TrySetException(new InvalidOperationException(FormatRpcError(error)));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    request.TrySetResult(result.Clone());
                }
                else
                {
                    request.TrySetResult(default);
                }
                return;
            }

            if (!root.TryGetProperty("method", out var methodElement))
            {
                return;
            }
            method = methodElement.GetString();
            if (!string.IsNullOrWhiteSpace(method) &&
                !method.EndsWith("/delta", StringComparison.OrdinalIgnoreCase) &&
                !method.Contains("tokenUsage", StringComparison.OrdinalIgnoreCase))
            {
                LogDiagnostic($"stdout notification: {method}");
            }
            var parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement
                : default;
            if (string.Equals(method, "thread/started", StringComparison.Ordinal))
            {
                var threadId = ReadNestedString(parameters, "thread", "id");
                if (!string.IsNullOrWhiteSpace(threadId))
                {
                    _threadStartedCompletion?.TrySetResult(threadId);
                }
                return;
            }
            if (string.Equals(method, "turn/started", StringComparison.Ordinal))
            {
                var turnId = ReadNestedString(parameters, "turn", "id");
                if (!string.IsNullOrWhiteSpace(turnId))
                {
                    _turnStartedCompletion?.TrySetResult(turnId);
                }
                return;
            }
            if (string.Equals(method, "item/agentMessage/delta", StringComparison.Ordinal))
            {
                var delta = ReadString(parameters, "delta");
                if (!string.IsNullOrEmpty(delta))
                {
                    _activeTurnDelta?.Append(delta);
                    _activeProgress?.Report(new AiConciergeProgress("delta", Delta: delta));
                }
                return;
            }
            if (string.Equals(method, "item/completed", StringComparison.Ordinal) &&
                parameters.ValueKind == JsonValueKind.Object &&
                parameters.TryGetProperty("item", out var item) &&
                string.Equals(ReadString(item, "type"), "agentMessage", StringComparison.Ordinal))
            {
                var text = ReadString(item, "text");
                var phase = ReadString(item, "phase");
                if (!string.IsNullOrWhiteSpace(text) &&
                    (string.IsNullOrWhiteSpace(phase) ||
                     string.Equals(phase, "final_answer", StringComparison.OrdinalIgnoreCase)))
                {
                    _activeCompletedAgentText = text;
                }
                return;
            }
            if (string.Equals(method, "turn/completed", StringComparison.Ordinal))
            {
                CompleteActiveTurn(parameters);
            }
        }
        catch (JsonException)
        {
            // Ignore non-protocol diagnostic output from the child process.
        }
    }

    private async Task HandleDynamicToolCallAsync(JsonElement requestId, JsonElement parameters)
    {
        AiConciergeToolResult result;
        try
        {
            var toolName = ReadString(parameters, "tool");
            var arguments = parameters.ValueKind == JsonValueKind.Object &&
                            parameters.TryGetProperty("arguments", out var argumentsElement)
                ? argumentsElement.GetRawText()
                : "{}";
            LogDiagnostic($"dynamic tool requested: {toolName}");
            if (string.Equals(
                    toolName,
                    "gallerybrowser_defer_visual_attribute_inference",
                    StringComparison.Ordinal))
            {
                result = QueueVisualAttributeInference(arguments);
            }
            else
            {
                var handler = ToolHandler;
                result = handler is null
                    ? new AiConciergeToolResult(
                        false,
                        "AIコンシェルジュの操作ウィンドウが閉じられているため実行できませんでした。")
                    : await handler(
                        new AiConciergeToolRequest(toolName, arguments),
                        _lifetimeCancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            result = new AiConciergeToolResult(false, "操作はキャンセルされました。");
        }
        catch (Exception ex)
        {
            LogDiagnostic($"dynamic tool failed: {ex.Message}");
            result = new AiConciergeToolResult(false, $"操作に失敗しました: {ex.Message}");
        }

        try
        {
            var contentItems = result.ContentItems is { Count: > 0 }
                ? result.ContentItems.Select(item =>
                    string.Equals(item.Type, "inputImage", StringComparison.Ordinal)
                        ? (object)new { type = "inputImage", imageUrl = item.ImageUrl }
                        : new { type = "inputText", text = item.Text }).ToArray()
                : [new { type = "inputText", text = result.Message }];
            await WriteMessageAsync(
                new
                {
                    id = requestId,
                    result = new
                    {
                        success = result.Success,
                        contentItems
                    }
                },
                _lifetimeCancellation.Token);
            LogDiagnostic($"dynamic tool completed: success={result.Success}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDiagnostic($"dynamic tool response failed: {ex.Message}");
        }
    }

    private AiConciergeToolResult QueueVisualAttributeInference(string arguments)
    {
        var parsed = JsonSerializer.Deserialize<VisualInferenceQueueArguments>(
            arguments,
            ProtocolJsonOptions) ?? new VisualInferenceQueueArguments();
        var section = parsed.Section.Trim();
        var paths = parsed.Paths
            .Select(path => path?.Trim() ?? string.Empty)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        if (string.IsNullOrWhiteSpace(section) || paths.Length == 0)
        {
            return new AiConciergeToolResult(
                false,
                "画像推論へ保留する区分と作品パスを指定してください。");
        }

        lock (_visualInferenceQueueLock)
        {
            if (!_visualInferenceQueueEnabled)
            {
                return new AiConciergeToolResult(
                    false,
                    "現在は自動難易度仕分けが無効です。このターン内で推論してください。");
            }
            _visualInferenceQueue.Add(new AiConciergeVisualInferenceBatch(section, paths));
        }
        return new AiConciergeToolResult(
            true,
            $"{paths.Length:N0}件を画像推論ステージへ保留しました。");
    }

    private void CompleteActiveTurn(JsonElement parameters)
    {
        if (_activeTurnCompletion is null)
        {
            return;
        }
        var turn = parameters.ValueKind == JsonValueKind.Object &&
                   parameters.TryGetProperty("turn", out var turnElement)
            ? turnElement
            : default;
        var status = ReadString(turn, "status");
        var errorMessage = string.Empty;
        if (turn.ValueKind == JsonValueKind.Object &&
            turn.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.Object)
        {
            errorMessage = ReadString(error, "message");
        }
        var text = string.IsNullOrWhiteSpace(_activeCompletedAgentText)
            ? _activeTurnDelta?.ToString() ?? string.Empty
            : _activeCompletedAgentText;
        _activeTurnCompletion.TrySetResult(new TurnCompletion(status, text, errorMessage));
    }

    private async Task ObserveProcessExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(_lifetimeCancellation.Token);
            if (!_disposed)
            {
                LogDiagnostic($"process exited: code={process.ExitCode}");
                FailConnection(new InvalidOperationException(
                    string.IsNullOrWhiteSpace(_lastStandardError)
                        ? "Codex App Serverが終了しました。"
                        : $"Codex App Serverが終了しました: {_lastStandardError}"));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal during shutdown.
        }
    }

    private void FailConnection(Exception exception)
    {
        foreach (var pending in _pendingRequests.Values)
        {
            pending.TrySetException(exception);
        }
        _activeTurnCompletion?.TrySetException(exception);
    }

    private IReadOnlyList<CodexLaunch> GetLaunchCandidates()
    {
        var candidates = new List<CodexLaunch>();
        AddLaunchCandidate(candidates, Environment.GetEnvironmentVariable("GALLERYBROWSER_CODEX_PATH"));
        var npmRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm");
        var npmCodexPackageRoot = Path.Combine(
            npmRoot,
            "node_modules",
            "@openai",
            "codex");
        var nativeCodex = FindNpmNativeCodex(npmCodexPackageRoot);
        if (!string.IsNullOrWhiteSpace(nativeCodex))
        {
            candidates.Add(new CodexLaunch(
                nativeCodex,
                ["app-server", "--listen", "stdio://"],
                nativeCodex));
        }
        var npmCodexScript = Path.Combine(
            npmCodexPackageRoot,
            "bin",
            "codex.js");
        if (File.Exists(npmCodexScript))
        {
            var nodeExecutable = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "nodejs",
                "node.exe");
            candidates.Add(new CodexLaunch(
                File.Exists(nodeExecutable) ? nodeExecutable : "node.exe",
                [npmCodexScript, "app-server", "--listen", "stdio://"],
                npmCodexScript));
        }
        AddLaunchCandidate(
            candidates,
            Path.Combine(npmRoot, "codex.cmd"));
        candidates.Add(new CodexLaunch(
            "codex.exe",
            ["app-server", "--listen", "stdio://"],
            "PATH上のcodex.exe"));
        candidates.Add(new CodexLaunch(
            "codex",
            ["app-server", "--listen", "stdio://"],
            "PATH上のcodex"));
        return candidates
            .DistinctBy(candidate => candidate.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? FindNpmNativeCodex(string packageRoot)
    {
        var nativePackagesRoot = Path.Combine(packageRoot, "node_modules");
        if (!Directory.Exists(nativePackagesRoot))
        {
            return null;
        }
        try
        {
            return Directory
                .EnumerateFiles(nativePackagesRoot, "codex.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void AddLaunchCandidate(List<CodexLaunch> candidates, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!File.Exists(path))
        {
            return;
        }
        if (string.Equals(Path.GetExtension(path), ".cmd", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetExtension(path), ".bat", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(new CodexLaunch(
                Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                ["/d", "/s", "/c", $"\"\"{path}\" app-server --listen stdio://\""],
                path));
            return;
        }
        candidates.Add(new CodexLaunch(
            path,
            ["app-server", "--listen", "stdio://"],
            path));
    }

    private void WriteWorkspaceInstructions()
    {
        var resourceName = typeof(AiConciergeService).Assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(
                "Assets.AiConciergeKnowledge.md",
                StringComparison.Ordinal));
        if (resourceName is null)
        {
            return;
        }
        using var stream = typeof(AiConciergeService).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return;
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = reader.ReadToEnd();
        var targetPath = Path.Combine(_workspaceDirectory, "AGENTS.md");
        if (!File.Exists(targetPath) ||
            !string.Equals(File.ReadAllText(targetPath), content, StringComparison.Ordinal))
        {
            File.WriteAllText(targetPath, content, new UTF8Encoding(false));
        }
    }

    private void WriteWorkspaceSkill()
    {
        var assembly = typeof(AiConciergeService).Assembly;
        var skillDirectory = Path.Combine(
            _workspaceDirectory,
            ".agents",
            "skills",
            "infer-gallery-attributes");
        Directory.CreateDirectory(skillDirectory);
        WriteEmbeddedResource(
            assembly,
            "Assets.AiConciergeSkills.infer-gallery-attributes.SKILL.md",
            Path.Combine(skillDirectory, "SKILL.md"));
        var agentsDirectory = Path.Combine(skillDirectory, "agents");
        Directory.CreateDirectory(agentsDirectory);
        WriteEmbeddedResource(
            assembly,
            "Assets.AiConciergeSkills.infer-gallery-attributes.agents.openai.yaml",
            Path.Combine(agentsDirectory, "openai.yaml"));
    }

    private static void WriteEmbeddedResource(
        Assembly assembly,
        string resourceSuffix,
        string targetPath)
    {
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(
                resourceSuffix,
                StringComparison.Ordinal));
        if (resourceName is null)
        {
            return;
        }
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return;
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = reader.ReadToEnd();
        if (!File.Exists(targetPath) ||
            !string.Equals(File.ReadAllText(targetPath), content, StringComparison.Ordinal))
        {
            File.WriteAllText(targetPath, content, new UTF8Encoding(false));
        }
    }

    private void ResetOversizedDiagnosticLog()
    {
        try
        {
            if (File.Exists(_diagnosticPath) &&
                new FileInfo(_diagnosticPath).Length > 1_048_576)
            {
                File.Delete(_diagnosticPath);
            }
        }
        catch
        {
            // Diagnostics must never block the concierge.
        }
    }

    private void LogDiagnostic(string message)
    {
        try
        {
            lock (_diagnosticLock)
            {
                File.AppendAllText(
                    _diagnosticPath,
                    $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never block the concierge.
        }
    }

    private PersistedSession LoadSession()
    {
        try
        {
            if (!File.Exists(_sessionPath))
            {
                return new PersistedSession();
            }
            return JsonSerializer.Deserialize<PersistedSession>(
                       File.ReadAllText(_sessionPath),
                       JsonOptions)
                   ?? new PersistedSession();
        }
        catch (JsonException)
        {
            var corruptPath = _sessionPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(_sessionPath, corruptPath, overwrite: false);
            return new PersistedSession();
        }
    }

    private void AddMessage(AiConciergeMessageDto message)
    {
        lock (_sessionLock)
        {
            _session.Messages.Add(message);
            if (_session.Messages.Count > MaximumMessages)
            {
                _session.Messages.RemoveRange(0, _session.Messages.Count - MaximumMessages);
            }
            SaveSessionLocked();
        }
    }

    private IReadOnlyList<AiConciergeMessageDto> GetRecentMessages()
    {
        lock (_sessionLock)
        {
            return _session.Messages.ToArray();
        }
    }

    private string GetLastMessageId()
    {
        lock (_sessionLock)
        {
            return _session.Messages.LastOrDefault()?.Id ?? string.Empty;
        }
    }

    private string GetThreadId()
    {
        lock (_sessionLock)
        {
            return _runtimeThreadId;
        }
    }

    private void SetThreadId(string threadId)
    {
        lock (_sessionLock)
        {
            _runtimeThreadId = threadId;
        }
    }

    private void SaveSessionLocked()
    {
        var directory = Path.GetDirectoryName(_sessionPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var temporaryPath = _sessionPath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(_session, JsonOptions),
            new UTF8Encoding(false));
        File.Move(temporaryPath, _sessionPath, overwrite: true);
    }

    private static string ReadNestedString(JsonElement element, string objectName, string valueName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(objectName, out var nested))
        {
            return string.Empty;
        }
        return ReadString(nested, valueName);
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }
        return value.GetString() ?? string.Empty;
    }

    private static string FormatRpcError(JsonElement error)
    {
        var code = error.ValueKind == JsonValueKind.Object &&
                   error.TryGetProperty("code", out var codeElement) &&
                   codeElement.TryGetInt32(out var parsedCode)
            ? parsedCode.ToString()
            : string.Empty;
        var message = ReadString(error, "message");
        return string.IsNullOrWhiteSpace(code)
            ? message
            : $"Codex App Server ({code}): {message}";
    }

    private void DisposeProcess()
    {
        try
        {
            _standardInput?.Dispose();
        }
        catch
        {
            // Best effort cleanup.
        }
        _standardInput = null;
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
            _process.Dispose();
            _process = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _lifetimeCancellation.Cancel();
        DisposeProcess();
        _lifetimeCancellation.Dispose();
    }
}
