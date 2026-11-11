using CopilotUsage.Data.Queries;

namespace CopilotUsage.Web.Ui;

public enum UserSort { User, Credits, FairShare, Prompts, Acceptance, TopModel, LastActive }

/// <summary>Search, sort and "inactive" rules for the users table, kept out of the component so they can be tested.</summary>
public static class UsersTable
{
    /// <summary>A seat with no credits and no prompts this month. Such seats are marked, never hidden.</summary>
    public static bool IsInactive(UserRow row) => row.Credits <= 0m && row.Prompts <= 0;

    /// <summary>Text columns start A to Z; number and date columns start with the highest or newest.</summary>
    public static bool DefaultDescending(UserSort sort) => sort is not (UserSort.User or UserSort.TopModel);

    /// <summary>Clicking the current column flips its direction; clicking another column starts at that column's default.</summary>
    public static (UserSort Sort, bool Descending) Toggle(UserSort current, bool descending, UserSort clicked) =>
        clicked == current ? (current, !descending) : (clicked, DefaultDescending(clicked));

    public static IReadOnlyList<UserRow> Filter(IEnumerable<UserRow> rows, string? search)
    {
        var term = search?.Trim();
        return string.IsNullOrEmpty(term)
            ? rows.ToList()
            : rows.Where(r => r.Login.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Sorts by the chosen column. Rows with no value in that column (no acceptance rate, no top model, never active)
    /// always sit last, whichever direction is chosen. Ties fall back to login A to Z.
    /// </summary>
    public static IReadOnlyList<UserRow> Sort(IEnumerable<UserRow> rows, UserSort sort, bool descending)
    {
        var present = new List<UserRow>();
        var missing = new List<UserRow>();
        foreach (var r in rows) (HasValue(r, sort) ? present : missing).Add(r);

        IOrderedEnumerable<UserRow> ordered = sort switch
        {
            UserSort.User => Order(present, r => r.Login, StringComparer.OrdinalIgnoreCase, descending),
            UserSort.Credits => Order(present, r => r.Credits, Comparer<decimal>.Default, descending),
            UserSort.FairShare => Order(present, r => r.FairSharePercent ?? 0m, Comparer<decimal>.Default, descending),
            UserSort.Prompts => Order(present, r => r.Prompts, Comparer<long>.Default, descending),
            UserSort.Acceptance => Order(present, r => r.AcceptanceRate ?? 0m, Comparer<decimal>.Default, descending),
            UserSort.TopModel => Order(present, r => r.TopModel ?? "", StringComparer.OrdinalIgnoreCase, descending),
            _ => Order(present, r => r.LastActiveAt ?? DateTimeOffset.MinValue, Comparer<DateTimeOffset>.Default, descending),
        };
        return ordered.ThenBy(r => r.Login, StringComparer.OrdinalIgnoreCase)
            .Concat(missing.OrderBy(r => r.Login, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private static bool HasValue(UserRow r, UserSort sort) => sort switch
    {
        UserSort.Acceptance => r.AcceptanceRate is not null,
        UserSort.TopModel => !string.IsNullOrWhiteSpace(r.TopModel),
        UserSort.LastActive => r.LastActiveAt is not null,
        UserSort.FairShare => r.FairSharePercent is not null,
        _ => true,
    };

    private static IOrderedEnumerable<UserRow> Order<TKey>(IEnumerable<UserRow> rows, Func<UserRow, TKey> key, IComparer<TKey> comparer, bool descending) =>
        descending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);

    /// <summary>The value for <c>aria-sort</c> on a column header.</summary>
    public static string AriaSort(UserSort column, UserSort current, bool descending) =>
        column != current ? "none" : descending ? "descending" : "ascending";

    /// <summary>URL-safe names for the query string (<c>?q=...&amp;sort=prompts&amp;dir=asc</c>).</summary>
    public static string Key(UserSort sort) => sort switch
    {
        UserSort.User => "user", UserSort.Credits => "credits", UserSort.FairShare => "fair", UserSort.Prompts => "prompts",
        UserSort.Acceptance => "acceptance", UserSort.TopModel => "model", _ => "active",
    };

    public static UserSort? ParseKey(string? key) => key?.ToLowerInvariant() switch
    {
        "user" => UserSort.User, "credits" => UserSort.Credits, "fair" => UserSort.FairShare, "prompts" => UserSort.Prompts,
        "acceptance" => UserSort.Acceptance, "model" => UserSort.TopModel, "active" => UserSort.LastActive, _ => null,
    };
}
