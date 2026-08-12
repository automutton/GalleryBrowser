using GalleryBrowser.Models;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GalleryBrowser.Services;

internal sealed record ScheduledNotificationCandidate(string EventKey, string Line);

internal sealed record ScheduledNotificationSection(
    string Heading,
    string Description,
    IReadOnlyList<ScheduledNotificationCandidate> Candidates);

internal sealed record ScheduledNotificationDigest(
    string Message,
    IReadOnlyList<ScheduledNotificationCandidate> Candidates);

internal static class ScheduledNotificationDigestBuilder
{
    public static ScheduledNotificationDigest? Create(
        ScheduledNotificationSnapshot snapshot,
        string occurrenceKey,
        bool notifyCreatorFollowAlert,
        bool notifySubscriptionEnding,
        bool notifySubscriptionReminder,
        bool notifyCreatorTasks,
        bool markdown,
        int maximumMessageLength)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(occurrenceKey);

        var sections = new List<ScheduledNotificationSection>();
        if (notifyCreatorFollowAlert)
        {
            var warningCandidates = snapshot.Creators
                .Where(item => item.FollowWarnFlg && !item.FollowAlertFlg)
                .Select(item => new ScheduledNotificationCandidate(
                    CreateEventKey(
                        "creator-follow-warn",
                        $"{item.Creator}|{item.LastCheckedOn}|{occurrenceKey}"),
                    FormatCreatorLine(item, markdown)))
                .ToArray();
            AddSection(
                sections,
                "🟡 Creator更新確認の警告",
                "最終確認から、設定されたフォロー間隔以上が経過しています。",
                warningCandidates);

            var alertCandidates = snapshot.Creators
                .Where(item => item.FollowAlertFlg)
                .Select(item => new ScheduledNotificationCandidate(
                    CreateEventKey(
                        "creator-follow-alert",
                        $"{item.Creator}|{item.LastCheckedOn}|{occurrenceKey}"),
                    FormatCreatorLine(item, markdown)))
                .ToArray();
            AddSection(
                sections,
                "🔴 Creator更新確認のアラート",
                "最終確認から、設定されたフォロー間隔の1.5倍以上が経過しています。",
                alertCandidates);
        }

        var tomorrow = DateOnly.FromDateTime(snapshot.RefreshedAt.LocalDateTime).AddDays(1);
        var subscriptions = snapshot.Events
            .Where(entry => string.Equals(entry.EventType, "subscription", StringComparison.OrdinalIgnoreCase))
            .Where(entry => TryParseDate(entry.RenewalOn, out var renewalOn) && renewalOn == tomorrow)
            .ToArray();

        if (notifySubscriptionEnding)
        {
            var endingCandidates = subscriptions
                .Where(entry => entry.EndingPlanned)
                .Select(entry => new ScheduledNotificationCandidate(
                    CreateEventKey(
                        "subscription-ending",
                        $"{entry.Id}|{entry.RenewalOn}|{occurrenceKey}"),
                    FormatSubscriptionLine(entry, markdown)))
                .ToArray();
            AddSection(
                sections,
                "⚠️ 明日はサブスクの解除予定日です",
                "終了予定が設定されているサブスクを確認してください。",
                endingCandidates);
        }

        if (notifySubscriptionReminder)
        {
            var reminderCandidates = subscriptions
                .Where(entry => !entry.EndingPlanned)
                .Select(entry => new ScheduledNotificationCandidate(
                    CreateEventKey(
                        "subscription-reminder",
                        $"{entry.Id}|{entry.RenewalOn}|{occurrenceKey}"),
                    FormatSubscriptionLine(entry, markdown)))
                .ToArray();
            AddSection(
                sections,
                "⏰ 明日はサブスクの更新予定日です",
                "更新予定のサブスクを確認してください。",
                reminderCandidates);
        }

        if (notifyCreatorTasks)
        {
            var today = DateOnly.FromDateTime(snapshot.RefreshedAt.LocalDateTime);
            var taskCandidates = snapshot.Events
                .Where(entry => string.Equals(entry.EventType, "task", StringComparison.OrdinalIgnoreCase))
                .Where(entry =>
                    TryParseDate(entry.StartOn, out var startOn) &&
                    TryParseDate(entry.EndOn, out var endOn) &&
                    entry.AlertFrequency switch
                    {
                        "daily" => today >= startOn && today <= endOn,
                        "end" => today == endOn,
                        _ => false
                    })
                .Select(entry => new ScheduledNotificationCandidate(
                    CreateEventKey(
                        "creator-task",
                        $"{entry.Id}|{today:yyyy-MM-dd}|{occurrenceKey}"),
                    FormatTaskLine(entry, markdown)))
                .ToArray();
            AddSection(
                sections,
                "📋 Creator Tracking タスク",
                "本日通知対象のタスクです。",
                taskCandidates);
        }

        if (sections.Count == 0)
        {
            return null;
        }

