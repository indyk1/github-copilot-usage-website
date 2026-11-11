using System.Globalization;

namespace CopilotUsage.Web.Ui;

/// <summary>
/// Display formatting for the dashboard. Always invariant culture ("47,500", "$12.50"), so the server's regional
/// settings never change how a figure looks and the count-up script (en-US) renders the same text.
/// </summary>
public static class Fmt
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>Whole credits with thousands separators; a positive amount under one credit shows as "&lt;1".</summary>
    public static string Credits(decimal credits)
    {
        if (credits > 0m && credits < 1m) return "<1";
        return Math.Round(credits, 0, MidpointRounding.AwayFromZero).ToString("N0", C);
    }

    /// <summary>Dollars with cents: "$12.50".</summary>
    public static string Dollars(decimal dollars) =>
        (dollars < 0m ? "-$" : "$") + Math.Abs(dollars).ToString("N2", C);

    /// <summary>
    /// A percentage. Below 100 it truncates rather than rounds, so 99.97% never reads as "100%" while the pool still
    /// has credits left. At 100 and above it rounds. Null (no denominator) shows an en dash.
    /// </summary>
    public static string Percent(decimal? value, int decimals = 0)
    {
        if (value is not { } v) return "–";
        decimals = Math.Clamp(decimals, 0, 4);
        if (v < 100m)
        {
            var factor = (decimal)Math.Pow(10, decimals);
            v = Math.Floor(Math.Max(0m, v) * factor) / factor;
        }
        else v = Math.Round(v, decimals, MidpointRounding.AwayFromZero);
        return v.ToString("N" + decimals, C) + "%";
    }

    /// <summary>Short axis labels: 950, 12.5k, 47.5k, 1.2M.</summary>
    public static string Compact(decimal value)
    {
        var a = Math.Abs(value);
        if (a >= 1_000_000m) return Trim(value / 1_000_000m) + "M";
        if (a >= 1_000m) return Trim(value / 1_000m) + "k";
        return Math.Round(value, 0, MidpointRounding.AwayFromZero).ToString("N0", C);

        static string Trim(decimal v) => Math.Round(v, 1, MidpointRounding.AwayFromZero).ToString("0.#", C);
    }

    /// <summary>"11 Nov 2026, 10:00 UTC".</summary>
    public static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("d MMM yyyy, HH:mm", C) + " UTC";

    /// <summary>"November 2026".</summary>
    public static string Month(CopilotUsage.Core.Calculations.BillingMonth month) =>
        month.Start.ToString("MMMM yyyy", C);

    /// <summary>"11 Nov".</summary>
    public static string DayMonth(DateOnly day) => day.ToString("d MMM", C);

    /// <summary>"just now", "5 min ago", "3 h ago", "2 days ago".</summary>
    public static string Relative(DateTimeOffset value, DateTimeOffset now)
    {
        var d = now - value;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours} h ago";
        var days = (int)d.TotalDays;
        return days == 1 ? "1 day ago" : $"{days} days ago";
    }

    /// <summary>"1 day", "20 days".</summary>
    public static string Plural(int count, string singular, string? plural = null) =>
        $"{count.ToString("N0", C)} {(count == 1 ? singular : plural ?? singular + "s")}";
}
