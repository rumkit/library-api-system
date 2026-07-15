using BookLibrary.Catalog.Data;
using BookLibrary.Catalog.Domain;
using MongoDB.Driver;

namespace BookLibrary.Seeder;

/// <summary>
/// One-shot, idempotent seeding: if the catalog already has books it does nothing, otherwise it
/// inserts the fixed <see cref="SampleData"/>. Kept out of the Catalog service on purpose so
/// startup stays free of data-loading concerns.
/// </summary>
public static partial class SeedRunner
{
    public static async Task RunAsync(LibraryDb db, ILogger logger, CancellationToken cancellationToken)
    {
        var existing = await db.Books.CountDocumentsAsync(
            FilterDefinition<Book>.Empty, cancellationToken: cancellationToken);
        if (existing > 0)
        {
            Log.Skipped(logger, existing);
            return;
        }

        Log.Starting(logger);

        var loans = SampleData.BuildLoans(DateTime.UtcNow);
        await db.Books.InsertManyAsync(SampleData.Books, cancellationToken: cancellationToken);
        await db.Users.InsertManyAsync(SampleData.Users, cancellationToken: cancellationToken);
        await db.Loans.InsertManyAsync(loans, cancellationToken: cancellationToken);

        Log.Seeded(logger, SampleData.Books.Count, SampleData.Users.Count, loans.Count);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Catalog already has {Count} books; skipping seed.")]
        public static partial void Skipped(ILogger logger, long count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Seeding sample library data...")]
        public static partial void Starting(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Seeded {Books} books, {Users} users, {Loans} loans.")]
        public static partial void Seeded(ILogger logger, int books, int users, int loans);
    }
}
