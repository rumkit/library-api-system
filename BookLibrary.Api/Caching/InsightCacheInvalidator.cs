using Microsoft.Extensions.Caching.Hybrid;

namespace BookLibrary.Api.Caching;

/// <summary>
/// Evicts every cached insight after a successful write. A new loan or a book/user create,
/// update or delete can change most-borrowed, top-borrowers, reading-pace and co-borrowed
/// results, so the whole tag is dropped rather than tracking per-entry dependencies. Call this
/// only after the backend call has returned successfully — a failed write must not evict.
/// </summary>
internal sealed class InsightCacheInvalidator(HybridCache cache)
{
    private static readonly string[] Tags = [CatalogCacheKeys.InsightsTag];

    public ValueTask InvalidateAsync(CancellationToken cancellationToken) =>
        cache.RemoveByTagAsync(Tags, cancellationToken);
}
