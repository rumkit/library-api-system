using BookLibrary.Catalog.Domain;
using MongoDB.Driver;

namespace BookLibrary.Catalog.Data;

/// <summary>
/// Creates the indexes the insight aggregations and write paths rely on, once at startup. Index
/// creation is idempotent in Mongo, so running on every boot is safe. Loans carry the analytical
/// load, so they get indexes on the fields the pipelines group/filter by; Books and Users get a
/// <c>(sortKey, _id)</c> compound index each to support cursor pagination.
/// </summary>
public sealed class MongoIndexInitializer(LibraryDb db, ILogger<MongoIndexInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var loanKeys = Builders<Loan>.IndexKeys;
        var loanModels = new[]
        {
            new CreateIndexModel<Loan>(loanKeys.Ascending(l => l.BookId)),
            new CreateIndexModel<Loan>(loanKeys.Ascending(l => l.UserId)),
            // Supports the reading-pace lookup, which filters by user and book together.
            new CreateIndexModel<Loan>(loanKeys.Ascending(l => l.UserId).Ascending(l => l.BookId)),
            // Double-loan guard (a book cannot have two open loans): unique on BookId, scoped to
            // open loans only via the partial filter.
            new CreateIndexModel<Loan>(
                loanKeys.Ascending(l => l.BookId),
                new CreateIndexOptions<Loan>
                {
                    Name = "ux_loans_open_book",
                    Unique = true,
                    PartialFilterExpression = Builders<Loan>.Filter.Eq(l => l.ReturnedAt, null),
                }),
            // Cursor paging over (BorrowedAt desc, _id desc); the BorrowedAt prefix also serves
            // the insight window filter, and Mongo walks the same index backwards for the desc
            // page order.
            new CreateIndexModel<Loan>(loanKeys.Ascending(l => l.BorrowedAt).Ascending(l => l.Id)),
        };

        var bookModels = new[]
        {
            // Case-insensitive listing order (see ListingCollation) requires the index collation
            // to match the query collation, or Mongo won't use the index. Named explicitly so a
            // database seeded before this change (which has the old, uncollated index under the
            // default name) gets this as a second index rather than an IndexOptionsConflict.
            new CreateIndexModel<Book>(
                Builders<Book>.IndexKeys.Ascending(b => b.Title).Ascending(b => b.Id),
                new CreateIndexOptions<Book> { Name = "ix_books_title_id_ci", Collation = ListingCollation.Collation }),
        };

        var userModels = new[]
        {
            new CreateIndexModel<User>(
                Builders<User>.IndexKeys.Ascending(u => u.Name).Ascending(u => u.Id),
                new CreateIndexOptions<User> { Name = "ix_users_name_id_ci", Collation = ListingCollation.Collation }),
        };

        await db.Loans.Indexes.CreateManyAsync(loanModels, cancellationToken);
        await db.Books.Indexes.CreateManyAsync(bookModels, cancellationToken);
        await db.Users.Indexes.CreateManyAsync(userModels, cancellationToken);

        logger.LogInformation(
            "Ensured {LoanCount} Loan, {BookCount} Book and {UserCount} User indexes.",
            loanModels.Length, bookModels.Length, userModels.Length);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
