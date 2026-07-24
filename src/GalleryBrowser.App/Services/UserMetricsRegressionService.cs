using System.Globalization;
using System.IO;
using System.Text.Json;
using GalleryBrowser.Models;
using Microsoft.Data.Sqlite;

namespace GalleryBrowser.Services;

internal sealed class UserMetricsRegressionService(
    string sourceDatabasePath,
    string resultDatabasePath,
    IReadOnlyList<CreatorTrackingMetricSettingDto> metricSettings)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaximumFeatureCount = 180;
    private const int MaximumFeaturesPerGroup = 40;
    private const double RegularizationStrength = 0.015;
    private const double MaximumVarianceInflationFactor = 10;

    private sealed class WorkRow
    {
        public required string Gid { get; init; }
        public required string Creator { get; init; }
        public required string LegacyTitle { get; init; }
        public required string LegacyCharacter { get; init; }
        public required string MediaType { get; init; }
        public required string Extension { get; init; }
        public int Rating { get; init; }
        public int ImageCount { get; init; }
        public long TotalSize { get; init; }
        public DateTime LastWriteTime { get; init; }
        public IReadOnlyList<string> Titles { get; set; } = [];
        public IReadOnlyList<string> Characters { get; set; } = [];
        public IReadOnlyList<string> Tags { get; set; } = [];
    }

    private sealed class CreatorAggregate(string creator)
    {
        public string Creator { get; } = creator;
        public List<WorkRow> Works { get; } = [];
        public Dictionary<string, int> TitleCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> CharacterCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> TagCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> CoreTitles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> CoreTags { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FeatureMetadata(string key, string label, string group, bool isBinary)
    {
        public string Key { get; } = key;
        public string Label { get; } = label;
        public string Group { get; } = group;
        public bool IsBinary { get; } = isBinary;
        public int SupportCount { get; set; }
        public double Sum { get; set; }
        public double SumSquares { get; set; }
    }

    private sealed class RegressionObservation(string id, double outcome, double weight)
    {
        public string Id { get; } = id;
        public double Outcome { get; } = outcome;
        public double Weight { get; } = weight;
        public Dictionary<string, double> Values { get; } = new(StringComparer.Ordinal);
    }

    private sealed record SparseObservation(double Outcome, double Weight, int[] Indices, double[] Values);

    private sealed record ModelDiagnostics(
        double[] StandardErrors,
        double[] PValues,
        double AkaikeInformationCriterion,
        double NullAkaikeInformationCriterion,
        double EffectiveParameterCount);

    private sealed class CreatorTrackingRegressionData
    {
        public IReadOnlyList<CreatorTrackingActivityLinkDto> ActivityLinks { get; init; } = [];
        public IReadOnlyDictionary<string, int> EvaluationMetrics { get; init; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<CreatorTrackingSubscriptionDto> Subscriptions { get; init; } = [];
        public IReadOnlyList<CreatorTrackingPurchaseDto> Purchases { get; init; } = [];
        public string TrackingStatus { get; init; } = string.Empty;
        public string ActivityStatus { get; init; } = string.Empty;
        public string FollowUpStatus { get; init; } = string.Empty;
        public int TrackingDays { get; init; }
    }

    public UserMetricsRegressionResultDto? Read(string category)
    {
        using var connection = OpenConnection(resultDatabasePath, SqliteOpenMode.ReadWriteCreate);
        EnsureResultTable(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM user_metrics_regression_results WHERE category = $category COLLATE NOCASE;";
        command.Parameters.AddWithValue("$category", category.Trim());
        var payload = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<UserMetricsRegressionResultDto>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public UserMetricsRegressionResultDto Analyze(string category)
    {
        var normalizedCategory = category.Trim();
        using var connection = OpenConnection(sourceDatabasePath, SqliteOpenMode.ReadOnly);

        var works = ReadWorks(connection, normalizedCategory);
        ApplyAssignments(connection, works);
        var creators = BuildCreatorAggregates(works);
        var tracking = ReadCreatorTrackingData(connection);

        var purchase = AnalyzePurchase(creators, tracking);
        var rating = AnalyzeRating(works, creators);
        var result = new UserMetricsRegressionResultDto(
            normalizedCategory,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            purchase,
            rating);

        using var resultConnection = OpenConnection(resultDatabasePath, SqliteOpenMode.ReadWriteCreate);
        EnsureResultTable(resultConnection);
        using var command = resultConnection.CreateCommand();
        command.CommandText = """
            INSERT INTO user_metrics_regression_results (category, generated_at, payload_json)
            VALUES ($category, $generatedAt, $payload)
            ON CONFLICT(category) DO UPDATE SET
                generated_at = excluded.generated_at,
                payload_json = excluded.payload_json;
            """;
        command.Parameters.AddWithValue("$category", normalizedCategory);
        command.Parameters.AddWithValue("$generatedAt", result.GeneratedAt);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(result, JsonOptions));
        command.ExecuteNonQuery();
        return result;
    }

    private UserMetricsRegressionModelDto AnalyzePurchase(
        IReadOnlyDictionary<string, CreatorAggregate> creators,
        IReadOnlyDictionary<string, CreatorTrackingRegressionData> tracking)
    {
        var metadata = new Dictionary<string, FeatureMetadata>(StringComparer.Ordinal);
        var observations = new List<RegressionObservation>();
        var today = DateTime.Today;
        foreach (var aggregate in creators.Values.OrderBy(item => item.Creator, StringComparer.OrdinalIgnoreCase))
        {
            var trackingData = tracking.GetValueOrDefault(aggregate.Creator) ?? new CreatorTrackingRegressionData();
            var conversionCount = CountPurchaseConversions(trackingData);
            var observation = new RegressionObservation(
                aggregate.Creator,
                conversionCount > 0 ? 1 : 0,
                conversionCount > 0 ? conversionCount : 1);
            var workCount = aggregate.Works.Count;
            var ratedCount = aggregate.Works.Count(work => work.Rating > 0);
            var totalImages = aggregate.Works.Sum(work => (long)work.ImageCount);
            var totalSize = aggregate.Works.Sum(work => work.TotalSize);
            var latestWorkDate = aggregate.Works.Count > 0
                ? aggregate.Works.Max(work => work.LastWriteTime.Date)
                : today;
            AddNumeric(observation, metadata, "files", "作品数", "作品構成", Math.Log(1 + workCount));
            AddNumeric(observation, metadata, "images", "画像枚数", "作品構成", Math.Log(1 + totalImages));
            AddNumeric(observation, metadata, "archive-size", "書庫の総サイズ", "作品構成", Math.Log(1 + Math.Max(0, totalSize) / 1_000_000d));
            AddNumeric(observation, metadata, "average-images", "1作品あたり画像枚数", "作品構成", workCount > 0 ? totalImages / (double)workCount : 0);
            AddNumeric(observation, metadata, "average-archive-size", "1作品あたり書庫サイズ", "作品構成", workCount > 0 ? totalSize / (double)workCount / 1_000_000d : 0);
            AddNumeric(observation, metadata, "latest-work-age", "最新作品からの経過月数", "更新状況", Math.Log(1 + Math.Max(0, (today - latestWorkDate).TotalDays / 30.4375)));
            AddNumeric(observation, metadata, "recent-work-ratio", "直近90日の作品率", "更新状況", workCount > 0 ? aggregate.Works.Count(work => (today - work.LastWriteTime.Date).TotalDays <= 90) / (double)workCount : 0);
            AddNumeric(observation, metadata, "average-rating", "作品の平均評価", "評価", workCount > 0 ? aggregate.Works.Sum(work => work.Rating) / (double)workCount : 0);
            AddNumeric(observation, metadata, "rated-ratio", "評価済み作品率", "評価", workCount > 0 ? ratedCount / (double)workCount : 0);
            for (var rating = 1; rating <= 5; rating++)
            {
                AddNumeric(observation, metadata, $"rating-{rating}-ratio", $"評価{rating}作品率", "評価", workCount > 0 ? aggregate.Works.Count(work => work.Rating == rating) / (double)workCount : 0);
            }
            AddNumeric(observation, metadata, "title-count", "Title数", "作品構成", aggregate.TitleCounts.Count);
            AddNumeric(observation, metadata, "character-count", "Character数", "作品構成", aggregate.CharacterCounts.Count);
            AddNumeric(observation, metadata, "tag-count", "Tag数", "作品構成", aggregate.TagCounts.Count);
            AddNumeric(observation, metadata, "titles-per-work", "1作品あたりTitle数", "作品構成", workCount > 0 ? aggregate.TitleCounts.Count / (double)workCount : 0);
            AddNumeric(observation, metadata, "characters-per-work", "1作品あたりCharacter数", "作品構成", workCount > 0 ? aggregate.CharacterCounts.Count / (double)workCount : 0);
            AddNumeric(observation, metadata, "tags-per-work", "1作品あたりTag数", "作品構成", workCount > 0 ? aggregate.TagCounts.Count / (double)workCount : 0);
            AddNumeric(observation, metadata, "core-title-count", "Core title数", "作品構成", aggregate.CoreTitles.Count);
            AddNumeric(observation, metadata, "core-tag-count", "Core tag数", "作品構成", aggregate.CoreTags.Count);
            AddNumeric(observation, metadata, "tracking-days", "フォローしている日数", "Creator Tracking", trackingData.TrackingDays);

            foreach (var metric in metricSettings)
            {
                AddNumeric(
                    observation,
                    metadata,
                    $"metric:{metric.Key}",
                    metric.Label,
                    "作品の傾向",
                    trackingData.EvaluationMetrics.GetValueOrDefault(metric.Key));
            }
            foreach (var title in aggregate.CoreTitles)
            {
                AddBinary(observation, metadata, $"core-title:{title}", title, "Core title");
            }
            foreach (var tag in aggregate.CoreTags)
            {
                AddBinary(observation, metadata, $"core-tag:{tag}", tag, "Core tag");
            }
            foreach (var site in trackingData.ActivityLinks.Select(link => link.Label).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                AddBinary(observation, metadata, $"site:{site.Trim()}", site.Trim(), "活動サイト");
            }
            AddCategorical(observation, metadata, "follow-policy", trackingData.TrackingStatus, "フォロー方針");
            AddCategorical(observation, metadata, "activity-status", trackingData.ActivityStatus, "活動状況");
            AddCategorical(observation, metadata, "follow-status", trackingData.FollowUpStatus, "フォロー状態");
            observations.Add(observation);
        }

        return FitModel(
            "purchase",
            "購入への寄与",
            observations,
            metadata,
            minimumEntities: 30,
            minimumPositive: 10,
            minimumNegative: 10,
            [
                "サブスクの請求月数とWishlistではない購入履歴件数をCVとして重み付けしています。",
                "購入金額は目的変数から直接決まるため、データ漏洩を避けて説明変数から除外しています。",
                "結果は因果関係ではなく、現在の登録データ内での関連の強さを示します。"
            ]);
    }

    private UserMetricsRegressionModelDto AnalyzeRating(
        IReadOnlyList<WorkRow> works,
        IReadOnlyDictionary<string, CreatorAggregate> creators)
    {
        var metadata = new Dictionary<string, FeatureMetadata>(StringComparer.Ordinal);
        var observations = new List<RegressionObservation>(works.Count);
        var today = DateTime.Today;
        foreach (var work in works)
        {
            var observation = new RegressionObservation(
                work.Gid,
                work.Rating > 0 ? 1 : 0,
                work.Rating > 0 ? work.Rating : 1);
            AddNumeric(observation, metadata, "images", "画像枚数", "ファイル", Math.Log(1 + work.ImageCount));
            AddNumeric(observation, metadata, "total-size", "書庫サイズ", "ファイル", Math.Log(1 + Math.Max(0, work.TotalSize) / 1_000_000d));
            AddNumeric(observation, metadata, "average-image-size", "平均画像サイズ", "ファイル",
                work.ImageCount > 0 ? Math.Log(1 + Math.Max(0, work.TotalSize) / (double)work.ImageCount / 1_000_000d) : 0);
            AddNumeric(observation, metadata, "age-months", "登録からの経過月数", "ファイル",
                Math.Log(1 + Math.Max(0, (today - work.LastWriteTime.Date).TotalDays / 30.4375)));
            AddNumeric(observation, metadata, "title-count", "付与Title数", "属性数", work.Titles.Count);
            AddNumeric(observation, metadata, "character-count", "付与Character数", "属性数", work.Characters.Count);
            AddNumeric(observation, metadata, "tag-count", "付与Tag数", "属性数", work.Tags.Count);
            AddCategorical(observation, metadata, "creator", work.Creator, "Creator");
            AddCategorical(observation, metadata, "media", work.MediaType, "ファイル形式");
            AddCategorical(observation, metadata, "extension", work.Extension, "拡張子");
            foreach (var title in work.Titles)
            {
                AddBinary(observation, metadata, $"title:{title}", title, "Title");
            }
            foreach (var character in work.Characters)
            {
                AddBinary(observation, metadata, $"character:{character}", character, "Character");
            }
            foreach (var tag in work.Tags)
            {
                AddBinary(observation, metadata, $"tag:{tag}", tag, "Tag");
            }
            if (creators.TryGetValue(work.Creator, out var creator))
            {
                if (work.Titles.Any(creator.CoreTitles.Contains))
                {
                    AddBinary(observation, metadata, "is-core-title", "Core title作品", "Core属性");
                }
                if (work.Tags.Any(creator.CoreTags.Contains))
                {
                    AddBinary(observation, metadata, "is-core-tag", "Core tag作品", "Core属性");
                }
            }
            observations.Add(observation);
        }

        return FitModel(
            "rating",
            "評価値加算への寄与",
            observations,
            metadata,
            minimumEntities: 100,
            minimumPositive: 30,
            minimumNegative: 30,
            [
                "評価0の作品を非CV、評価1～5の作品を評価点数分のCVとして重み付けしています。",
                "出現作品が少なすぎるCreator・Title・Character・Tagは自動的に除外します。",
                "結果は因果関係ではなく、現在の登録データ内での関連の強さを示します。"
            ]);
    }

    private static UserMetricsRegressionModelDto FitModel(
        string kind,
        string label,
        IReadOnlyList<RegressionObservation> observations,
        IReadOnlyDictionary<string, FeatureMetadata> metadata,
        int minimumEntities,
        int minimumPositive,
        int minimumNegative,
        IReadOnlyList<string> notes)
    {
        var positive = observations.Where(item => item.Outcome > 0.5).Sum(item => item.Weight);
        var negative = observations.Where(item => item.Outcome <= 0.5).Sum(item => item.Weight);
        var candidates = metadata.Values
            .Where(feature => IsEligibleFeature(feature, observations.Count))
            .GroupBy(feature => feature.Group, StringComparer.Ordinal)
            .SelectMany(group => group
                .OrderByDescending(feature => feature.IsBinary ? feature.SupportCount : observations.Count)
                .ThenBy(feature => feature.Label, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaximumFeaturesPerGroup))
            .OrderBy(feature => feature.IsBinary ? 1 : 0)
            .ThenByDescending(feature => feature.SupportCount)
            .Take(MaximumFeatureCount)
            .ToArray();

        var shortages = new List<string>();
        if (observations.Count < minimumEntities) shortages.Add($"対象数 {observations.Count}/{minimumEntities}");
        if (positive < minimumPositive) shortages.Add($"CV数 {positive:0}/{minimumPositive}");
        if (negative < minimumNegative) shortages.Add($"非CV数 {negative:0}/{minimumNegative}");
        if (candidates.Length == 0) shortages.Add("採用可能な説明変数 0");
        if (shortages.Count > 0)
        {
            return new UserMetricsRegressionModelDto(
                kind,
                label,
                "insufficientData",
                $"分析に必要なデータが不足しています（{string.Join("、", shortages)}）。",
                observations.Count,
                positive,
                negative,
                metadata.Count,
                candidates.Length,
                0,
                0,
                null,
                null,
                null,
                null,
                null,
                0,
                [],
                notes);
        }

        // An inference-oriented model needs enough independent entities per feature. The
        // cap and VIF pass keep sparse category dummies from consuming all degrees of freedom.
        var inferenceFeatureLimit = Math.Max(1, observations.Count / 5);
        var cappedCandidates = candidates.Take(inferenceFeatureLimit).ToArray();
        var (eligible, maximumVif, removedCollinearFeatureCount) =
            ReduceMulticollinearity(cappedCandidates, observations);

        var featureIndex = eligible.Select((feature, index) => (feature.Key, index))
            .ToDictionary(item => item.Key, item => item.index, StringComparer.Ordinal);
        var scales = eligible.Select(feature => CalculateFeatureScale(feature, observations)).ToArray();
        var sparse = observations.Select(observation =>
        {
            var values = observation.Values
                .Where(value => featureIndex.ContainsKey(value.Key) && Math.Abs(value.Value) > 1e-12)
                .Select(value => (Index: featureIndex[value.Key], Value: value.Value / scales[featureIndex[value.Key]]))
                .OrderBy(value => value.Index)
                .ToArray();
            return new SparseObservation(
                observation.Outcome,
                observation.Weight,
                values.Select(value => value.Index).ToArray(),
                values.Select(value => value.Value).ToArray());
        }).ToArray();

        var coefficients = TrainLogisticRegression(sparse, eligible.Length);
        var predictions = sparse.Select(row => Predict(row, coefficients)).ToArray();
        var auc = CalculateWeightedAuc(sparse, predictions);
        var pseudoR2 = CalculatePseudoRSquared(sparse, predictions);
        var diagnostics = CalculateModelDiagnostics(sparse, predictions, coefficients);
        var factors = eligible.Select((feature, index) =>
            {
                var coefficient = coefficients[index + 1];
                return new UserMetricsRegressionFactorDto(
                    feature.Key,
                    feature.Label,
                    feature.Group,
                    Math.Round(coefficient, 4),
                    Math.Round(Math.Exp(Math.Clamp(coefficient, -20, 20)), 3),
                    double.IsFinite(diagnostics.StandardErrors[index + 1])
                        ? Math.Round(diagnostics.StandardErrors[index + 1], 4)
                        : null,
                    double.IsFinite(diagnostics.PValues[index + 1])
                        ? Math.Round(diagnostics.PValues[index + 1], 6)
                        : null,
                    feature.SupportCount,
                    coefficient >= 0 ? "positive" : "negative");
            })
            .OrderByDescending(factor => Math.Abs(factor.Coefficient))
            .ToArray();

        return new UserMetricsRegressionModelDto(
            kind,
            label,
            "completed",
            "分析が完了しました。",
            observations.Count,
            positive,
            negative,
            metadata.Count,
            eligible.Length,
            Math.Round(auc, 3),
            Math.Round(pseudoR2, 3),
            Math.Round(diagnostics.AkaikeInformationCriterion, 2),
            Math.Round(diagnostics.NullAkaikeInformationCriterion, 2),
            Math.Round(diagnostics.AkaikeInformationCriterion - diagnostics.NullAkaikeInformationCriterion, 2),
            Math.Round(diagnostics.EffectiveParameterCount, 2),
            Math.Round(maximumVif, 2),
            removedCollinearFeatureCount,
            factors,
            [
                ..notes,
                "連続値のオッズ比は1標準偏差増加あたり、0/1項目は非該当から該当への変化を示します。",
                "p値はL2正則化モデルの近似Wald検定（多重検定補正なし）です。探索的な目安として扱ってください。",
                "AICは正則化の有効パラメータ数を使った参考値です。ΔAICはNullモデルとの差で、負の値ほど適合が改善しています。",
                "多重共線性はVIF 10を上限として変数を除外し、残る影響をL2正則化で安定化しています。"
            ]);
    }

    private static (FeatureMetadata[] Features, double MaximumVif, int RemovedCount) ReduceMulticollinearity(
        IReadOnlyList<FeatureMetadata> candidates,
        IReadOnlyList<RegressionObservation> observations)
    {
        var selected = candidates.ToList();
        var removed = 0;
        while (selected.Count > 1)
        {
            if (!TryCalculateVarianceInflationFactors(selected, observations, out var vifs))
            {
                var removeIndex = FindMostCorrelatedFeature(selected, observations);
                selected.RemoveAt(removeIndex);
                removed++;
                continue;
            }

            var maximum = vifs.Max();
            if (maximum <= MaximumVarianceInflationFactor)
            {
                return (selected.ToArray(), Math.Max(1, maximum), removed);
            }
            selected.RemoveAt(Array.IndexOf(vifs, maximum));
            removed++;
        }
        return (selected.ToArray(), 1, removed);
    }

    private static bool TryCalculateVarianceInflationFactors(
        IReadOnlyList<FeatureMetadata> features,
        IReadOnlyList<RegressionObservation> observations,
        out double[] vifs)
    {
        var count = features.Count;
        var totalWeight = Math.Max(1, observations.Sum(item => item.Weight));
        var means = new double[count];
        foreach (var observation in observations)
        {
            for (var featureIndex = 0; featureIndex < count; featureIndex++)
            {
                means[featureIndex] += observation.Weight * observation.Values.GetValueOrDefault(features[featureIndex].Key);
            }
        }
        for (var featureIndex = 0; featureIndex < count; featureIndex++) means[featureIndex] /= totalWeight;

        var covariance = new double[count, count];
        foreach (var observation in observations)
        {
            for (var left = 0; left < count; left++)
            {
                var leftValue = observation.Values.GetValueOrDefault(features[left].Key) - means[left];
                for (var right = left; right < count; right++)
                {
                    covariance[left, right] += observation.Weight * leftValue *
                        (observation.Values.GetValueOrDefault(features[right].Key) - means[right]);
                }
            }
        }

        var correlation = new double[count, count];
        for (var left = 0; left < count; left++)
        {
            var leftVariance = covariance[left, left];
            if (leftVariance <= 1e-12)
            {
                vifs = [];
                return false;
            }
            correlation[left, left] = 1;
            for (var right = left + 1; right < count; right++)
            {
                var rightVariance = covariance[right, right];
                if (rightVariance <= 1e-12)
                {
                    vifs = [];
                    return false;
                }
                var value = covariance[left, right] / Math.Sqrt(leftVariance * rightVariance);
                correlation[left, right] = correlation[right, left] = Math.Clamp(value, -1, 1);
            }
        }

        if (!TryInvertMatrix(correlation, out var inverse))
        {
            vifs = [];
            return false;
        }
        vifs = Enumerable.Range(0, count).Select(index => Math.Max(1, inverse[index, index])).ToArray();
        return vifs.All(double.IsFinite);
    }

    private static int FindMostCorrelatedFeature(
        IReadOnlyList<FeatureMetadata> features,
        IReadOnlyList<RegressionObservation> observations)
    {
        var removeIndex = features.Count - 1;
        var largestCorrelation = 0d;
        for (var left = 0; left < features.Count; left++)
        {
            for (var right = left + 1; right < features.Count; right++)
            {
                var correlation = Math.Abs(CalculateWeightedCorrelation(
                    features[left].Key,
                    features[right].Key,
                    observations));
                if (correlation <= largestCorrelation) continue;
                largestCorrelation = correlation;
                removeIndex = features[left].SupportCount < features[right].SupportCount ? left : right;
            }
        }
        return removeIndex;
    }

    private static double CalculateWeightedCorrelation(
        string leftKey,
        string rightKey,
        IReadOnlyList<RegressionObservation> observations)
    {
        var totalWeight = Math.Max(1, observations.Sum(item => item.Weight));
        var leftMean = observations.Sum(item => item.Weight * item.Values.GetValueOrDefault(leftKey)) / totalWeight;
        var rightMean = observations.Sum(item => item.Weight * item.Values.GetValueOrDefault(rightKey)) / totalWeight;
        var covariance = 0d;
        var leftVariance = 0d;
        var rightVariance = 0d;
        foreach (var observation in observations)
        {
            var left = observation.Values.GetValueOrDefault(leftKey) - leftMean;
            var right = observation.Values.GetValueOrDefault(rightKey) - rightMean;
            covariance += observation.Weight * left * right;
            leftVariance += observation.Weight * left * left;
            rightVariance += observation.Weight * right * right;
        }
        return leftVariance <= 1e-12 || rightVariance <= 1e-12
            ? 1
            : covariance / Math.Sqrt(leftVariance * rightVariance);
    }

    private static bool IsEligibleFeature(FeatureMetadata feature, int observationCount)
    {
        if (feature.IsBinary)
        {
            var minimumSupport = Math.Max(5, (int)Math.Ceiling(observationCount * 0.01));
            return feature.SupportCount >= minimumSupport && observationCount - feature.SupportCount >= minimumSupport;
        }
        var mean = feature.Sum / Math.Max(1, feature.SupportCount);
        var variance = feature.SumSquares / Math.Max(1, feature.SupportCount) - mean * mean;
        return feature.SupportCount > 1 && variance > 1e-8;
    }

    private static double CalculateFeatureScale(FeatureMetadata feature, IReadOnlyList<RegressionObservation> observations)
    {
        if (feature.IsBinary) return 1;
        var values = observations.Select(item => item.Values.GetValueOrDefault(feature.Key)).ToArray();
        var mean = values.Average();
        var variance = values.Sum(value => (value - mean) * (value - mean)) / Math.Max(1, values.Length - 1);
        return Math.Max(1e-6, Math.Sqrt(variance));
    }

    private static double[] TrainLogisticRegression(IReadOnlyList<SparseObservation> observations, int featureCount)
    {
        var parameters = new double[featureCount + 1];
        var firstMoment = new double[parameters.Length];
        var secondMoment = new double[parameters.Length];
        var gradient = new double[parameters.Length];
        var totalWeight = Math.Max(1, observations.Sum(item => item.Weight));
        const double learningRate = 0.035;
        const double beta1 = 0.9;
        const double beta2 = 0.999;
        const double epsilon = 1e-8;

        for (var iteration = 1; iteration <= 500; iteration++)
        {
            Array.Clear(gradient);
            foreach (var row in observations)
            {
                var error = (Predict(row, parameters) - row.Outcome) * row.Weight;
                gradient[0] += error;
                for (var valueIndex = 0; valueIndex < row.Indices.Length; valueIndex++)
                {
                    gradient[row.Indices[valueIndex] + 1] += error * row.Values[valueIndex];
                }
            }

            var largestUpdate = 0d;
            for (var parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
            {
                var value = gradient[parameterIndex] / totalWeight;
                if (parameterIndex > 0)
                {
                    value += RegularizationStrength * parameters[parameterIndex];
                }
                firstMoment[parameterIndex] = beta1 * firstMoment[parameterIndex] + (1 - beta1) * value;
                secondMoment[parameterIndex] = beta2 * secondMoment[parameterIndex] + (1 - beta2) * value * value;
                var correctedFirst = firstMoment[parameterIndex] / (1 - Math.Pow(beta1, iteration));
                var correctedSecond = secondMoment[parameterIndex] / (1 - Math.Pow(beta2, iteration));
                var update = learningRate * correctedFirst / (Math.Sqrt(correctedSecond) + epsilon);
                parameters[parameterIndex] -= update;
                largestUpdate = Math.Max(largestUpdate, Math.Abs(update));
            }
            if (iteration > 80 && largestUpdate < 1e-6)
            {
                break;
            }
        }
        return parameters;
    }

    private static double Predict(SparseObservation observation, IReadOnlyList<double> parameters)
    {
        var linear = parameters[0];
        for (var index = 0; index < observation.Indices.Length; index++)
        {
            linear += parameters[observation.Indices[index] + 1] * observation.Values[index];
        }
        linear = Math.Clamp(linear, -30, 30);
        return 1 / (1 + Math.Exp(-linear));
    }

    private static double CalculateWeightedAuc(IReadOnlyList<SparseObservation> observations, IReadOnlyList<double> predictions)
    {
        var ranked = observations.Select((item, index) => (item, score: predictions[index]))
            .OrderBy(item => item.score)
            .ToArray();
        var totalPositive = ranked.Where(item => item.item.Outcome > 0.5).Sum(item => item.item.Weight);
        var totalNegative = ranked.Where(item => item.item.Outcome <= 0.5).Sum(item => item.item.Weight);
        if (totalPositive <= 0 || totalNegative <= 0) return 0;
        var cumulativeNegative = 0d;
        var concordant = 0d;
        for (var start = 0; start < ranked.Length;)
        {
            var end = start + 1;
            while (end < ranked.Length && Math.Abs(ranked[end].score - ranked[start].score) < 1e-12) end++;
            var groupPositive = 0d;
            var groupNegative = 0d;
            for (var index = start; index < end; index++)
            {
                if (ranked[index].item.Outcome > 0.5) groupPositive += ranked[index].item.Weight;
                else groupNegative += ranked[index].item.Weight;
            }
            concordant += groupPositive * (cumulativeNegative + groupNegative / 2);
            cumulativeNegative += groupNegative;
            start = end;
        }
        return concordant / (totalPositive * totalNegative);
    }

    private static double CalculatePseudoRSquared(IReadOnlyList<SparseObservation> observations, IReadOnlyList<double> predictions)
    {
        var totalWeight = observations.Sum(item => item.Weight);
        var positiveRate = observations.Sum(item => item.Outcome * item.Weight) / Math.Max(1, totalWeight);
        positiveRate = Math.Clamp(positiveRate, 1e-9, 1 - 1e-9);
        var modelLogLikelihood = 0d;
        var nullLogLikelihood = 0d;
        for (var index = 0; index < observations.Count; index++)
        {
            var row = observations[index];
            var probability = Math.Clamp(predictions[index], 1e-9, 1 - 1e-9);
            modelLogLikelihood += row.Weight * (row.Outcome * Math.Log(probability) + (1 - row.Outcome) * Math.Log(1 - probability));
            nullLogLikelihood += row.Weight * (row.Outcome * Math.Log(positiveRate) + (1 - row.Outcome) * Math.Log(1 - positiveRate));
        }
        return nullLogLikelihood >= -1e-9 ? 0 : Math.Clamp(1 - modelLogLikelihood / nullLogLikelihood, 0, 1);
    }

    private static ModelDiagnostics CalculateModelDiagnostics(
        IReadOnlyList<SparseObservation> observations,
        IReadOnlyList<double> predictions,
        IReadOnlyList<double> coefficients)
    {
        var parameterCount = coefficients.Count;
        var dataInformation = new double[parameterCount, parameterCount];
        var totalWeight = Math.Max(1, observations.Sum(item => item.Weight));
        var modelLogLikelihood = 0d;
        var positiveRate = observations.Sum(item => item.Outcome * item.Weight) / totalWeight;
        positiveRate = Math.Clamp(positiveRate, 1e-9, 1 - 1e-9);
        var nullLogLikelihood = 0d;

        for (var rowIndex = 0; rowIndex < observations.Count; rowIndex++)
        {
            var row = observations[rowIndex];
            var probability = Math.Clamp(predictions[rowIndex], 1e-9, 1 - 1e-9);
            modelLogLikelihood += row.Weight *
                (row.Outcome * Math.Log(probability) + (1 - row.Outcome) * Math.Log(1 - probability));
            nullLogLikelihood += row.Weight *
                (row.Outcome * Math.Log(positiveRate) + (1 - row.Outcome) * Math.Log(1 - positiveRate));

            var curvatureWeight = row.Weight * probability * (1 - probability);
            var indices = new int[row.Indices.Length + 1];
            var values = new double[row.Values.Length + 1];
            indices[0] = 0;
            values[0] = 1;
            for (var valueIndex = 0; valueIndex < row.Indices.Length; valueIndex++)
            {
                indices[valueIndex + 1] = row.Indices[valueIndex] + 1;
                values[valueIndex + 1] = row.Values[valueIndex];
            }
            for (var left = 0; left < indices.Length; left++)
            {
                for (var right = left; right < indices.Length; right++)
                {
                    dataInformation[indices[left], indices[right]] +=
                        curvatureWeight * values[left] * values[right];
                }
            }
        }
        MirrorUpperTriangle(dataInformation);

        var penalizedInformation = (double[,])dataInformation.Clone();
        for (var index = 1; index < parameterCount; index++)
        {
            penalizedInformation[index, index] += RegularizationStrength * totalWeight;
        }

        if (!TryInvertMatrix(penalizedInformation, out var inversePenalizedInformation))
        {
            return new ModelDiagnostics(
                Enumerable.Repeat(double.NaN, parameterCount).ToArray(),
                Enumerable.Repeat(double.NaN, parameterCount).ToArray(),
                -2 * modelLogLikelihood + 2 * parameterCount,
                -2 * nullLogLikelihood + 2,
                parameterCount);
        }

        var covariance = MultiplyMatrices(
            MultiplyMatrices(inversePenalizedInformation, dataInformation),
            inversePenalizedInformation);
        var standardErrors = new double[parameterCount];
        var pValues = new double[parameterCount];
        for (var index = 0; index < parameterCount; index++)
        {
            standardErrors[index] = covariance[index, index] > 1e-14
                ? Math.Sqrt(covariance[index, index])
                : double.NaN;
            pValues[index] = double.IsFinite(standardErrors[index])
                ? TwoSidedNormalPValue(coefficients[index] / standardErrors[index])
                : double.NaN;
        }

        var effectiveParameterCount = TraceProduct(inversePenalizedInformation, dataInformation);
        return new ModelDiagnostics(
            standardErrors,
            pValues,
            -2 * modelLogLikelihood + 2 * effectiveParameterCount,
            -2 * nullLogLikelihood + 2,
            effectiveParameterCount);
    }

    private static void MirrorUpperTriangle(double[,] matrix)
    {
        for (var row = 0; row < matrix.GetLength(0); row++)
        {
            for (var column = row + 1; column < matrix.GetLength(1); column++)
            {
                matrix[column, row] = matrix[row, column];
            }
        }
    }

    private static bool TryInvertMatrix(double[,] source, out double[,] inverse)
    {
        var size = source.GetLength(0);
        inverse = new double[size, size];
        if (size != source.GetLength(1)) return false;
        var augmented = new double[size, size * 2];
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++) augmented[row, column] = source[row, column];
            augmented[row, size + row] = 1;
        }

        for (var pivot = 0; pivot < size; pivot++)
        {
            var pivotRow = pivot;
            var pivotMagnitude = Math.Abs(augmented[pivot, pivot]);
            for (var row = pivot + 1; row < size; row++)
            {
                var candidate = Math.Abs(augmented[row, pivot]);
                if (candidate <= pivotMagnitude) continue;
                pivotMagnitude = candidate;
                pivotRow = row;
            }
            if (pivotMagnitude < 1e-10 || !double.IsFinite(pivotMagnitude)) return false;
            if (pivotRow != pivot)
            {
                for (var column = 0; column < size * 2; column++)
                {
                    (augmented[pivot, column], augmented[pivotRow, column]) =
                        (augmented[pivotRow, column], augmented[pivot, column]);
                }
            }

            var divisor = augmented[pivot, pivot];
            for (var column = 0; column < size * 2; column++) augmented[pivot, column] /= divisor;
            for (var row = 0; row < size; row++)
            {
                if (row == pivot) continue;
                var factor = augmented[row, pivot];
                if (Math.Abs(factor) < 1e-18) continue;
                for (var column = 0; column < size * 2; column++)
                {
                    augmented[row, column] -= factor * augmented[pivot, column];
                }
            }
        }

        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                inverse[row, column] = augmented[row, size + column];
                if (!double.IsFinite(inverse[row, column])) return false;
            }
        }
        return true;
    }

    private static double[,] MultiplyMatrices(double[,] left, double[,] right)
    {
        var rows = left.GetLength(0);
        var shared = left.GetLength(1);
        var columns = right.GetLength(1);
        var result = new double[rows, columns];
        for (var row = 0; row < rows; row++)
        {
            for (var inner = 0; inner < shared; inner++)
            {
                var value = left[row, inner];
                if (Math.Abs(value) < 1e-18) continue;
                for (var column = 0; column < columns; column++)
                {
                    result[row, column] += value * right[inner, column];
                }
            }
        }
        return result;
    }

    private static double TraceProduct(double[,] left, double[,] right)
    {
        var size = left.GetLength(0);
        var trace = 0d;
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                trace += left[row, column] * right[column, row];
            }
        }
        return Math.Clamp(trace, 1, size);
    }

    private static double TwoSidedNormalPValue(double zScore)
    {
        var absolute = Math.Abs(zScore);
        // Abramowitz-Stegun approximation of the upper standard-normal tail.
        var t = 1 / (1 + 0.2316419 * absolute);
        var density = Math.Exp(-0.5 * absolute * absolute) / Math.Sqrt(2 * Math.PI);
        var upperTail = density * t *
            (0.319381530 + t * (-0.356563782 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
        return Math.Clamp(2 * upperTail, 0, 1);
    }

    private static int CountPurchaseConversions(CreatorTrackingRegressionData data)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var count = data.Purchases.Count(purchase =>
            !purchase.Wishlist && purchase.Amount > 0 && TryParseDate(purchase.PurchasedOn, out var date) && date <= today);
        foreach (var subscription in data.Subscriptions)
        {
            count += CreatorTrackingSubscriptionSchedule.ListChargeDates(subscription, today).Count;
        }
        return count;
    }

    private IReadOnlyDictionary<string, CreatorTrackingRegressionData> ReadCreatorTrackingData(SqliteConnection connection)
    {
        var result = new Dictionary<string, CreatorTrackingRegressionData>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT creator, tracking_status, activity_status, follow_up_status,
                   last_checked_on, last_activity_on, activity_links_json,
                   evaluation_metrics_json, subscription_history_json, purchase_history_json
            FROM creator_tracking;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var creator = reader.GetString(0).Trim();
            if (string.IsNullOrWhiteSpace(creator)) continue;
            var metrics = Deserialize<Dictionary<string, int>>(reader.GetString(7)) ?? new Dictionary<string, int>();
            result[creator] = new CreatorTrackingRegressionData
            {
                TrackingStatus = reader.GetString(1).Trim(),
                ActivityStatus = reader.GetString(2).Trim(),
                FollowUpStatus = reader.GetString(3).Trim(),
                TrackingDays = TryParseDate(reader.GetString(4), out var started) && TryParseDate(reader.GetString(5), out var checkedOn)
                    ? Math.Max(0, checkedOn.DayNumber - started.DayNumber)
                    : 0,
                ActivityLinks = Deserialize<CreatorTrackingActivityLinkDto[]>(reader.GetString(6)) ?? [],
                EvaluationMetrics = metrics,
                Subscriptions = Deserialize<CreatorTrackingSubscriptionDto[]>(reader.GetString(8)) ?? [],
                Purchases = Deserialize<CreatorTrackingPurchaseDto[]>(reader.GetString(9)) ?? []
            };
        }
        return result;
    }

    private static IReadOnlyDictionary<string, CreatorAggregate> BuildCreatorAggregates(IReadOnlyList<WorkRow> works)
    {
        var result = new Dictionary<string, CreatorAggregate>(StringComparer.OrdinalIgnoreCase);
        foreach (var work in works)
        {
            if (string.IsNullOrWhiteSpace(work.Creator)) continue;
            if (!result.TryGetValue(work.Creator, out var aggregate))
            {
                aggregate = new CreatorAggregate(work.Creator);
                result[work.Creator] = aggregate;
            }
            aggregate.Works.Add(work);
            IncrementCounts(aggregate.TitleCounts, work.Titles);
            IncrementCounts(aggregate.CharacterCounts, work.Characters);
            IncrementCounts(aggregate.TagCounts, work.Tags);
        }
        foreach (var aggregate in result.Values)
        {
            var threshold = aggregate.Works.Count * 0.3;
            foreach (var value in aggregate.TitleCounts.Where(item => item.Value >= threshold).Select(item => item.Key)) aggregate.CoreTitles.Add(value);
            foreach (var value in aggregate.TagCounts.Where(item => item.Value >= threshold).Select(item => item.Key)) aggregate.CoreTags.Add(value);
        }
        return result;
    }

    private static void IncrementCounts(IDictionary<string, int> counts, IEnumerable<string> values)
    {
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            counts[value] = counts.TryGetValue(value, out var count) ? count + 1 : 1;
        }
    }

    private static List<WorkRow> ReadWorks(SqliteConnection connection, string category)
    {
        var result = new List<WorkRow>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT gid, COALESCE(TRIM(creator), ''), COALESCE(TRIM(title), ''),
                   COALESCE(TRIM(character), ''), COALESCE(rating, 0),
                   COALESCE(image_count, 0), COALESCE(total_uncompressed_size, 0),
                   COALESCE(media_type, ''), COALESCE(current_path, ''),
                   COALESCE(last_write_time, '')
            FROM items
            WHERE category = $category COLLATE NOCASE AND COALESCE(archived_flg, 0) = 0;
            """;
        command.Parameters.AddWithValue("$category", category);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(8);
            result.Add(new WorkRow
            {
                Gid = reader.GetString(0),
                Creator = reader.GetString(1),
                LegacyTitle = reader.GetString(2),
                LegacyCharacter = reader.GetString(3),
                Rating = Math.Clamp(reader.GetInt32(4), 0, 5),
                ImageCount = Math.Max(0, reader.GetInt32(5)),
                TotalSize = Math.Max(0, reader.GetInt64(6)),
                MediaType = reader.GetString(7),
                Extension = Path.GetExtension(path).Trim().ToLowerInvariant(),
                LastWriteTime = DateTime.TryParse(reader.GetString(9), out var parsed) ? parsed : DateTime.Today
            });
        }
        return result;
    }

    private static void ApplyAssignments(SqliteConnection connection, IReadOnlyList<WorkRow> works)
    {
        var titles = ReadFilterAssignments(connection, "title");
        var characters = ReadFilterAssignments(connection, "character");
        var tags = ReadTagAssignments(connection);
        foreach (var work in works)
        {
            work.Titles = titles.GetValueOrDefault(work.Gid) ?? SplitLegacyValues(work.LegacyTitle);
            work.Characters = characters.GetValueOrDefault(work.Gid) ?? SplitLegacyValues(work.LegacyCharacter);
            work.Tags = tags.GetValueOrDefault(work.Gid) ?? [];
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadFilterAssignments(SqliteConnection connection, string filterType)
    {
        if (!TableExists(connection, "gallery_item_filter_combinations") ||
            !TableExists(connection, "gallery_filter_combinations") ||
            !TableExists(connection, "gallery_filters"))
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        }
        var values = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = filterType == "title"
            ? """
                SELECT map.gid, title.canonical_name
                FROM gallery_item_filter_combinations AS map
                JOIN gallery_filter_combinations AS combination ON combination.combination_id = map.combination_id AND combination.use_flg <> 0
                JOIN gallery_filters AS title ON title.filter_id = combination.title_filter_id;
                """
            : """
                SELECT map.gid, character.canonical_name
                FROM gallery_item_filter_combinations AS map
                JOIN gallery_filter_combinations AS combination ON combination.combination_id = map.combination_id AND combination.use_flg <> 0
                JOIN gallery_filters AS character ON character.filter_id = combination.character_filter_id
                WHERE combination.character_filter_id IS NOT NULL;
                """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            AddAssignment(values, reader.GetString(0), reader.GetString(1));
        }
        return values.ToDictionary(item => item.Key, item => (IReadOnlyList<string>)item.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadTagAssignments(SqliteConnection connection)
    {
        if (!TableExists(connection, "item_tags") || !TableExists(connection, "tags"))
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        }
        var values = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_tag.gid, tag.tag
            FROM item_tags AS item_tag
            JOIN tags AS tag ON tag.tag_id = item_tag.tag_id
            WHERE tag.use_flg <> 0;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            AddAssignment(values, reader.GetString(0), reader.GetString(1));
        }
        return values.ToDictionary(item => item.Key, item => (IReadOnlyList<string>)item.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static void AddAssignment(IDictionary<string, HashSet<string>> values, string gid, string value)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return;
        if (!values.TryGetValue(gid, out var assigned))
        {
            assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            values[gid] = assigned;
        }
        assigned.Add(normalized);
    }

    private static IReadOnlyList<string> SplitLegacyValues(string value) => value
        .Split([',', ';', '/', '／', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static void AddNumeric(
        RegressionObservation observation,
        IDictionary<string, FeatureMetadata> metadata,
        string key,
        string label,
        string group,
        double value)
    {
        if (!double.IsFinite(value)) return;
        AddValue(observation, metadata, key, label, group, value, false);
    }

    private static void AddBinary(
        RegressionObservation observation,
        IDictionary<string, FeatureMetadata> metadata,
        string key,
        string label,
        string group) => AddValue(observation, metadata, key, label, group, 1, true);

    private static void AddCategorical(
        RegressionObservation observation,
        IDictionary<string, FeatureMetadata> metadata,
        string prefix,
        string value,
        string group)
    {
        var normalized = value.Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            AddBinary(observation, metadata, $"{prefix}:{normalized}", normalized, group);
        }
    }

    private static void AddValue(
        RegressionObservation observation,
        IDictionary<string, FeatureMetadata> metadata,
        string key,
        string label,
        string group,
        double value,
        bool isBinary)
    {
        if (!metadata.TryGetValue(key, out var feature))
        {
            feature = new FeatureMetadata(key, label, group, isBinary);
            metadata[key] = feature;
        }
        if (!observation.Values.TryAdd(key, value)) return;
        feature.SupportCount++;
        feature.Sum += value;
        feature.SumSquares += value * value;
    }

    private static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static bool TryParseDate(string value, out DateOnly date)
    {
        return DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ||
               DateOnly.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out date);
    }

    private static SqliteConnection OpenConnection(string databasePath, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ConnectionString);
        connection.Open();
        return connection;
    }

    internal static void EnsureResultTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS user_metrics_regression_results (
                category TEXT PRIMARY KEY COLLATE NOCASE,
                generated_at TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }
}
