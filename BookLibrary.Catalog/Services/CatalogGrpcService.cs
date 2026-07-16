using BookLibrary.Catalog.Data;
using BookLibrary.Catalog.Data.Paging;
using BookLibrary.Catalog.Insights;
using BookLibrary.Catalog.Mapping;
using BookLibrary.Contracts;
using Grpc.Core;
using Google.Protobuf.WellKnownTypes;
using Domain = BookLibrary.Catalog.Domain;

namespace BookLibrary.Catalog.Services;

/// <summary>
/// gRPC implementation of the catalog contract. Owns validation (translated to gRPC status
/// codes the REST edge maps to HTTP), delegates persistence/aggregation to the repositories,
/// and maps domain models onto contract messages. No HTTP concerns leak in here.
/// </summary>
public sealed partial class CatalogGrpcService(
    IBookRepository books,
    IUserRepository users,
    ILoanRepository loans,
    IInsightRepository insights,
    CatalogMapper mapper,
    ILogger<CatalogGrpcService> logger) : CatalogService.CatalogServiceBase
{
    private const int MaxLimit = 1000;
    private const int MaxTextLength = 500;

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

    public override async Task<ListUsersResponse> ListUsers(ListUsersRequest request, ServerCallContext context)
    {
        var limit = NormalizeLimit(request.Limit);
        var cursor = request.HasCursor ? request.Cursor : null;
        if (cursor is not null && !Cursor.TryDecode(cursor, out _, out _))
            throw InvalidArgument("cursor is not valid.");

        var page = await users.ListAsync(limit, cursor, context.CancellationToken);
        var response = new ListUsersResponse();
        response.Users.AddRange(page.Items.Select(mapper.ToContract));
        if (page.NextCursor is not null)
            response.NextCursor = page.NextCursor;
        return response;
    }

    public override async Task<User> CreateUser(CreateUserRequest request, ServerCallContext context)
    {
        var name = RequireText(request.Name, nameof(request.Name));

        // Duplicate names are allowed: a name is a display label, not an identity.
        var user = new Domain.User { Id = Guid.CreateVersion7(), Name = name };
        await users.CreateAsync(user, context.CancellationToken);
        Log.UserCreated(logger, user.Id, user.Name);
        return mapper.ToContract(user);
    }

    public override async Task<User> UpdateUser(UpdateUserRequest request, ServerCallContext context)
    {
        var id = ParseId(request.Id, nameof(request.Id));
        var name = RequireText(request.Name, nameof(request.Name));

        // Renames do not touch loan history: Loan.UserName is a snapshot of the name as it was at
        // borrow time (see Loan's XML docs and data-schema.md), so a later rename must not rewrite
        // it. Do not add a cascading update to Loans here.
        var user = await users.UpdateNameAsync(id, name, context.CancellationToken)
                   ?? throw NotFound($"User '{id}' was not found.");
        Log.UserRenamed(logger, id, name);
        return mapper.ToContract(user);
    }

    public override async Task<DeleteUserResponse> DeleteUser(DeleteUserRequest request, ServerCallContext context)
    {
        var id = ParseId(request.Id, nameof(request.Id));
        _ = await users.GetAsync(id, context.CancellationToken)
            ?? throw NotFound($"User '{id}' was not found.");

        // Deliberately asymmetric with book delete: deleting a book closes its open loans (the
        // book is physically gone), but deleting a user is refused while they hold books, because
        // those copies are still out in the world and must come back first.
        var openLoans = await loans.CountOpenByUserAsync(id, context.CancellationToken);
        if (openLoans > 0)
        {
            Log.UserDeleteRejected(logger, id, openLoans);
            throw FailedPrecondition($"User '{id}' still holds {openLoans} book(s). Close the loans first.");
        }

        // Accepted race: a loan created between the check above and the delete below leaves an
        // open loan pointing at a deleted user. The insight pipelines $lookup/$unwind and drop
        // dangling references, so this degrades gracefully; standalone Mongo has no transactions
        // to close the window, and that is the documented "no enforced referential integrity" stance.
        await users.DeleteAsync(id, context.CancellationToken);
        Log.UserDeleted(logger, id);
        return new DeleteUserResponse();
    }

    public override async Task<ListBooksResponse> ListBooks(ListBooksRequest request, ServerCallContext context)
    {
        var limit = NormalizeLimit(request.Limit);
        var cursor = request.HasCursor ? request.Cursor : null;
        if (cursor is not null && !Cursor.TryDecode(cursor, out _, out _))
            throw InvalidArgument("cursor is not valid.");

        var page = await books.ListAsync(limit, cursor, context.CancellationToken);
        var response = new ListBooksResponse();
        response.Books.AddRange(page.Items.Select(mapper.ToContract));
        if (page.NextCursor is not null)
            response.NextCursor = page.NextCursor;
        return response;
    }

    public override async Task<Book> CreateBook(CreateBookRequest request, ServerCallContext context)
    {
        var title = RequireText(request.Title, nameof(request.Title));
        var author = RequireText(request.Author, nameof(request.Author));
        if (request.PageCount < 0)
            throw InvalidArgument("page_count must be zero or greater.");
        if (request.Year != 0 && (request.Year < 1 || request.Year > DateTime.UtcNow.Year + 1))
            throw InvalidArgument("year must be 0 (unknown) or a plausible publication year.");

        // Duplicate titles are allowed: one Book document is one physical copy, so two copies of
        // the same title are two documents.
        var book = new Domain.Book
        {
            Id = Guid.CreateVersion7(),
            Title = title,
            Author = author,
            PageCount = request.PageCount,
            Year = request.Year,
        };
        await books.CreateAsync(book, context.CancellationToken);
        Log.BookCreated(logger, book.Id, book.Title);
        return mapper.ToContract(book);
    }

    public override async Task<DeleteBookResponse> DeleteBook(DeleteBookRequest request, ServerCallContext context)
    {
        var id = ParseId(request.Id, nameof(request.Id));
        _ = await books.GetAsync(id, context.CancellationToken)
            ?? throw NotFound($"Book '{id}' was not found.");

        // The "lost book" flow: the physical copy is gone, so any open loan can never be closed by
        // a return and must be force-closed here. Close loans before deleting the book so there is
        // no window where the book is gone but a loan is still open. The loan keeps its
        // BookTitle/BookAuthor snapshot, so history stays readable after the book is deleted.
        var closedLoans = await loans.CloseOpenByBookAsync(id, DateTime.UtcNow, context.CancellationToken);
        await books.DeleteAsync(id, context.CancellationToken);
        Log.BookDeleted(logger, id, closedLoans);

        return new DeleteBookResponse { ClosedLoans = (int)closedLoans };
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

    private static string RequireText(string value, string field)
    {
        var trimmed = value.Trim();
        return trimmed.Length switch
        {
            0 => throw InvalidArgument($"{field} must not be blank."),
            > MaxTextLength => throw InvalidArgument($"{field} must be at most {MaxTextLength} characters."),
            _ => trimmed,
        };
    }

    private static RpcException InvalidArgument(string message) =>
        new(new Status(StatusCode.InvalidArgument, message));

    private static RpcException NotFound(string message) =>
        new(new Status(StatusCode.NotFound, message));

    private static RpcException FailedPrecondition(string message) =>
        new(new Status(StatusCode.FailedPrecondition, message));

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

        [LoggerMessage(Level = LogLevel.Information, Message = "Book created: id={BookId}, title={Title}")]
        public static partial void BookCreated(ILogger logger, Guid bookId, string title);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Book deleted: id={BookId}, closedLoans={ClosedLoans}")]
        public static partial void BookDeleted(ILogger logger, Guid bookId, long closedLoans);

        [LoggerMessage(Level = LogLevel.Information, Message = "User created: id={UserId}, name={Name}")]
        public static partial void UserCreated(ILogger logger, Guid userId, string name);

        [LoggerMessage(Level = LogLevel.Information, Message = "User renamed: id={UserId}, name={Name}")]
        public static partial void UserRenamed(ILogger logger, Guid userId, string name);

        [LoggerMessage(Level = LogLevel.Information, Message = "User deleted: id={UserId}")]
        public static partial void UserDeleted(ILogger logger, Guid userId);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "User delete rejected: id={UserId}, openLoans={OpenLoans}")]
        public static partial void UserDeleteRejected(ILogger logger, Guid userId, long openLoans);
    }
}
