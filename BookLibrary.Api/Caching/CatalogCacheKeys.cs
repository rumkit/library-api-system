using System.Globalization;

namespace BookLibrary.Api.Caching;

/// <summary>
/// Pure builders for the cache keys of the insight endpoints. Each key is fully determined by the
/// request inputs, so identical requests hit the same entry and any input change is a fresh key.
/// A nullable window bound formats as a round-trippable timestamp or the literal <c>none</c>, so an
/// absent value never collides with a supplied one.
/// </summary>
internal static class CatalogCacheKeys
{
    /// <summary>Tag applied to every insight entry, for future <c>RemoveByTagAsync</c> invalidation.</summary>
    public const string InsightsTag = "insights";

    public static string MostBorrowed(int limit, DateTime? from, DateTime? to) =>
        $"insights:most-borrowed:{limit}:{Window(from)}:{Window(to)}";

    public static string TopBorrowers(int limit, DateTime? from, DateTime? to) =>
        $"insights:top-borrowers:{limit}:{Window(from)}:{Window(to)}";

    public static string ReadingPace(Guid userId, Guid bookId) =>
        $"insights:reading-pace:{userId}:{bookId}";

    public static string CoBorrowed(Guid bookId, int limit) =>
        $"insights:co-borrowed:{bookId}:{limit}";

    private static string Window(DateTime? value) =>
        value is { } v ? v.ToString("o", CultureInfo.InvariantCulture) : "none";
}
