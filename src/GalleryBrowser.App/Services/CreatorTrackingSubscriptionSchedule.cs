using GalleryBrowser.Models;
using System.Globalization;

namespace GalleryBrowser.Services;

internal static class CreatorTrackingSubscriptionSchedule
{
    private const int MaximumBillingCycles = 1200;

    public static IReadOnlyList<DateOnly> ListChargeDates(
        CreatorTrackingSubscriptionDto subscription,
        DateOnly today)
    {
        if (subscription.Wishlist ||
            subscription.Amount <= 0 ||
            !TryParseDate(subscription.StartedOn, out var startedOn) ||
            startedOn > today)
        {
            return [];
        }

        var boundaryText = subscription.IsEnded
            ? subscription.EndedOn
            : subscription.RenewalOn;
        if (!TryParseDate(boundaryText, out var exclusiveBoundary) ||
            exclusiveBoundary <= startedOn)
        {
            return [];
        }

        var intervalMonths = subscription.BillingFrequency.Trim().ToLowerInvariant() switch
        {
            "quarterly" => 3,
            "semiannual" => 6,
            "annual" => 12,
            _ => 1
        };
        var dates = new List<DateOnly>();
        for (var cycle = 0; cycle < MaximumBillingCycles; cycle++)
        {
            var chargeDate = AddMonthsClamped(startedOn, cycle * intervalMonths);
            if (chargeDate >= exclusiveBoundary || chargeDate > today)
            {
                break;
            }

            dates.Add(chargeDate);
        }
        return dates;
    }

    private static DateOnly AddMonthsClamped(DateOnly source, int months)
    {
        var first = new DateOnly(source.Year, source.Month, 1).AddMonths(months);
        return new DateOnly(
            first.Year,
            first.Month,
            Math.Min(source.Day, DateTime.DaysInMonth(first.Year, first.Month)));
    }

    private static bool TryParseDate(string? value, out DateOnly date)
    {
        return DateOnly.TryParseExact(
            value?.Trim(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }
}
