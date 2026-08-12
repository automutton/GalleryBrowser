namespace GalleryBrowser.Models;

public sealed record AiAttributeCandidateDto(
    long Id,
    string Name,
    string ParentName,
    string CategoryName,
    int CreatorWorkCount,
    int TotalWorkCount,
    IReadOnlyList<string> Aliases);

public sealed record AiAttributeInferenceWorkDto(
    string Gid,
    string Path,
    string Name,
    string Category,
    string Creator,
    IReadOnlyList<string> TitleHintFolders,
    int ImageCount,
    IReadOnlyList<AiAttributeCandidateDto> TitleCandidates);

public sealed record AiAttributeLearningExampleDto(
    string Creator,
    string WorkName,
    IReadOnlyList<string> PredictedTitles,
    IReadOnlyList<string> PredictedCharacters,
    IReadOnlyList<string> CorrectedTitles,
    IReadOnlyList<string> CorrectedCharacters);

public sealed record AiAttributeInferenceBatchDto(
    IReadOnlyList<AiAttributeInferenceWorkDto> Works,
    IReadOnlyList<AiAttributeLearningExampleDto> LearningExamples,
    int TotalUnassigned,
    string NextCursor);

public sealed class AiAttributeInferenceFilterDto
{
    public IReadOnlyList<string> Creators { get; set; } = [];
    public IReadOnlyList<string> Tags { get; set; } = [];
    public IReadOnlyList<string> Paths { get; set; } = [];
    public string TagMatch { get; set; } = "any";
    public string PathContains { get; set; } = string.Empty;
    public string FileNameContains { get; set; } = string.Empty;
    public int? MinimumRating { get; set; }
    public int? MaximumRating { get; set; }
    public int? MinimumImageCount { get; set; }
    public int? MaximumImageCount { get; set; }
}

public sealed record AiAttributeCharacterRequestDto(
    string Path,
    long TitleId);

public sealed record AiAttributeCharacterCandidateSetDto(
    string Path,
    long TitleId,
    string Title,
    IReadOnlyList<AiAttributeCandidateDto> Candidates);

public sealed record AiAttributeInferenceAssignmentDto(
    string Path,
    IReadOnlyList<long> TitleIds,
    IReadOnlyList<long> CharacterIds,
    double Confidence,
    string Reason);

public sealed record AiAttributeInferenceApplyResultDto(
    int AppliedCount,
    int SkippedCount,
    IReadOnlyList<string> Errors);

public sealed record AiAttributeInferenceReviewRequestDto(
    string Path,
    string Reason);

public sealed record AiAttributeInferenceReviewItemDto(
    string Gid,
    string Path,
    string Name,
    string Category,
    string Creator,
    int ImageCount,
    string Reason,
    string CreatedAt);