        var candidates = sections.SelectMany(section => section.Candidates).ToArray();
        var message = BuildMessage(snapshot.RefreshedAt, sections, markdown, maximumMessageLength);
        return new ScheduledNotificationDigest(message, candidates);
    }

    private static void AddSection(
        ICollection<ScheduledNotificationSection> sections,
        string heading,
        string description,
        IReadOnlyList<ScheduledNotificationCandidate> candidates)
    {
        if (candidates.Count > 0)
        {
            sections.Add(new ScheduledNotificationSection(heading, description, candidates));
        }
    }

    private static string BuildMessage(
        DateTimeOffset refreshedAt,
        IReadOnlyList<ScheduledNotificationSection> sections,
        bool markdown,
        int maximumMessageLength)
    {
        var builder = new StringBuilder();
        AppendLine(builder, markdown ? "**📣 GalleryBrowser 定時通知**" : "📣 GalleryBrowser 定時通知");
        AppendLine(
            builder,
            $"最新データ確認: {refreshedAt.LocalDateTime:yyyy-MM-dd HH:mm}");

        var sectionHeaders = sections
            .Select(section =>
                $"{Environment.NewLine}" +
                $"{(markdown ? $"**{section.Heading}**" : section.Heading)}{Environment.NewLine}" +
                $"{section.Description}{Environment.NewLine}")
            .ToArray();
        var fixedLength = builder.Length + sectionHeaders.Sum(header => header.Length);
        var candidateBudgetPerSection = Math.Max(
            28,
            (maximumMessageLength - fixedLength) / Math.Max(1, sections.Count));

        for (var sectionIndex = 0; sectionIndex < sections.Count; sectionIndex++)
        {
            var section = sections[sectionIndex];
            builder.Append(sectionHeaders[sectionIndex]);
            var sectionStart = builder.Length;

            var added = 0;
            foreach (var candidate in section.Candidates)
            {
                var line = candidate.Line.Trim();
                var remainingSectionBudget =
                    candidateBudgetPerSection -
                    (builder.Length - sectionStart) -
                    Environment.NewLine.Length;
                if (remainingSectionBudget <= 24)
                {
                    break;
                }

                var normalizedLine = line.Length <= remainingSectionBudget
                    ? line
                    : line[..Math.Max(1, remainingSectionBudget - 1)] + "…";
                AppendLine(builder, normalizedLine);
                added++;
            }

            var omitted = section.Candidates.Count - added;
            if (omitted > 0)
            {
                var summary = $"・ほか{omitted:N0}件";
                if (builder.Length + summary.Length + Environment.NewLine.Length <= maximumMessageLength)
                {
                    AppendLine(builder, summary);
                }
            }
        }

        var message = builder.ToString().TrimEnd();
        return message.Length <= maximumMessageLength
            ? message
            : message[..(maximumMessageLength - 1)] + "…";
    }

    private static void AppendLine(StringBuilder builder, string value) =>
        builder.AppendLine(value);

    private static string FormatCreatorLine(
        CreatorTrackingIndexItemDto item,
        bool markdown)
    {
        var name = markdown ? $"**{item.DisplayName}**" : item.DisplayName;
        return $"・{name} — 最終確認 {FormatDate(item.LastCheckedOn)}（{item.SinceLastCheckDays:N0}日経過）";
    }

    private static string FormatSubscriptionLine(
        CalendarSubscriptionEventDto entry,
        bool markdown)
    {
        var platform = string.IsNullOrWhiteSpace(entry.Platform) ? "プラットフォーム未設定" : entry.Platform;
        var plan = string.IsNullOrWhiteSpace(entry.Plan) ? string.Empty : $" / {entry.Plan}";
        var amount = entry.Amount > 0
            ? $" — {entry.Amount:N0} {entry.Currency}"
            : string.Empty;
        var name = markdown ? $"**{entry.DisplayName}**" : entry.DisplayName;
        return $"・{name} — {platform}{plan}{amount}";
    }

    private static string FormatTaskLine(
        CalendarSubscriptionEventDto entry,
        bool markdown)
    {
        var name = markdown ? $"**{entry.DisplayName}**" : entry.DisplayName;
        var category = string.IsNullOrWhiteSpace(entry.TaskCategory)
            ? string.Empty
            : $" [{entry.TaskCategory}]";
        var dateRange = entry.StartOn == entry.EndOn
            ? entry.EndOn
            : $"{entry.StartOn}～{entry.EndOn}";
        return $"・{name}{category} — {entry.Title}（{dateRange}）";
    }

    private static string FormatDate(string value) =>
        TryParseDate(value, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "未設定";

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(
            value?.Trim(),
            ["yyyy-MM-dd", "yyyy/M/d", "yyyy/MM/dd"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);

    private static string CreateEventKey(string eventType, string identity)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.Trim())));
        return $"{eventType}:{hash[..24]}";
    }
}
