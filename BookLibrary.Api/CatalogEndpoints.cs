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

    // Mirrors CatalogGrpcService.MaxLimit (Catalog project) — keep the two in sync by hand, since
    // Api must not reference Catalog directly.
    private const int MaxLimit = 1000;

    private static readonly string LimitDescription = $"limit: 1–{MaxLimit} (values above {MaxLimit} are clamped)";

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
            string? cursor = null) =>
        {
            var request = new ListBooksRequest { Limit = limit };
            if (cursor is not null) request.Cursor = cursor;
            var reply = await client.ListBooksAsync(request, cancellationToken: ct);
            return Results.Ok(new CursorPage<BookDto>(
                reply.Books.Select(b => b.ToDto()).ToList(),
                reply.HasNextCursor ? reply.NextCursor : null));
        })
        .WithSummary("List books, cursor-paginated.")
        .WithDescription(LimitDescription)
        .Produces<CursorPage<BookDto>>()
        .ProducesProblem(StatusCodes.Status400BadRequest);

        books.MapGet("/{id:guid}", async (
            Guid id, CatalogService.CatalogServiceClient client, CancellationToken ct) =>
        {
            var book = await client.GetBookAsync(new GetBookRequest { Id = id.ToString() }, cancellationToken: ct);
            return Results.Ok(book.ToDto());
        })
        .WithSummary("Get a single book by id.")
        .Produces<BookDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        books.MapPost("/", async (
            CreateBookRequestDto request,
            CatalogService.CatalogServiceClient client,
            InsightCacheInvalidator cacheInvalidator,
            CancellationToken ct) =>
        {
            var book = await client.CreateBookAsync(new CreateBookRequest
            {
                Title = request.Title,
                Author = request.Author,
                PageCount = request.PageCount,
                Year = request.Year,
            }, cancellationToken: ct);
            await cacheInvalidator.InvalidateAsync(ct);
            var dto = book.ToDto();
            return Results.Created($"/books/{dto.Id}", dto);
        })
        .WithSummary("Create a book.")
        .Produces<BookDto>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        books.MapDelete("/{id:guid}", async (
            Guid id,
            CatalogService.CatalogServiceClient client,
            InsightCacheInvalidator cacheInvalidator,
            CancellationToken ct) =>
        {
            var reply = await client.DeleteBookAsync(new DeleteBookRequest { Id = id.ToString() }, cancellationToken: ct);
            await cacheInvalidator.InvalidateAsync(ct);
            return Results.Ok(new DeleteBookResultDto(reply.ClosedLoans));
        })
        .WithSummary("Delete a book. Any open loan on it is force-closed (lost-book flow).")
        .Produces<DeleteBookResultDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        var users = app.MapGroup("/users").WithTags("Users");

        users.MapGet("/", async (
            CatalogService.CatalogServiceClient client,
            CancellationToken ct,
            int limit = DefaultLimit,
            string? cursor = null) =>
        {
            var request = new ListUsersRequest { Limit = limit };
            if (cursor is not null) request.Cursor = cursor;
            var reply = await client.ListUsersAsync(request, cancellationToken: ct);
            return Results.Ok(new CursorPage<UserDto>(
                reply.Users.Select(u => u.ToDto()).ToList(),
                reply.HasNextCursor ? reply.NextCursor : null));
        })
        .WithSummary("List users, cursor-paginated.")
        .WithDescription(LimitDescription)
        .Produces<CursorPage<UserDto>>()
        .ProducesProblem(StatusCodes.Status400BadRequest);

        users.MapGet("/{id:guid}", async (
            Guid id, CatalogService.CatalogServiceClient client, CancellationToken ct) =>
        {
            var user = await client.GetUserAsync(new GetUserRequest { Id = id.ToString() }, cancellationToken: ct);
            return Results.Ok(user.ToDto());
        })
        .WithSummary("Get a single user by id.")
        .Produces<UserDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        users.MapPost("/", async (
            CreateUserRequestDto request,
            CatalogService.CatalogServiceClient client,
            InsightCacheInvalidator cacheInvalidator,
            CancellationToken ct) =>
        {
            var user = await client.CreateUserAsync(new CreateUserRequest { Name = request.Name }, cancellationToken: ct);
            await cacheInvalidator.InvalidateAsync(ct);
            var dto = user.ToDto();
            return Results.Created($"/users/{dto.Id}", dto);
        })
        .WithSummary("Create a user.")
        .Produces<UserDto>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        users.MapPut("/{id:guid}", async (
            Guid id,
            UpdateUserRequestDto request,
            CatalogService.CatalogServiceClient client,
            InsightCacheInvalidator cacheInvalidator,
            CancellationToken ct) =>
        {
            var user = await client.UpdateUserAsync(
                new UpdateUserRequest { Id = id.ToString(), Name = request.Name }, cancellationToken: ct);
            await cacheInvalidator.InvalidateAsync(ct);
            return Results.Ok(user.ToDto());
        })
        .WithSummary("Rename a user. Name is the entire mutable surface, so this is a full replacement.")
        .Produces<UserDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        users.MapDelete("/{id:guid}", async (
            Guid id,
            CatalogService.CatalogServiceClient client,
            InsightCacheInvalidator cacheInvalidator,
            CancellationToken ct) =>
        {
            await client.DeleteUserAsync(new DeleteUserRequest { Id = id.ToString() }, cancellationToken: ct);
            await cacheInvalidator.InvalidateAsync(ct);
            return Results.NoContent();
        })
        .WithSummary("Delete a user. Rejected with 409 while the user holds open loans.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        var loans = app.MapGroup("/loans").WithTags("Loans");

        loans.MapGet("/", async (
            CatalogService.CatalogServiceClient client,
            CancellationToken ct,
            int limit = DefaultLimit,
            string? cursor = null,
            Guid? userId = null,
            Guid? bookId = null,
            bool? openOnly = null) =>
        {
            var request = new ListLoansRequest { Limit = limit };
            if (cursor is not null) request.Cursor = cursor;
            if (userId is { } u) request.UserId = u.ToString();
            if (bookId is { } b) request.BookId = b.ToString();
            if (openOnly is { } o) request.OpenOnly = o;
            var reply = await client.ListLoansAsync(request, cancellationToken: ct);
            return Results.Ok(new CursorPage<LoanDto>(
                reply.Loans.Select(l => l.ToDto()).ToList(),
                reply.HasNextCursor ? reply.NextCursor : null));
        })
        .WithSummary("List loans, cursor-paginated. Optionally filter by userId, bookId, openOnly.")
        .WithDescription(LimitDescription)
        .Produces<CursorPage<LoanDto>>()
        .ProducesProblem(StatusCodes.Status400BadRequest);

        loans.MapGet("/{id:guid}", async (
            Guid id, CatalogService.CatalogServiceClient client, CancellationToken ct) =>
        {
            var loan = await client.GetLoanAsync(new GetLoanRequest { Id = id.ToString() }, cancellationToken: ct);
            return Results.Ok(loan.ToDto());
        })
        .WithSummary("Get a single loan by id.")
        .Produces<LoanDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        loans.MapPost("/", async (
            CreateLoanRequestDto request,
            CatalogService.CatalogServiceClient client,
            InsightCacheInvalidator cacheInvalidator,
            CancellationToken ct) =>
        {
            var grpcRequest = new CreateLoanRequest
            {
                BookId = request.BookId.ToString(),
                UserId = request.UserId.ToString(),
            };
            if (request.BorrowedAt.ToTimestamp() is { } borrowedAt) grpcRequest.BorrowedAt = borrowedAt;

            var loan = await client.CreateLoanAsync(grpcRequest, cancellationToken: ct);
            await cacheInvalidator.InvalidateAsync(ct);
            var dto = loan.ToDto();
            return Results.Created($"/loans/{dto.Id}", dto);
        })
        .WithSummary("Create a loan (borrow a book). Rejected with 409 if the book is already on loan.")
        .Produces<LoanDto>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        loans.MapPost("/{id:guid}/return", async (
            Guid id,
            ReturnLoanRequestDto? request,
            CatalogService.CatalogServiceClient client,
            InsightCacheInvalidator cacheInvalidator,
            CancellationToken ct) =>
        {
            var grpcRequest = new ReturnLoanRequest { Id = id.ToString() };
            if (request?.ReturnedAt.ToTimestamp() is { } returnedAt) grpcRequest.ReturnedAt = returnedAt;

            var loan = await client.ReturnLoanAsync(grpcRequest, cancellationToken: ct);
            await cacheInvalidator.InvalidateAsync(ct);
            return Results.Ok(loan.ToDto());
        })
        .WithSummary("Return a loan (close it). The only legal loan mutation; loans are never edited or deleted.")
        .Produces<LoanDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        var insights = app.MapGroup("/insights").WithTags("Insights");

        insights.MapGet("/most-borrowed", async (
            CatalogService.CatalogServiceClient client,
            HybridCache cache,
            IOptions<CatalogCacheOptions> cacheOptions,
            CancellationToken ct,
            int limit = DefaultLimit,
            UtcDateTime? from = null,
            UtcDateTime? to = null) =>
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
        .WithSummary("Most borrowed books, optionally within a [from, to) window.")
        .WithDescription(LimitDescription);

        insights.MapGet("/top-borrowers", async (
            CatalogService.CatalogServiceClient client,
            HybridCache cache,
            IOptions<CatalogCacheOptions> cacheOptions,
            CancellationToken ct,
            int limit = DefaultLimit,
            UtcDateTime? from = null,
            UtcDateTime? to = null) =>
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
        .WithSummary("Top borrowers within a [from, to) window (defaults to year-to-date).")
        .WithDescription(LimitDescription);

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
        .WithSummary("Books co-borrowed by users who borrowed the given book.")
        .WithDescription(LimitDescription);

        return app;
    }
}
