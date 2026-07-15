using BookLibrary.Api.Contracts;
using BookLibrary.Contracts;

namespace BookLibrary.Api;

/// <summary>
/// The REST facade. Each endpoint is a thin adapter: bind/validate HTTP input, call the Catalog
/// gRPC backend, and project the reply onto a REST DTO. Backend validation failures surface as
/// ProblemDetails via <see cref="RpcExceptionHandler"/>, so there is no error handling here.
/// </summary>
public static class CatalogEndpoints
{
    private const int DefaultLimit = 20;

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
            CancellationToken ct,
            int limit = DefaultLimit,
            DateTime? from = null,
            DateTime? to = null) =>
        {
            var request = new GetMostBorrowedBooksRequest { Limit = limit };
            if (from.ToTimestamp() is { } f) request.From = f;
            if (to.ToTimestamp() is { } t) request.To = t;

            var reply = await client.GetMostBorrowedBooksAsync(request, cancellationToken: ct);
            return Results.Ok(reply.Books.Select(b => b.ToDto()));
        })
        .WithSummary("Most borrowed books, optionally within a [from, to) window.");

        insights.MapGet("/top-borrowers", async (
            CatalogService.CatalogServiceClient client,
            CancellationToken ct,
            int limit = DefaultLimit,
            DateTime? from = null,
            DateTime? to = null) =>
        {
            var request = new GetTopBorrowersRequest { Limit = limit };
            if (from.ToTimestamp() is { } f) request.From = f;
            if (to.ToTimestamp() is { } t) request.To = t;

            var reply = await client.GetTopBorrowersAsync(request, cancellationToken: ct);
            return Results.Ok(reply.Borrowers.Select(b => b.ToDto()));
        })
        .WithSummary("Top borrowers within a [from, to) window (defaults to year-to-date).");

        insights.MapGet("/reading-pace", async (
            Guid userId,
            Guid bookId,
            CatalogService.CatalogServiceClient client,
            CancellationToken ct) =>
        {
            var reply = await client.GetReadingPaceAsync(
                new GetReadingPaceRequest { UserId = userId.ToString(), BookId = bookId.ToString() },
                cancellationToken: ct);
            return Results.Ok(reply.ToDto());
        })
        .WithSummary("Estimate a user's reading pace (pages/day) for a book.");

        insights.MapGet("/co-borrowed/{bookId:guid}", async (
            Guid bookId,
            CatalogService.CatalogServiceClient client,
            CancellationToken ct,
            int limit = DefaultLimit) =>
        {
            var reply = await client.GetCoBorrowedBooksAsync(
                new GetCoBorrowedBooksRequest { BookId = bookId.ToString(), Limit = limit },
                cancellationToken: ct);
            return Results.Ok(reply.Books.Select(b => b.ToDto()));
        })
        .WithSummary("Books co-borrowed by users who borrowed the given book.");

        return app;
    }
}
