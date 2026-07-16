using BookLibrary.Api.Caching;
using BookLibrary.Api.Contracts;

namespace BookLibrary.Api.Tests;

/// <summary>The insight cache keys: distinct inputs must yield distinct keys, and an absent
/// window bound must never collide with a supplied one.</summary>
public class CatalogCacheKeysTests
{
    private static readonly Guid BookId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly UtcDateTime From = new(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    private static readonly UtcDateTime To = new(new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc));

    [Test]
    public async Task MostBorrowed_WhenLimitDiffers_ShouldProduceDistinctKeys()
    {
        var a = CatalogCacheKeys.MostBorrowed(10, From, To);
        var b = CatalogCacheKeys.MostBorrowed(20, From, To);

        await Assert.That(a).IsNotEqualTo(b);
    }

    [Test]
    public async Task MostBorrowed_WhenNoWindow_ShouldNotCollideWithSuppliedWindow()
    {
        var open = CatalogCacheKeys.MostBorrowed(20, null, null);
        var bounded = CatalogCacheKeys.MostBorrowed(20, From, To);

        await Assert.That(open).IsNotEqualTo(bounded);
    }

    [Test]
    public async Task MostBorrowed_WhenOnlyFromSupplied_ShouldNotCollideWithOnlyTo()
    {
        var fromOnly = CatalogCacheKeys.MostBorrowed(20, From, null);
        var toOnly = CatalogCacheKeys.MostBorrowed(20, null, From);

        await Assert.That(fromOnly).IsNotEqualTo(toOnly);
    }

    [Test]
    public async Task MostBorrowed_WhenInputsMatch_ShouldProduceStableKey()
    {
        var a = CatalogCacheKeys.MostBorrowed(20, From, To);
        var b = CatalogCacheKeys.MostBorrowed(20, From, To);

        await Assert.That(a).IsEqualTo(b);
    }

    [Test]
    public async Task TopBorrowers_And_MostBorrowed_ShouldNotShareKeysForSameInputs()
    {
        var most = CatalogCacheKeys.MostBorrowed(20, From, To);
        var top = CatalogCacheKeys.TopBorrowers(20, From, To);

        await Assert.That(most).IsNotEqualTo(top);
    }

    [Test]
    public async Task ReadingPace_WhenUserAndBookSwapped_ShouldProduceDistinctKeys()
    {
        var a = CatalogCacheKeys.ReadingPace(UserId, BookId);
        var b = CatalogCacheKeys.ReadingPace(BookId, UserId);

        await Assert.That(a).IsNotEqualTo(b);
    }

    [Test]
    public async Task CoBorrowed_WhenLimitDiffers_ShouldProduceDistinctKeys()
    {
        var a = CatalogCacheKeys.CoBorrowed(BookId, 10);
        var b = CatalogCacheKeys.CoBorrowed(BookId, 20);

        await Assert.That(a).IsNotEqualTo(b);
    }
}
