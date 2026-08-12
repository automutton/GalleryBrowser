namespace GalleryBrowser.Services;

internal static class NotificationTestMessageFactory
{
    public const string CreatorFollowWarning = "creatorFollowWarning";
    public const string CreatorFollowAlert = "creatorFollowAlert";
    public const string SubscriptionEnding = "subscriptionEnding";
    public const string SubscriptionReminder = "subscriptionReminder";
    public const string CreatorTask = "creatorTask";
    public const string ScheduledScanStarted = "scheduledScanStarted";
    public const string ScheduledScanCompleted = "scheduledScanCompleted";

    public static string CreateDiscordMessage(string eventType) =>
        eventType switch
        {
            CreatorFollowWarning =>
                "**🟡 Creator更新確認の警告（テスト配信）**\n" +
                "最終確認から、設定されたフォロー間隔以上が経過しています。\n" +
                "• **Sample Creator** — 最終確認 2026-07-10（14日経過）",
            CreatorFollowAlert =>
                "**🔴 Creator更新確認アラート（テスト配信）**\n" +
                "最終確認から、設定されたフォロー間隔の1.5倍以上が経過しています。\n" +
                "• **Sample Creator** — 最終確認 2026-07-01（23日経過）",
            SubscriptionEnding =>
                "**⚠️ 明日はサブスクの解除予定日です（テスト配信）**\n" +
                "終了予定が設定されているサブスクを確認してください。\n" +
                "• **Sample Creator** — Patreon / Standard Plan — 1,000 JPY",
            SubscriptionReminder =>
                "**⏰ 明日はサブスクの更新予定日です（テスト配信）**\n" +
                "継続中のサブスクを確認してください。\n" +
                "• **Sample Creator** — FANBOX / Support Plan — 500 JPY",
            CreatorTask =>
                "**📋 Creator Tracking タスク（テスト配信）**\n" +
                "設定された通知タイミングになったタスクがあります。\n" +
                "• **Sample Creator** — 制作確認 / 投稿内容を確認 — 2026-07-25",
            ScheduledScanStarted =>
                "**🔄 定期フォルダ走査を開始しました（テスト配信）**\n" +
                "対象: 生成AI作品 / 同人誌\n" +
                "予想所要時間: 12分30秒",
            ScheduledScanCompleted =>
                "**✅ 完了: 定期フォルダ走査（テスト配信）**\n" +
                "対象: 生成AI作品 / 同人誌\n" +
                "経過時間: 11分48秒\n" +
                "作品 128件を確認 / エラー 0件",
            _ => throw CreateUnsupportedEventException(eventType)
        };

    public static string CreateLineMessage(string eventType) =>
        eventType switch
        {
            CreatorFollowWarning =>
                "🟡 Creator更新確認の警告（テスト配信）\n" +
                "最終確認から、設定されたフォロー間隔以上が経過しています。\n" +
                "・Sample Creator — 最終確認 2026-07-10（14日経過）",
            CreatorFollowAlert =>
                "🔴 Creator更新確認アラート（テスト配信）\n" +
                "最終確認から、設定されたフォロー間隔の1.5倍以上が経過しています。\n" +
                "・Sample Creator — 最終確認 2026-07-01（23日経過）",
            SubscriptionEnding =>
                "⚠️ 明日はサブスクの解除予定日です（テスト配信）\n" +
                "終了予定が設定されているサブスクを確認してください。\n" +
                "・Sample Creator — Patreon / Standard Plan — 1,000 JPY",
            SubscriptionReminder =>
                "⏰ 明日はサブスクの更新予定日です（テスト配信）\n" +
                "継続中のサブスクを確認してください。\n" +
                "・Sample Creator — FANBOX / Support Plan — 500 JPY",
            CreatorTask =>
                "📋 Creator Tracking タスク（テスト配信）\n" +
                "設定された通知タイミングになったタスクがあります。\n" +
                "・Sample Creator — 制作確認 / 投稿内容を確認 — 2026-07-25",
            ScheduledScanStarted =>
                "🔄 定期フォルダ走査を開始しました（テスト配信）\n" +
                "対象: 生成AI作品 / 同人誌\n" +
                "予想所要時間: 12分30秒",
            ScheduledScanCompleted =>
                "✅ 完了: 定期フォルダ走査（テスト配信）\n" +
                "対象: 生成AI作品 / 同人誌\n" +
                "経過時間: 11分48秒\n" +
                "作品 128件を確認 / エラー 0件",
            _ => throw CreateUnsupportedEventException(eventType)
        };

    private static ArgumentException CreateUnsupportedEventException(string eventType) =>
        new($"未対応の通知テスト種別です: {eventType}", nameof(eventType));
}
