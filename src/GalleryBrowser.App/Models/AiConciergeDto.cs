namespace GalleryBrowser.Models;

public sealed record AiConciergeMessageDto(
    string Id,
    string Role,
    string Content,
    DateTimeOffset CreatedAt);

public sealed record AiConciergeSessionDto(
    int Version,
    string ThreadId,
    IReadOnlyList<AiConciergeMessageDto> Messages);

public sealed record AiConciergeSettingsDto(
    string DataDirectory,
    string Model,
    string ReasoningEffort,
    string ServiceTier);

public sealed record AiConciergeReasoningEffortDto(
    string Value,
    string Description);

public sealed record AiConciergeServiceTierDto(
    string Id,
    string Name,
    string Description);

public sealed record AiConciergeModelOptionDto(
    string Id,
    string DisplayName,
    bool IsDefault,
    string DefaultReasoningEffort,
    IReadOnlyList<AiConciergeReasoningEffortDto> SupportedReasoningEfforts,
    string DefaultServiceTier,
    IReadOnlyList<AiConciergeServiceTierDto> ServiceTiers);

public sealed record AiConciergeProgress(
    string Kind,
    string Message = "",
    string Delta = "");

public sealed record AiConciergeToolRequest(
    string ToolName,
    string ArgumentsJson);

public sealed record AiConciergeToolContentItem(
    string Type,
    string Text = "",
    string ImageUrl = "");

public sealed record AiConciergeToolResult(
    bool Success,
    string Message,
    IReadOnlyList<AiConciergeToolContentItem>? ContentItems = null);

public sealed record AiConciergeVisualInferenceBatch(
    string Section,
    IReadOnlyList<string> Paths);
