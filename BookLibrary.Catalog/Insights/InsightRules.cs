namespace BookLibrary.Catalog.Insights;

/// <summary>
/// Shared business rules that shape every insight. Centralised so the same definition of a
/// "counted borrow" is applied identically by the aggregation pipelines and the in-process
/// reading-pace calculation — and so it is documented in exactly one place.
/// </summary>
public static class InsightRules
{
    /// <summary>
    /// A loan returned in less than this window is treated as a mistake (wrong book, or
    /// over-borrowed for a weekend) rather than a genuine borrow, and is excluded from every
    /// insight. The boundary is duration-based (not calendar-day) to avoid midnight/timezone
    /// edge cases. Open loans still count — the book left the shelf, it just isn't back yet.
    /// </summary>
    public static readonly TimeSpan MinimumBorrowDuration = TimeSpan.FromDays(1);

    /// <summary>Milliseconds in <see cref="MinimumBorrowDuration"/>, for use in Mongo pipelines
    /// where subtracting two dates yields milliseconds.</summary>
    public static readonly long MinimumBorrowDurationMs = (long)MinimumBorrowDuration.TotalMilliseconds;

    /// <summary>
    /// True when a loan should be counted as a genuine borrow: either still open, or held for
    /// at least <see cref="MinimumBorrowDuration"/> before being returned.
    /// </summary>
    public static bool IsCountedBorrow(DateTime borrowedAt, DateTime? returnedAt) =>
        returnedAt is null || (returnedAt.Value - borrowedAt) >= MinimumBorrowDuration;
}
