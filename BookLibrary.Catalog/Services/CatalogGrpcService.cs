using BookLibrary.Catalog.Data;
using BookLibrary.Catalog.Insights;
using BookLibrary.Catalog.Mapping;
using BookLibrary.Contracts;
using Grpc.Core;
using Google.Protobuf.WellKnownTypes;

namespace BookLibrary.Catalog.Services;

/// <summary>
/// gRPC implementation of the catalog contract. Owns validation (translated to gRPC status
/// codes the REST edge maps to HTTP), delegates persistence/aggregation to the repositories,
/// and maps domain models onto contract messages. No HTTP concerns leak in here.
/// </summary>
public sealed partial class CatalogGrpcService(
    IBookRepository books,
    IUserRepository users,
    IInsightRepository insights,
    CatalogMapper mapper,
    ILogger<CatalogGrpcService> logger) : CatalogService.CatalogServiceBase
{
    private const int MaxLimit = 1000;

    public override async Task<Book> GetBook(GetBookRequest request, ServerCallContext context)
    {
        var id = ParseId(request.Id, nameof(request.Id));
        var book = await books.GetAsync(id, context.CancellationToken)
                   ?? throw NotFound($"Book '{id}' was not found.");
        return mapper.ToContract(book);
    }

    public override async Task<User> GetUser(GetUserRequest request, ServerCallContext context)
    {
        var id = ParseId(request.Id, nameof(request.Id));
        var user = await users.GetAsync(id, context.CancellationToken)
                   ?? throw NotFound($"User '{id}' was not found.");
        return mapper.ToContract(user);
    }

    public override async Task<ListBooksResponse> ListBooks(ListBooksRequest request, ServerCallContext context)
    {
        var limit = NormalizeLimit(request.Limit);
        var skip = request.Skip < 0
            ? throw InvalidArgument("skip must be zero or greater.")
            : request.Skip;

        var result = await books.ListAsync(limit, skip, context.CancellationToken);
        var response = new ListBooksResponse();
        response.Books.AddRange(result.Select(mapper.ToContract));
        return response;
    }

    public override async Task<MostBorrowedBooksResponse> GetMostBorrowedBooks(
        GetMostBorrowedBooksRequest request, ServerCallContext context)
    {
        var limit = NormalizeLimit(request.Limit);
        var (from, to) = ReadOptionalWindow(request.From, request.To);
        Log.MostBorrowed(logger, limit, from, to);

        var result = await insights.GetMostBorrowedBooksAsync(limit, from, to, context.CancellationToken);
        Log.ResultCount(logger, result.Count);

        var response = new MostBorrowedBooksResponse();
        response.Books.AddRange(result.Select(r => new BorrowedBook
        {
            Book = mapper.ToContract(r.Book),
            BorrowCount = r.BorrowCount,
        }));
        return response;
    }

    public override async Task<TopBorrowersResponse> GetTopBorrowers(
        GetTopBorrowersRequest request, ServerCallContext context)
    {
        var limit = NormalizeLimit(request.Limit);
        var (from, to) = ResolveBorrowerWindow(request.From, request.To);
        Log.TopBorrowers(logger, limit, from, to);

        var result = await insights.GetTopBorrowersAsync(from, to, limit, context.CancellationToken);
        Log.ResultCount(logger, result.Count);

        var response = new TopBorrowersResponse();
        response.Borrowers.AddRange(result.Select(r => new Borrower
        {
            User = mapper.ToContract(r.User),
            BorrowCount = r.BorrowCount,
        }));
        return response;
    }

    public override async Task<CoBorrowedBooksResponse> GetCoBorrowedBooks(
        GetCoBorrowedBooksRequest request, ServerCallContext context)
    {
        var bookId = ParseId(request.BookId, nameof(request.BookId));
        var limit = NormalizeLimit(request.Limit);
        Log.CoBorrowed(logger, bookId, limit);

        // Surface a clear 404 rather than an empty list when the book itself does not exist.
        if (await books.GetAsync(bookId, context.CancellationToken) is null)
            throw NotFound($"Book '{bookId}' was not found.");

        var result = await insights.GetCoBorrowedBooksAsync(bookId, limit, context.CancellationToken);
        Log.ResultCount(logger, result.Count);

        var response = new CoBorrowedBooksResponse();
        response.Books.AddRange(result.Select(r => new CoBorrowedBook
        {
            Book = mapper.ToContract(r.Book),
            CoBorrowerCount = r.CoBorrowerCount,
        }));
        return response;
    }

    public override async Task<ReadingPaceResponse> GetReadingPace(
        GetReadingPaceRequest request, ServerCallContext context)
    {
        var userId = ParseId(request.UserId, nameof(request.UserId));
        var bookId = ParseId(request.BookId, nameof(request.BookId));

        var book = await books.GetAsync(bookId, context.CancellationToken)
                   ?? throw NotFound($"Book '{bookId}' was not found.");
        if (await users.GetAsync(userId, context.CancellationToken) is null)
            throw NotFound($"User '{userId}' was not found.");

        var loans = await insights.GetLoansAsync(userId, bookId, context.CancellationToken);
        var pace = ReadingPace.Compute(book, loans);

        return new ReadingPaceResponse
        {
            Computable = pace.Computable,
            PagesPerDay = pace.PagesPerDay,
            Reason = pace.Reason,
        };
    }

    private static (DateTime? From, DateTime? To) ReadOptionalWindow(Timestamp? from, Timestamp? to)
    {
        var f = from?.ToDateTime();
        var t = to?.ToDateTime();
        if (f is not null && t is not null && f > t)
            throw InvalidArgument("'from' must not be after 'to'.");
        return (f, t);
    }

    // Top borrowers default to year-to-date when no window is supplied.
    private static (DateTime From, DateTime To) ResolveBorrowerWindow(Timestamp? from, Timestamp? to)
    {
        var now = DateTime.UtcNow;
        var f = from?.ToDateTime() ?? new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = to?.ToDateTime() ?? now;
        if (f > t)
            throw InvalidArgument("'from' must not be after 'to'.");
        return (f, t);
    }

    private static int NormalizeLimit(int limit) => limit switch
    {
        <= 0 => throw InvalidArgument("limit must be greater than zero."),
        > MaxLimit => MaxLimit,
        _ => limit,
    };

    private static Guid ParseId(string value, string field) =>
        Guid.TryParse(value, out var id)
            ? id
            : throw InvalidArgument($"{field} is not a valid id.");

    private static RpcException InvalidArgument(string message) =>
        new(new Status(StatusCode.InvalidArgument, message));

    private static RpcException NotFound(string message) =>
        new(new Status(StatusCode.NotFound, message));

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Most-borrowed query: limit={Limit}, from={From}, to={To}")]
        public static partial void MostBorrowed(ILogger logger, int limit, DateTime? from, DateTime? to);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Top-borrowers query: limit={Limit}, from={From}, to={To}")]
        public static partial void TopBorrowers(ILogger logger, int limit, DateTime from, DateTime to);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Co-borrowed query: bookId={BookId}, limit={Limit}")]
        public static partial void CoBorrowed(ILogger logger, Guid bookId, int limit);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Insight returned {Count} rows.")]
        public static partial void ResultCount(ILogger logger, int count);
    }
}
