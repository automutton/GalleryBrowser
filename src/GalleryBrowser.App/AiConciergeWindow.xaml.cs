using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GalleryBrowser.Models;
using GalleryBrowser.Services;

namespace GalleryBrowser;

public partial class AiConciergeWindow : Window
{
    private sealed record ReasoningSelection(string Value, string Label);
    private sealed record ServiceTierSelection(
        string Value,
        string Label,
        string Description);
    private sealed record ModelRoutingDecision(
        string Model,
        string ReasoningEffort,
        string ModelDisplayName,
        string Reason,
        bool UsesAdaptiveAttributeInference = false);

    private const string AutomaticModelSelectionId = "__auto__";
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitorHandle, ref MonitorInfo monitorInfo);

    private static readonly Brush AssistantBackground = new SolidColorBrush(Color.FromRgb(31, 39, 52));
    private static readonly Brush UserBackground = new SolidColorBrush(Color.FromRgb(42, 54, 65));
    private static readonly Brush AssistantBorder = new SolidColorBrush(Color.FromRgb(57, 70, 90));
    private static readonly Brush UserBorder = new SolidColorBrush(Color.FromRgb(93, 117, 67));
    private static readonly Brush PrimaryText = new SolidColorBrush(Color.FromRgb(229, 235, 244));
    private static readonly Brush SecondaryText = new SolidColorBrush(Color.FromRgb(127, 144, 169));

    private readonly AiConciergeService _service;
    private readonly Func<AiConciergeToolRequest, CancellationToken, Task<AiConciergeToolResult>>
        _actionExecutor;
    private readonly Func<AiConciergeToolRequest, CancellationToken, Task<AiConciergeToolResult>>
        _toolHandler;
    private readonly Action<string, string, string> _saveRuntimeSettings;
    private readonly string _initialModel;
    private readonly string _initialReasoningEffort;
    private readonly string _initialServiceTier;
    private IReadOnlyList<AiConciergeModelOptionDto> _modelOptions = [];
    private CancellationTokenSource? _requestCancellation;
    private string _contextJson;
    private string _viewLabel;
    private TextBox? _streamingTextBlock;
    private readonly StringBuilder _streamingText = new();
    private bool _busy;
    private bool _ownerEventsAttached;
    private bool _positionUpdateQueued;
    private bool _modelControlsReady;

    internal AiConciergeWindow(
        AiConciergeService service,
        string contextJson,
        string viewLabel,
        AiConciergeSettingsDto settings,
        Action<string, string, string> saveRuntimeSettings,
        Func<AiConciergeToolRequest, CancellationToken, Task<AiConciergeToolResult>>
            actionExecutor)
    {
        _service = service;
        _saveRuntimeSettings = saveRuntimeSettings;
        _initialModel = settings.Model;
        _initialReasoningEffort = settings.ReasoningEffort;
        _initialServiceTier = settings.ServiceTier;
        _actionExecutor = actionExecutor;
        _toolHandler = HandleToolRequestAsync;
        _service.ToolHandler = _toolHandler;
        _contextJson = string.IsNullOrWhiteSpace(contextJson) ? "{}" : contextJson;
        _viewLabel = string.IsNullOrWhiteSpace(viewLabel) ? "GalleryBrowser" : viewLabel;
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private Task<AiConciergeToolResult> HandleToolRequestAsync(
        AiConciergeToolRequest request,
        CancellationToken cancellationToken)
    {
        if (Dispatcher.CheckAccess())
        {
            return ConfirmAndExecuteToolAsync(request, cancellationToken);
        }

        return Dispatcher.InvokeAsync(
                () => ConfirmAndExecuteToolAsync(request, cancellationToken))
            .Task
            .Unwrap();
    }

    private async Task<AiConciergeToolResult> ConfirmAndExecuteToolAsync(
        AiConciergeToolRequest request,
        CancellationToken cancellationToken)
    {
        var actionLabel = GetToolActionLabel(request.ToolName);
        var isInferenceRead =
            request.ToolName is
                "gallerybrowser_get_attribute_inference_batch" or
                "gallerybrowser_get_character_candidates_for_inference";
        var isInferenceApply =
            request.ToolName is
                "gallerybrowser_apply_inferred_attributes" or
                "gallerybrowser_queue_attribute_inference_review";
        var needsConfirmation = !isInferenceRead && !isInferenceApply;
        if (needsConfirmation)
        {
            var argumentPreview = FormatArguments(
                request.ToolName,
                request.ArgumentsJson);
            var confirmation = MessageBox.Show(
                this,
                $"{actionLabel}\n\n{argumentPreview}\n\nこの操作を実行しますか？",
                "AIコンシェルジュの操作確認",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);
            if (confirmation != MessageBoxResult.OK)
            {
                return new AiConciergeToolResult(false, "ユーザーが操作をキャンセルしました。");
            }
        }

        ShowStatus($"{actionLabel}を実行しています...");
        try
        {
            var result = await _actionExecutor(request, cancellationToken);
            ShowStatus(result.Message, isError: !result.Success);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new AiConciergeToolResult(false, "操作はキャンセルされました。");
        }
    }

    private static string GetToolActionLabel(string toolName) =>
        toolName switch
        {
            "gallerybrowser_navigate" => "画面を移動",
            "gallerybrowser_set_gallery_filters" => "Galleryのフィルタを変更",
            "gallerybrowser_search" => "検索条件を変更",
            "gallerybrowser_refresh" => "データを再読み込み",
            "gallerybrowser_manage_tab" => "タブを操作",
            "gallerybrowser_open_attribute_editor" => "属性の登録・解除画面を開く",
            "gallerybrowser_get_attribute_inference_batch" =>
                "Title未付与作品と推論候補を取得",
            "gallerybrowser_get_character_candidates_for_inference" =>
                "推論用Character候補を取得",
            "gallerybrowser_apply_inferred_attributes" =>
                "AI推論したTitle・Characterを一括付与",
            "gallerybrowser_queue_attribute_inference_review" =>
                "属性推論の要確認リストへ追加",
            "gallerybrowser_update_creator_tracking" => "Creator Trackingを変更",
            "gallerybrowser_assign_missing_folder_thumbnails" =>
                "未設定フォルダのサムネイルを自動設定",
            _ => $"GalleryBrowser操作: {toolName}"
        };

    private static string FormatArguments(string toolName, string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (toolName == "gallerybrowser_apply_inferred_attributes" &&
                document.RootElement.TryGetProperty("assignments", out var assignments) &&
                assignments.ValueKind == JsonValueKind.Array)
            {
                var samples = assignments
                    .EnumerateArray()
                    .Take(8)
                    .Select(assignment => new
                    {
                        path = assignment.TryGetProperty("path", out var path)
                            ? path.GetString()
                            : string.Empty,
                        titleIds = assignment.TryGetProperty("titleIds", out var titleIds)
                            ? titleIds.Clone()
                            : default,
                        characterIds = assignment.TryGetProperty(
                            "characterIds",
                            out var characterIds)
                            ? characterIds.Clone()
                            : default,
                        confidence = assignment.TryGetProperty(
                            "confidence",
                            out var confidence)
                            ? confidence.GetDouble()
                            : 0
                    })
                    .ToArray();
                return JsonSerializer.Serialize(
                    new
                    {
                        assignmentCount = assignments.GetArrayLength(),
                        firstAssignments = samples,
                        note = assignments.GetArrayLength() > samples.Length
                            ? $"ほか{assignments.GetArrayLength() - samples.Length:N0}件"
                            : string.Empty
                    },
                    new JsonSerializerOptions { WriteIndented = true });
            }
            if (toolName == "gallerybrowser_assign_missing_folder_thumbnails" &&
                document.RootElement.TryGetProperty("sections", out var sections) &&
                sections.ValueKind == JsonValueKind.Array)
            {
                var labels = sections
                    .EnumerateArray()
                    .Select(section => section.GetString() switch
                    {
                        "ai" => "生成AI作品",
                        "doujin_anime" => "同人アニメ",
                        var value => value ?? string.Empty
                    })
                    .Where(label => !string.IsNullOrWhiteSpace(label));
                return "対象区分: " + string.Join("、", labels);
            }
            return JsonSerializer.Serialize(
                document.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return argumentsJson;
        }
    }

    public void UpdateContext(string contextJson, string viewLabel)
    {
        _contextJson = string.IsNullOrWhiteSpace(contextJson) ? "{}" : contextJson;
        _viewLabel = string.IsNullOrWhiteSpace(viewLabel) ? "GalleryBrowser" : viewLabel;
        ContextLabel.Text = $"✦ {_viewLabel}";
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachOwnerEvents();
        PositionBesideOwner();
        UpdateContext(_contextJson, _viewLabel);
        RenderMessages();
        SetBusy(_service.IsBusy);
        InitializeModelControls();
        _ = LoadModelOptionsAsync();
        InputTextBox.Focus();
    }

    private void InitializeModelControls()
    {
        _modelControlsReady = false;
        var items = new List<AiConciergeModelOptionDto>
        {
            CreateAutomaticModelOption(),
            new AiConciergeModelOptionDto(
                string.Empty,
                "Codex既定",
                true,
                string.Empty,
                [],
                string.Empty,
                [])
        };
        if (!string.IsNullOrWhiteSpace(_initialModel) &&
            !string.Equals(
                _initialModel,
                AutomaticModelSelectionId,
                StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new AiConciergeModelOptionDto(
                _initialModel,
                $"{_initialModel}（保存済み）",
                false,
                _initialReasoningEffort,
                [],
                _initialServiceTier,
                []));
        }
        ModelComboBox.ItemsSource = items;
        ModelComboBox.SelectedValue = _initialModel;
        if (ModelComboBox.SelectedIndex < 0)
        {
            ModelComboBox.SelectedIndex = 0;
        }
        PopulateReasoningOptions(_initialReasoningEffort);
        PopulateServiceTierOptions(_initialServiceTier);
        _modelControlsReady = true;
    }

    private async Task LoadModelOptionsAsync()
    {
        try
        {
            var models = await _service.GetModelOptionsAsync(CancellationToken.None);
            if (!IsLoaded)
            {
                return;
            }
            _modelOptions = models;
            _modelControlsReady = false;
            var items = new List<AiConciergeModelOptionDto>
            {
                CreateAutomaticModelOption(),
                new(
                    string.Empty,
                    "Codex既定",
                    true,
                    string.Empty,
                    [],
                    string.Empty,
                    [])
            };
            items.AddRange(models);
            if (!string.IsNullOrWhiteSpace(_initialModel) &&
                items.All(item => !string.Equals(
                    item.Id,
                    _initialModel,
                    StringComparison.OrdinalIgnoreCase)))
            {
                items.Add(new AiConciergeModelOptionDto(
                    _initialModel,
                    $"{_initialModel}（保存済み）",
                    false,
                    _initialReasoningEffort,
                    [],
                    _initialServiceTier,
                    []));
            }
            ModelComboBox.ItemsSource = items;
            ModelComboBox.SelectedValue = _initialModel;
            if (ModelComboBox.SelectedIndex < 0)
            {
                ModelComboBox.SelectedIndex = 0;
            }
            PopulateReasoningOptions(_initialReasoningEffort);
            PopulateServiceTierOptions(_initialServiceTier);
            _modelControlsReady = true;
        }
        catch (Exception ex)
        {
            ShowStatus(
                $"モデル一覧を取得できませんでした。Codex既定の設定で会話できます。\n{ex.Message}",
                isError: true);
        }
    }

    private static AiConciergeModelOptionDto CreateAutomaticModelOption() =>
        new(
            AutomaticModelSelectionId,
            "自動（内容に応じて選択）",
            false,
            string.Empty,
            [],
            string.Empty,
            []);

    private void PopulateReasoningOptions(string? preferredValue = null)
    {
        var selectedModel = ModelComboBox.SelectedValue?.ToString() ?? string.Empty;
        var model = _modelOptions.FirstOrDefault(item =>
            string.Equals(item.Id, selectedModel, StringComparison.OrdinalIgnoreCase));
        var values = model?.SupportedReasoningEfforts
            .Select(item => item.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        if (values.Count == 0)
        {
            values.AddRange(["none", "low", "medium", "high", "xhigh", "max"]);
        }

        var items = new List<ReasoningSelection>
        {
            new(
                string.Empty,
                string.Equals(
                    selectedModel,
                    AutomaticModelSelectionId,
                    StringComparison.OrdinalIgnoreCase)
                    ? "推論: 自動"
                    : "推論: Codex既定")
        };
        items.AddRange(values.Select(value => new ReasoningSelection(
            value,
            $"推論: {FormatReasoningEffort(value)}")));
        var target = preferredValue ?? ReasoningComboBox.SelectedValue?.ToString() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(target) &&
            items.All(item => !string.Equals(
                item.Value,
                target,
                StringComparison.OrdinalIgnoreCase)))
        {
            items.Add(new ReasoningSelection(target, $"推論: {target}（保存済み）"));
        }
        ReasoningComboBox.ItemsSource = items;
        ReasoningComboBox.SelectedValue = target;
        if (ReasoningComboBox.SelectedIndex < 0)
        {
            ReasoningComboBox.SelectedIndex = 0;
        }
    }

    private static string FormatReasoningEffort(string value) =>
        value.ToLowerInvariant() switch
        {
            "none" => "なし",
            "low" => "低",
            "medium" => "中",
            "high" => "高",
            "xhigh" => "非常に高い",
            "max" => "最大",
            _ => value
        };

    private void PopulateServiceTierOptions(string? preferredValue = null)
    {
        var selectedModel = ModelComboBox.SelectedValue?.ToString() ?? string.Empty;
        IEnumerable<AiConciergeServiceTierDto> availableTiers;
        if (string.Equals(
                selectedModel,
                AutomaticModelSelectionId,
                StringComparison.OrdinalIgnoreCase))
        {
            availableTiers = _modelOptions
                .SelectMany(item => item.ServiceTiers)
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First());
        }
        else
        {
            availableTiers = _modelOptions
                .FirstOrDefault(item => string.Equals(
                    item.Id,
                    selectedModel,
                    StringComparison.OrdinalIgnoreCase))
                ?.ServiceTiers ?? [];
        }

        var items = new List<ServiceTierSelection>
        {
            new(string.Empty, "速度: 既定", "Codexで現在有効な速度設定を使用します。")
        };
        items.AddRange(availableTiers.Select(tier => new ServiceTierSelection(
            tier.Id,
            $"速度: {FormatServiceTierName(tier)}",
            tier.Description)));

        var target = preferredValue ??
                     ServiceTierComboBox.SelectedValue?.ToString() ??
                     string.Empty;
        if (!string.IsNullOrWhiteSpace(target) &&
            items.All(item => !string.Equals(
                item.Value,
                target,
                StringComparison.OrdinalIgnoreCase)))
        {
            items.Add(new ServiceTierSelection(
                target,
                $"速度: {target}（保存済み）",
                "保存済みの速度設定です。"));
        }

        ServiceTierComboBox.ItemsSource = items;
        ServiceTierComboBox.SelectedValue = target;
        if (ServiceTierComboBox.SelectedIndex < 0)
        {
            ServiceTierComboBox.SelectedIndex = 0;
        }
        UpdateServiceTierToolTip();
    }

    private static string FormatServiceTierName(AiConciergeServiceTierDto tier)
    {
        if (!string.IsNullOrWhiteSpace(tier.Name))
        {
            return tier.Name;
        }
        return tier.Id.Equals("fast", StringComparison.OrdinalIgnoreCase)
            ? "Fast"
            : tier.Id;
    }

    private ModelRoutingDecision ResolveModelForTurn(string message)
    {
        var selectedModel = ModelComboBox.SelectedValue?.ToString() ?? string.Empty;
        var selectedEffort = ReasoningComboBox.SelectedValue?.ToString() ?? string.Empty;
        if (!string.Equals(
                selectedModel,
                AutomaticModelSelectionId,
                StringComparison.OrdinalIgnoreCase))
        {
            var selectedOption = _modelOptions.FirstOrDefault(item =>
                string.Equals(item.Id, selectedModel, StringComparison.OrdinalIgnoreCase));
            return new ModelRoutingDecision(
                selectedModel,
                selectedEffort,
                selectedOption?.DisplayName ??
                    (string.IsNullOrWhiteSpace(selectedModel) ? "Codex既定" : selectedModel),
                string.Empty);
        }

        var usesAdaptiveAttributeInference = IsAttributeInferenceRequest(message);
        var useSol = !usesAdaptiveAttributeInference && RequiresSol(message);
        var target = FindAvailableModel(useSol ? "sol" : "terra") ??
                     FindAvailableModel(useSol ? "terra" : "sol") ??
                     _modelOptions.FirstOrDefault(item => item.IsDefault) ??
                     _modelOptions.FirstOrDefault();
        var resolvedModel = target?.Id ?? string.Empty;
        var resolvedEffort = selectedEffort;
        if (string.IsNullOrWhiteSpace(resolvedEffort))
        {
            resolvedEffort = useSol ? "high" : "medium";
            if (target is not null &&
                target.SupportedReasoningEfforts.Count > 0 &&
                target.SupportedReasoningEfforts.All(item => !string.Equals(
                    item.Value,
                    resolvedEffort,
                    StringComparison.OrdinalIgnoreCase)))
            {
                resolvedEffort = target.DefaultReasoningEffort;
            }
        }

        var reason = usesAdaptiveAttributeInference
            ? "作品ごとに難易度を仕分け、文字情報で判断できる作品と画像判断が必要な作品を別々に処理します。"
            : useSol
                ? "画像・視覚判断または複雑な推論が必要な依頼として判定しました。"
                : "文字情報を中心に処理できる依頼として判定しました。";
        return new ModelRoutingDecision(
            resolvedModel,
            resolvedEffort,
            target?.DisplayName ??
                (string.IsNullOrWhiteSpace(resolvedModel) ? "Codex既定" : resolvedModel),
            reason,
            usesAdaptiveAttributeInference);
    }

    private ModelRoutingDecision ResolveVisualInferenceRouting()
    {
        var target = FindAvailableModel("sol") ??
                     FindAvailableModel("terra") ??
                     _modelOptions.FirstOrDefault(item => item.IsDefault) ??
                     _modelOptions.FirstOrDefault();
        var effort = ReasoningComboBox.SelectedValue?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(effort))
        {
            effort = "high";
            if (target is not null &&
                target.SupportedReasoningEfforts.Count > 0 &&
                target.SupportedReasoningEfforts.All(item => !string.Equals(
                    item.Value,
                    effort,
                    StringComparison.OrdinalIgnoreCase)))
            {
                effort = target.DefaultReasoningEffort;
            }
        }
        return new ModelRoutingDecision(
            target?.Id ?? string.Empty,
            effort,
            target?.DisplayName ??
                (string.IsNullOrWhiteSpace(target?.Id) ? "Codex既定" : target.Id),
            "文字情報だけでは根拠が不足した作品を、サムネイルを含めて再判定します。");
    }

    private AiConciergeModelOptionDto? FindAvailableModel(string familyName) =>
        _modelOptions.FirstOrDefault(item =>
            item.Id.Contains(familyName, StringComparison.OrdinalIgnoreCase) ||
            item.DisplayName.Contains(familyName, StringComparison.OrdinalIgnoreCase));

    private static bool RequiresSol(string message)
    {
        var normalized = message.ToLowerInvariant();
        var explicitlyTextOnly =
            ContainsAny(
                normalized,
                "文字情報だけ",
                "文字情報のみ",
                "テキストだけ",
                "テキストのみ",
                "ファイル名だけ",
                "ファイル名のみ",
                "作者名だけ",
                "作者名のみ",
                "text only") &&
            !ContainsAny(
                normalized,
                "画像も",
                "画像から",
                "サムネイルから",
                "vision",
                "image");
        if (explicitlyTextOnly)
        {
            return false;
        }

        if (ContainsAny(
                normalized,
                "画像",
                "サムネイル",
                "見た目",
                "外観",
                "視覚",
                "写真",
                "イラスト",
                "絵から",
                "顔",
                "衣装",
                "情報が乏しい",
                "判別が難しい",
                "vision",
                "image",
                "visual",
                "thumbnail",
                "appearance"))
        {
            return true;
        }

        if (ContainsAny(
                normalized,
                "厳密",
                "複雑",
                "詳細に検証",
                "比較検証",
                "原因を特定",
                "難しい推論",
                "deep analysis",
                "complex reasoning"))
        {
            return true;
        }

        var isAttributeInference = ContainsAny(
            normalized,
            "属性を推論",
            "推論付与",
            "title属性",
            "character属性",
            "属性推論");
        var hasStrongTextSignal = ContainsAny(
            normalized,
            "ファイル名",
            "作者名",
            "フォルダ名",
            "文字情報",
            "テキスト");
        return isAttributeInference && !hasStrongTextSignal;
    }

    private static bool IsAttributeInferenceRequest(string message)
    {
        var normalized = message.ToLowerInvariant();
        if (ContainsAny(
                normalized,
                "属性を推論",
                "属性推論",
                "推論付与",
                "titleとcharacterを付け",
                "title・characterを付け",
                "未分類作品を整理"))
        {
            return true;
        }
        return normalized.Contains("推論", StringComparison.Ordinal) &&
               ContainsAny(normalized, "title", "character", "属性") &&
               ContainsAny(normalized, "付与", "登録", "整理");
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(value.Contains);

    private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_modelControlsReady)
        {
            return;
        }
        _modelControlsReady = false;
        PopulateReasoningOptions();
        PopulateServiceTierOptions();
        _modelControlsReady = true;
        SaveRuntimeSettings();
    }

    private void OnReasoningSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_modelControlsReady)
        {
            SaveRuntimeSettings();
        }
    }

    private void OnServiceTierSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateServiceTierToolTip();
        if (_modelControlsReady)
        {
            SaveRuntimeSettings();
        }
    }

    private void UpdateServiceTierToolTip()
    {
        if (ServiceTierComboBox.SelectedItem is ServiceTierSelection selection)
        {
            ServiceTierComboBox.ToolTip = string.IsNullOrWhiteSpace(selection.Description)
                ? "次のメッセージから使用する速度。自動では変更されません"
                : selection.Description;
        }
    }

    private void SaveRuntimeSettings()
    {
        try
        {
            _saveRuntimeSettings(
                ModelComboBox.SelectedValue?.ToString() ?? string.Empty,
                ReasoningComboBox.SelectedValue?.ToString() ?? string.Empty,
                ServiceTierComboBox.SelectedValue?.ToString() ?? string.Empty);
        }
        catch (Exception ex)
        {
            ShowStatus($"モデル設定を保存できませんでした。\n{ex.Message}", isError: true);
        }
    }

    private void PositionBesideOwner()
    {
        if (Owner is not { IsLoaded: true } owner ||
            owner.WindowState == WindowState.Minimized)
        {
            return;
        }

        var ownerBounds = GetWindowBounds(owner);
        var workArea = GetOwnerWorkArea(owner);
        var visibleTop = Math.Max(ownerBounds.Top, workArea.Top);
        var visibleBottom = Math.Min(ownerBounds.Bottom, workArea.Bottom);
        var availableHeight = Math.Max(0, visibleBottom - visibleTop);
        var targetHeight = Math.Min(
            Math.Max(MinHeight, availableHeight),
            Math.Max(MinHeight, workArea.Height));
        var targetWidth = ActualWidth > 0 ? ActualWidth : Width;

        Height = targetHeight;
        Top = Math.Clamp(
            visibleTop,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - targetHeight));

        var rightCandidate = ownerBounds.Right;
        var leftCandidate = ownerBounds.Left - targetWidth;
        if (rightCandidate + targetWidth <= workArea.Right)
        {
            Left = rightCandidate;
        }
        else if (leftCandidate >= workArea.Left)
        {
            Left = leftCandidate;
        }
        else
        {
            // A maximized owner leaves no external space. Keep the sidecar visible
            // by attaching it over the owner's right edge instead of detaching it.
            Left = Math.Clamp(
                ownerBounds.Right - targetWidth,
                workArea.Left,
                Math.Max(workArea.Left, workArea.Right - targetWidth));
        }
    }

    private void AttachOwnerEvents()
    {
        if (_ownerEventsAttached || Owner is null)
        {
            return;
        }

        Owner.LocationChanged += OnOwnerBoundsChanged;
        Owner.SizeChanged += OnOwnerBoundsChanged;
        Owner.StateChanged += OnOwnerStateChanged;
        Owner.DpiChanged += OnOwnerDpiChanged;
        _ownerEventsAttached = true;
    }

    private void DetachOwnerEvents()
    {
        if (!_ownerEventsAttached || Owner is null)
        {
            return;
        }

        Owner.LocationChanged -= OnOwnerBoundsChanged;
        Owner.SizeChanged -= OnOwnerBoundsChanged;
        Owner.StateChanged -= OnOwnerStateChanged;
        Owner.DpiChanged -= OnOwnerDpiChanged;
        _ownerEventsAttached = false;
    }

    private void OnOwnerBoundsChanged(object? sender, EventArgs e) => QueuePositionUpdate();

    private void OnOwnerStateChanged(object? sender, EventArgs e) => QueuePositionUpdate();

    private void OnOwnerDpiChanged(object sender, DpiChangedEventArgs e) => QueuePositionUpdate();

    private void QueuePositionUpdate()
    {
        if (_positionUpdateQueued || !IsLoaded)
        {
            return;
        }

        _positionUpdateQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _positionUpdateQueued = false;
            PositionBesideOwner();
        }, DispatcherPriority.Render);
    }

    private static Rect GetWindowBounds(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != nint.Zero && GetWindowRect(handle, out var nativeBounds))
        {
            return DeviceRectToLogical(window, nativeBounds);
        }

        return new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
    }

    private static Rect GetOwnerWorkArea(Window owner)
    {
        var handle = new WindowInteropHelper(owner).Handle;
        if (handle != nint.Zero)
        {
            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            var monitorInfo = new MonitorInfo
            {
                Size = Marshal.SizeOf<MonitorInfo>()
            };
            if (monitor != nint.Zero && GetMonitorInfo(monitor, ref monitorInfo))
            {
                return DeviceRectToLogical(owner, monitorInfo.Work);
            }
        }

        return SystemParameters.WorkArea;
    }

    private static Rect DeviceRectToLogical(Visual visual, NativeRect rect)
    {
        var source = PresentationSource.FromVisual(visual);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(rect.Left, rect.Top));
        var bottomRight = transform.Transform(new Point(rect.Right, rect.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendAsync();

    private async void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter ||
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ||
            Keyboard.IsKeyDown(Key.ImeProcessed))
        {
            return;
        }

        e.Handled = true;
        await SendAsync();
    }

    private async Task SendAsync()
    {
        var message = InputTextBox.Text.Trim();
        if (_busy || message.Length == 0)
        {
            return;
        }

        if (message.Length > 16_000)
        {
            ShowStatus("メッセージは16,000文字以内にしてください。", isError: true);
            return;
        }

        InputTextBox.Clear();
        SetBusy(true);
        ShowStatus("Codexを準備しています...");
        _streamingTextBlock = null;
        _streamingText.Clear();
        _requestCancellation?.Dispose();
        _requestCancellation = new CancellationTokenSource();

        var contextJson = ShareContextCheckBox.IsChecked == true
            ? _contextJson
            : JsonSerializer.Serialize(new
            {
                capturedAt = DateTimeOffset.Now,
                viewLabel = _viewLabel,
                detailSharing = false
            });
        var adaptiveQueueActive = false;

        try
        {
            var progress = new Progress<AiConciergeProgress>(HandleProgress);
            var routing = ResolveModelForTurn(message);
            if (routing.UsesAdaptiveAttributeInference)
            {
                _service.BeginAdaptiveAttributeInference();
                adaptiveQueueActive = true;
            }
            else
            {
                _service.CancelAdaptiveAttributeInference();
            }
            if (string.Equals(
                    ModelComboBox.SelectedValue?.ToString(),
                    AutomaticModelSelectionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                var effortLabel = string.IsNullOrWhiteSpace(routing.ReasoningEffort)
                    ? "Codex既定"
                    : FormatReasoningEffort(routing.ReasoningEffort);
                ShowStatus(
                    $"自動選択: {routing.ModelDisplayName} / 推論: {effortLabel}\n" +
                    routing.Reason);
            }
            await _service.SendAsync(
                message,
                contextJson,
                routing.Model,
                routing.ReasoningEffort,
                ServiceTierComboBox.SelectedValue?.ToString(),
                progress,
                _requestCancellation.Token,
                adaptiveAttributeInference: routing.UsesAdaptiveAttributeInference);
            if (routing.UsesAdaptiveAttributeInference)
            {
                var visualBatches = NormalizeVisualInferenceBatches(
                    _service.DrainVisualInferenceQueue());
                adaptiveQueueActive = false;
                if (visualBatches.Count > 0)
                {
                    var visualRouting = ResolveVisualInferenceRouting();
                    var visualResults = new List<string>(visualBatches.Count);
                    for (var index = 0; index < visualBatches.Count; index++)
                    {
                        _requestCancellation.Token.ThrowIfCancellationRequested();
                        var batch = visualBatches[index];
                        var effortLabel = string.IsNullOrWhiteSpace(
                            visualRouting.ReasoningEffort)
                            ? "Codex既定"
                            : FormatReasoningEffort(visualRouting.ReasoningEffort);
                        ShowStatus(
                            $"画像推論 {index + 1:N0}/{visualBatches.Count:N0}: " +
                            $"{visualRouting.ModelDisplayName} / 推論: {effortLabel}\n" +
                            $"{batch.Paths.Count:N0}件をサムネイル込みで再判定しています。");
                        var result = await _service.SendAsync(
                            BuildVisualInferencePrompt(batch),
                            contextJson,
                            visualRouting.Model,
                            visualRouting.ReasoningEffort,
                            ServiceTierComboBox.SelectedValue?.ToString(),
                            progress,
                            _requestCancellation.Token,
                            persistUserMessage: false,
                            persistAssistantMessage: false);
                        if (!string.IsNullOrWhiteSpace(result))
                        {
                            visualResults.Add(result.Trim());
                        }
                        await _actionExecutor(
                            new AiConciergeToolRequest(
                                "gallerybrowser_queue_attribute_inference_review",
                                JsonSerializer.Serialize(new
                                {
                                    section = batch.Section,
                                    works = batch.Paths.Select(path => new
                                    {
                                        path,
                                        reason = "画像と文字情報を確認しても既存Titleを特定できませんでした。"
                                    })
                                })),
                            _requestCancellation.Token);
                    }
                    _service.AppendAssistantMessage(
                        BuildVisualInferenceSummary(
                            visualBatches,
                            visualResults,
                            routing,
                            visualRouting));
                }
            }
        }
        catch (OperationCanceledException)
        {
            ShowStatus("応答を停止しました。");
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, isError: true);
        }
        finally
        {
            if (adaptiveQueueActive)
            {
                _service.CancelAdaptiveAttributeInference();
            }
            SetBusy(false);
            RenderMessages();
            InputTextBox.Focus();
        }
    }

    private static IReadOnlyList<AiConciergeVisualInferenceBatch>
        NormalizeVisualInferenceBatches(
            IReadOnlyList<AiConciergeVisualInferenceBatch> batches)
    {
        var result = new List<AiConciergeVisualInferenceBatch>();
        foreach (var sectionGroup in batches
                     .Where(batch => !string.IsNullOrWhiteSpace(batch.Section))
                     .GroupBy(batch => batch.Section.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var paths = sectionGroup
                .SelectMany(batch => batch.Paths)
                .Select(path => path?.Trim() ?? string.Empty)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var chunk in paths.Chunk(20))
            {
                result.Add(new AiConciergeVisualInferenceBatch(
                    sectionGroup.Key,
                    chunk));
            }
        }
        return result;
    }

    private static string BuildVisualInferencePrompt(
        AiConciergeVisualInferenceBatch batch)
    {
        var targetJson = JsonSerializer.Serialize(new
        {
            section = batch.Section,
            paths = batch.Paths
        });
        return $"""
            これは属性推論の自動難易度仕分けで保留された画像推論ステージです。
            次の対象だけを処理してください。対象外の作品へ広げないでください。
            {targetJson}

            gallerybrowser_get_attribute_inference_batchをsection={batch.Section}、
            cursor=""、batchSize={batch.Paths.Count}、includeImages=true、
            conditions.pathsへ上記pathsを完全一致で指定して呼んでください。
            サムネイルと文字情報を総合し、十分な根拠がある既存Titleを選び、
            必要ならCharacter候補を取得してgallerybrowser_apply_inferred_attributesで付与してください。
            このステージではgallerybrowser_defer_visual_attribute_inferenceを呼ばず、
            根拠が不足する作品は未付与のままにしてください。
            GalleryBrowserが未付与のまま残った作品を要確認リストへ自動登録します。
            最後は付与・要確認・エラー件数だけを簡潔に報告してください。
            """;
    }

    private static string BuildVisualInferenceSummary(
        IReadOnlyList<AiConciergeVisualInferenceBatch> batches,
        IReadOnlyList<string> results,
        ModelRoutingDecision textRouting,
        ModelRoutingDecision visualRouting)
    {
        var total = batches.Sum(batch => batch.Paths.Count);
        var textEffort = string.IsNullOrWhiteSpace(textRouting.ReasoningEffort)
            ? "Codex既定"
            : FormatReasoningEffort(textRouting.ReasoningEffort);
        var visualEffort = string.IsNullOrWhiteSpace(visualRouting.ReasoningEffort)
            ? "Codex既定"
            : FormatReasoningEffort(visualRouting.ReasoningEffort);
        var builder = new System.Text.StringBuilder()
            .Append("難易度別の属性推論を完了しました。")
            .AppendLine()
            .Append("- 文字情報で判断できる作品: ")
            .Append(textRouting.ModelDisplayName)
            .Append(" / 推論: ")
            .Append(textEffort)
            .AppendLine()
            .Append("- 画像判断が必要な作品: ")
            .Append(visualRouting.ModelDisplayName)
            .Append(" / 推論: ")
            .Append(visualEffort)
            .Append("（")
            .Append(total.ToString("N0"))
            .Append("件）");
        if (results.Count > 0)
        {
            builder.AppendLine().AppendLine().Append(
                string.Join(Environment.NewLine, results));
        }
        return builder.ToString();
    }

    private void HandleProgress(AiConciergeProgress progress)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => HandleProgress(progress));
            return;
        }

        switch (progress.Kind)
        {
            case "history":
                RenderMessages();
                break;
            case "status":
                ShowStatus(progress.Message);
                break;
            case "delta":
                AppendStreamingText(progress.Delta);
                break;
            case "completed":
                HideStatus();
                _streamingTextBlock = null;
                _streamingText.Clear();
                RenderMessages();
                break;
            case "interrupted":
                ShowStatus("応答を停止しました。");
                _streamingTextBlock = null;
                _streamingText.Clear();
                break;
        }
    }

    private void AppendStreamingText(string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        if (_streamingTextBlock is null)
        {
            var (_, textBlock) = AddMessageBubble("assistant", string.Empty);
            _streamingTextBlock = textBlock;
        }
        _streamingText.Append(delta);
        _streamingTextBlock.Text = FormatAssistantDisplayText(_streamingText.ToString());
        ScrollToBottom();
    }

    private void RenderMessages()
    {
        if (!IsLoaded && !Dispatcher.CheckAccess())
        {
            return;
        }

        MessagePanel.Children.Clear();
        _streamingTextBlock = null;
        _streamingText.Clear();
        var messages = _service.GetSession().Messages;
        if (messages.Count == 0)
        {
            AddWelcomeContent();
        }
        else
        {
            foreach (var message in messages)
            {
                AddMessageBubble(message.Role, message.Content);
            }
        }
        ScrollToBottom();
    }

    private void AddWelcomeContent()
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(14, 70, 14, 20),
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 370
        };
        panel.Children.Add(new TextBlock
        {
            Text = "✦",
            FontSize = 36,
            Foreground = new SolidColorBrush(Color.FromRgb(199, 236, 103)),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = "何をお手伝いしましょう？",
            Margin = new Thickness(0, 12, 0, 5),
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = "現在の画面、選択中のフィルタや作品、作者、Explorerのパスを踏まえて案内します。",
            Foreground = new SolidColorBrush(Color.FromRgb(155, 168, 187)),
            FontSize = 12,
            LineHeight = 20,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });

        var suggestions = new WrapPanel
        {
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        AddSuggestionButton(suggestions, "今の画面を説明", "今の画面でできることを教えて");
        AddSuggestionButton(suggestions, "選択状態を要約", "選択中の状態を要約して");
        AddSuggestionButton(suggestions, "次の確認を提案", "次に確認すべきことを提案して");
        panel.Children.Add(suggestions);
        MessagePanel.Children.Add(panel);
    }

    private void AddSuggestionButton(Panel panel, string label, string prompt)
    {
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(3),
            Padding = new Thickness(10, 6, 10, 6),
            FontSize = 11
        };
        button.Click += (_, _) =>
        {
            InputTextBox.Text = prompt;
            InputTextBox.CaretIndex = InputTextBox.Text.Length;
            InputTextBox.Focus();
        };
        panel.Children.Add(button);
    }

    private (Border Bubble, TextBox Text) AddMessageBubble(string role, string content)
    {
        var isUser = string.Equals(role, "user", StringComparison.OrdinalIgnoreCase);
        var container = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 14),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 410
        };
        container.Children.Add(new TextBlock
        {
            Text = isUser ? "YOU" : "CODEX",
            Margin = new Thickness(5, 0, 5, 5),
            Foreground = SecondaryText,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            TextAlignment = isUser ? TextAlignment.Right : TextAlignment.Left
        });

        var text = new TextBox
        {
            Text = isUser ? content : FormatAssistantDisplayText(content),
            Foreground = PrimaryText,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            SelectionBrush = new SolidColorBrush(Color.FromRgb(91, 117, 45)),
            Cursor = Cursors.IBeam
        };
        var bubble = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            Background = isUser ? UserBackground : AssistantBackground,
            BorderBrush = isUser ? UserBorder : AssistantBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = isUser
                ? new CornerRadius(13, 4, 13, 13)
                : new CornerRadius(4, 13, 13, 13),
            Child = text
        };
        container.Children.Add(bubble);
        MessagePanel.Children.Add(container);
        return (bubble, text);
    }

    private static string FormatAssistantDisplayText(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        var normalized = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        normalized = Regex.Replace(
            normalized,
            @"[ \t]+(?=(?:[-*•]\s+|\d{1,2}[.)]\s+))",
            "\n",
            RegexOptions.CultureInvariant);

        var formatted = new StringBuilder(normalized.Length + 32);
        var lines = normalized.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            formatted.Append(FormatLongAssistantLine(line));
            if (index < lines.Length - 1)
            {
                formatted.AppendLine();
            }
        }

        return Regex.Replace(
                formatted.ToString(),
                @"\n{3,}",
                "\n\n",
                RegexOptions.CultureInvariant)
            .TrimEnd();
    }

    private static string FormatLongAssistantLine(string line)
    {
        if (line.Length < 150 ||
            line.TrimStart().StartsWith("```", StringComparison.Ordinal) ||
            line.TrimStart().StartsWith("{", StringComparison.Ordinal) ||
            line.TrimStart().StartsWith("[", StringComparison.Ordinal))
        {
            return line;
        }

        var formatted = new StringBuilder(line.Length + 16);
        var charactersSinceBreak = 0;
        var sentencesSinceBreak = 0;
        foreach (var character in line)
        {
            formatted.Append(character);
            charactersSinceBreak++;
            if (character is not ('。' or '！' or '？' or '!' or '?'))
            {
                continue;
            }

            sentencesSinceBreak++;
            if (charactersSinceBreak < 110 &&
                (sentencesSinceBreak < 2 || charactersSinceBreak < 75))
            {
                continue;
            }

            formatted.AppendLine();
            formatted.AppendLine();
            charactersSinceBreak = 0;
            sentencesSinceBreak = 0;
        }
        return formatted.ToString().TrimEnd();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        InputTextBox.IsEnabled = !busy;
        SendButton.IsEnabled = !busy;
        SendButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        ResetButton.IsEnabled = !busy;
        StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowStatus(string message, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            HideStatus();
            return;
        }
        StatusText.Text = FormatAssistantDisplayText(message);
        StatusText.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(255, 194, 199))
            : new SolidColorBrush(Color.FromRgb(190, 208, 231));
        StatusBorder.Background = isError
            ? new SolidColorBrush(Color.FromRgb(67, 34, 42))
            : new SolidColorBrush(Color.FromRgb(37, 53, 74));
        StatusBorder.BorderBrush = isError
            ? new SolidColorBrush(Color.FromRgb(150, 65, 76))
            : new SolidColorBrush(Color.FromRgb(73, 97, 126));
        StatusBorder.Visibility = Visibility.Visible;
    }

    private void HideStatus()
    {
        StatusBorder.Visibility = Visibility.Collapsed;
        StatusText.Text = string.Empty;
    }

    private void ScrollToBottom()
    {
        Dispatcher.BeginInvoke(
            () => MessageScrollViewer.ScrollToEnd(),
            DispatcherPriority.Background);
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        _requestCancellation?.Cancel();
        try
        {
            await _service.CancelAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, isError: true);
        }
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        if (MessageBox.Show(
                this,
                "AIコンシェルジュの会話履歴をリセットしますか？",
                "会話をリセット",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            _service.ResetSession();
            HideStatus();
            RenderMessages();
            InputTextBox.Focus();
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, isError: true);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DetachOwnerEvents();
        if (_service.ToolHandler == _toolHandler)
        {
            _service.ToolHandler = null;
        }
        _requestCancellation?.Cancel();
        _requestCancellation?.Dispose();
        _requestCancellation = null;
    }
}
