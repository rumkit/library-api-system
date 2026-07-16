using BookLibrary.Api.Caching;
using BookLibrary.Api.Contracts;
using BookLibrary.Contracts;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace BookLibrary.Api;

/// <summary>
/// The REST facade. Each endpoint is a thin adapter: bind/validate HTTP input, call the Catalog
/// gRPC backend, and project the reply onto a REST DTO. Backend validation failures surface as
/// ProblemDetails via <see cref="RpcExceptionHandler"/>, so there is no error handling here.
/// </summary>
public static class CatalogEndpoints
{
    private const int DefaultLimit = 20;

    // Every insight entry carries the same tag so a future write path can evict them in one call.
    private static readonly string[] InsightsTags = [CatalogCacheKeys.InsightsTag];

    private static HybridCacheEntryOptions Entry(TimeSpan expiration) => new() { Expiration = expiration };

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var books = app.MapGroup("/books").WithTags("Books");

        books.MapGet("/", async (
            CatalogService.CatalogServiceClient client,
            CancellationToken ct,
            int limit = DefaultLimit,
            int skip = 0) =>
        {
            var reply = await client.ListBooksAsync(
                new ListBooksRequest { Limit = limit, Skip = skip }, cancellationToken: ct);
            return Results.Ok(reply.Books.Select(b => b.ToDto()));
        })
        .WithSummary("List books (bounded by limit/skip).");

        books.MapGet("/{id:guid}", async (
            Guid id, CatalogService.CatalogServiceClient client, CancellationToken ct) =>
        {
            var book = await client.GetBookAsync(new GetBookRequest { Id = id.ToString() }, cancellationToken: ct);
            return Results.Ok(book.ToDto());
        })
        .WithSummary("Get a single book by id.");

        app.MapGet("/users/{id:guid}", async (
            Guid id, CatalogService.CatalogServiceClient client, CancellationToken ct) =>
        {
            var user = await client.GetUserAsync(new GetUserRequest { Id = id.ToString() }, cancellationToken: ct);
            return Results.Ok(user.ToDto());
        })
        .WithTags("Users")
        .WithSummary("Get a single user by id.");

        var insights = app.MapGroup("/insights").WithTags("Insights");

        insights.MapGet("/most-borrowed", async (
            CatalogService.CatalogServiceClient client,
            HybridCache cache,
            IOptions<CatalogCacheOptions> cacheOptions,
            CancellationToken ct,
            int limit = DefaultLimit,
            DateTime? from = null,
            DateTime? to = null) =>
        {
            var books = await cache.GetOrCreateAsync(
                CatalogCacheKeys.MostBorrowed(limit, from, to),
                (client, limit, from, to),
                static async (s, ct) =>
                {
                    var request = new GetMostBorrowedBooksRequest { Limit = s.limit };
                    if (s.from.ToTimestamp() is { } f) request.From = f;
                    if (s.to.ToTimestamp() is { } t) request.To = t;

                    var reply = await s.client.GetMostBorrowedBooksAsync(request, cancellationToken: ct);
                    return reply.Books.Select(b => b.ToDto()).ToList();
                },
                Entry(cacheOptions.Value.DefaultExpiration),
                tags: InsightsTags,
                cancellationToken: ct);
            return Results.Ok(books);
        })
        .WithSummary("Most borrowed books, optionally within a [from, to) window.");

        insights.MapGet("/top-borrowers", async (
            CatalogService.CatalogServiceClient client,
            HybridCache cache,
            IOptions<CatalogCacheOptions> cacheOptions,
            CancellationToken ct,
            int limit = DefaultLimit,
            DateTime? from = null,
            DateTime? to = null) =>
        {
            var borrowers = await cache.GetOrCreateAsync(
                CatalogCacheKeys.TopBorrowers(limit, from, to),
                (client, limit, from, to),
                static async (s, ct) =>
                {
                    var request = new GetTopBorrowersRequest { Limit = s.limit };
                    if (s.from.ToTimestamp() is { } f) request.From = f;
                    if (s.to.ToTimestamp() is { } t) request.To = t;

                    var reply = await s.client.GetTopBorrowersAsync(request, cancellationToken: ct);
                    return reply.Borrowers.Select(b => b.ToDto()).ToList();
                },
                Entry(cacheOptions.Value.TopBorrowersExpiration),
                tags: InsightsTags,
                cancellationToken: ct);
            return Results.Ok(borrowers);
        })
        .WithSummary("Top borrowers within a [from, to) window (defaults to year-to-date).");

        insights.MapGet("/reading-pace", async (
            Guid userId,
            Guid bookId,
            CatalogService.CatalogServiceClient client,
            HybridCache cache,
            IOptions<CatalogCacheOptions> cacheOptions,
            CancellationToken ct) =>
        {
            var pace = await cache.GetOrCreateAsync(
                CatalogCacheKeys.ReadingPace(userId, bookId),
                (client, userId, bookId),
                static async (s, ct) =>
                {
                    var reply = await s.client.GetReadingPaceAsync(
                        new GetReadingPaceRequest { UserId = s.userId.ToString(), BookId = s.bookId.ToString() },
                        cancellationToken: ct);
                    return reply.ToDto();
                },
                Entry(cacheOptions.Value.DefaultExpiration),
                tags: InsightsTags,
                cancellationToken: ct);
            return Results.Ok(pace);
        })
        .WithSummary("Estimate a user's reading pace (pages/day) for a book.");

        insights.MapGet("/co-borrowed/{bookId:guid}", async (
            Guid bookId,
            CatalogService.CatalogServiceClient client,
            HybridCache cache,
            IOptions<CatalogCacheOptions> cacheOptions,
            CancellationToken ct,
            int limit = DefaultLimit) =>
        {
            var books = await cache.GetOrCreateAsync(
                CatalogCacheKeys.CoBorrowed(bookId, limit),
                (client, bookId, limit),
                static async (s, ct) =>
                {
                    var reply = await s.client.GetCoBorrowedBooksAsync(
                        new GetCoBorrowedBooksRequest { BookId = s.bookId.ToString(), Limit = s.limit },
                        cancellationToken: ct);
                    return reply.Books.Select(b => b.ToDto()).ToList();
                },
                Entry(cacheOptions.Value.DefaultExpiration),
                tags: InsightsTags,
                cancellationToken: ct);
            return Results.Ok(books);
        })
        .WithSummary("Books co-borrowed by users who borrowed the given book.");

        return app;
    }
}
