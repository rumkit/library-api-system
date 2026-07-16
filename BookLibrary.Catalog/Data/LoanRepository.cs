using BookLibrary.Catalog.Data.Paging;
using BookLibrary.Catalog.Domain;
using MongoDB.Driver;

namespace BookLibrary.Catalog.Data;

public enum CreateLoanOutcome { Created, BookAlreadyOnLoan }

/// <summary>Optional filters for <see cref="ILoanRepository.ListAsync"/>.</summary>
public sealed record LoanFilter(Guid? UserId, Guid? BookId, bool OpenOnly);

public interface ILoanRepository
{
    Task<Loan?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Page<Loan>> ListAsync(int limit, string? cursor, LoanFilter filter, CancellationToken cancellationToken);

    /// <summary>Inserts the loan. Returns BookAlreadyOnLoan on the unique-index duplicate key.</summary>
    Task<(CreateLoanOutcome Outcome, Loan? Loan)> CreateAsync(Loan loan, CancellationToken cancellationToken);

    /// <summary>Closes one open loan. Returns null if it does not exist or is already closed.</summary>
    Task<Loan?> CloseAsync(Guid id, DateTime returnedAt, CancellationToken cancellationToken);

    /// <summary>Closes every open loan for a book. Returns the number closed. Used by book delete.</summary>
    Task<long> CloseOpenByBookAsync(Guid bookId, DateTime returnedAt, CancellationToken cancellationToken);

    Task<long> CountOpenByUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<Loan?> FindOpenByBookAsync(Guid bookId, CancellationToken cancellationToken);
}

public sealed class LoanRepository(LibraryDb db) : ILoanRepository
{
    public async Task<Loan?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Loans.Find(l => l.Id == id).FirstOrDefaultAsync(cancellationToken);

    public async Task<Page<Loan>> ListAsync(
        int limit, string? cursor, LoanFilter filter, CancellationToken cancellationToken)
    {
        var builder = Builders<Loan>.Filter;
        var filters = new List<FilterDefinition<Loan>>();

        if (filter.UserId is { } userId)
            filters.Add(builder.Eq(l => l.UserId, userId));
        if (filter.BookId is { } bookId)
            filters.Add(builder.Eq(l => l.BookId, bookId));
        if (filter.OpenOnly)
            filters.Add(builder.Eq(l => l.ReturnedAt, null));

        if (!string.IsNullOrEmpty(cursor))
        {
            var (sortKey, lastId) = Cursor.Decode(cursor);

            // Loans sort by BorrowedAt (a date), unlike books/users which sort by a string
            // field — a cursor whose sort key isn't a roundtrippable DateTime (e.g. one produced
            // by the book/user listings) is semantically invalid for this repository's sort, even
            // though it decoded structurally. TryParse (not Parse) keeps this a signal, not an
            // exception-driven control flow.
            if (!DateTime.TryParse(
                    sortKey, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var lastBorrowedAt))
                throw new InvalidCursorException("cursor is not valid.");

            // Newest-first order: strictly-before on BorrowedAt, or equal and strictly-before on id.
            filters.Add(builder.Or(
                builder.Lt(l => l.BorrowedAt, lastBorrowedAt),
                builder.And(
                    builder.Eq(l => l.BorrowedAt, lastBorrowedAt),
                    builder.Lt(l => l.Id, lastId))));
        }

        var combined = filters.Count == 0 ? FilterDefinition<Loan>.Empty : builder.And(filters);

        var items = await db.Loans
            .Find(combined)
            .SortByDescending(l => l.BorrowedAt)
            .ThenByDescending(l => l.Id)
            .Limit(limit + 1)
            .ToListAsync(cancellationToken);

        if (items.Count <= limit)
            return new Page<Loan>(items, null);

        var page = items[..limit];
        var last = page[^1];
        var nextCursor = Cursor.Encode(
            DateTime.SpecifyKind(last.BorrowedAt, DateTimeKind.Utc).ToString("o"), last.Id);
        return new Page<Loan>(page, nextCursor);
    }

    public async Task<(CreateLoanOutcome Outcome, Loan? Loan)> CreateAsync(Loan loan, CancellationToken cancellationToken)
    {
        try
        {
            await db.Loans.InsertOneAsync(loan, cancellationToken: cancellationToken);
            return (CreateLoanOutcome.Created, loan);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return (CreateLoanOutcome.BookAlreadyOnLoan, null);
        }
    }

    public async Task<Loan?> CloseAsync(Guid id, DateTime returnedAt, CancellationToken cancellationToken) =>
        await db.Loans.FindOneAndUpdateAsync(
            l => l.Id == id && l.ReturnedAt == null,
            Builders<Loan>.Update.Set(l => l.ReturnedAt, returnedAt),
            new FindOneAndUpdateOptions<Loan> { ReturnDocument = ReturnDocument.After },
            cancellationToken);

    public async Task<long> CloseOpenByBookAsync(Guid bookId, DateTime returnedAt, CancellationToken cancellationToken)
    {
        var result = await db.Loans.UpdateManyAsync(
            l => l.BookId == bookId && l.ReturnedAt == null,
            Builders<Loan>.Update.Set(l => l.ReturnedAt, returnedAt),
            cancellationToken: cancellationToken);
        return result.ModifiedCount;
    }

    public async Task<long> CountOpenByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Loans.CountDocumentsAsync(
            l => l.UserId == userId && l.ReturnedAt == null, cancellationToken: cancellationToken);

    public async Task<Loan?> FindOpenByBookAsync(Guid bookId, CancellationToken cancellationToken) =>
        await db.Loans
            .Find(l => l.BookId == bookId && l.ReturnedAt == null)
            .FirstOrDefaultAsync(cancellationToken);
}
