namespace GalleryBrowser.Models;

public sealed record UserMetricsRegressionFactorDto(
    string Key,
    string Label,
    string Group,
    double Coefficient,
    double OddsRatio,
    double? StandardError,
    double? PValue,
    int SupportCount,
    string Direction);

public sealed record UserMetricsRegressionModelDto(
    string Kind,
    string Label,
    string Status,
    string Message,
    int EntityCount,
    double PositiveObservationCount,
    double NegativeObservationCount,
    int CandidateFeatureCount,
    int UsedFeatureCount,
    double AreaUnderCurve,
    double PseudoRSquared,
    double? AkaikeInformationCriterion,
    double? NullAkaikeInformationCriterion,
    double? DeltaAkaikeInformationCriterion,
    double? EffectiveParameterCount,
    double? MaximumVarianceInflationFactor,
    int RemovedCollinearFeatureCount,
    IReadOnlyList<UserMetricsRegressionFactorDto> Factors,
    IReadOnlyList<string> Notes);

public sealed record UserMetricsRegressionResultDto(
    string Category,
    string GeneratedAt,
    UserMetricsRegressionModelDto Purchase,
    UserMetricsRegressionModelDto Rating);
