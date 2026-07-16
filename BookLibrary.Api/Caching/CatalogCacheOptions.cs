namespace BookLibrary.Api.Caching;

/// <summary>
/// Time-to-live settings for cached insight responses, bound from the <c>"CatalogCache"</c>
/// configuration section. The API is read-only, so entries expire by time rather than on writes.
/// </summary>
public sealed class CatalogCacheOptions
{
    /// <summary>TTL for most-borrowed, reading-pace and co-borrowed insights.</summary>
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// TTL for top-borrowers. Shorter than the default because an omitted window makes the backend
    /// fill <c>to = UtcNow</c>, so the underlying result drifts over time behind a stable cache key.
    /// </summary>
    public TimeSpan TopBorrowersExpiration { get; set; } = TimeSpan.FromSeconds(60);
}
