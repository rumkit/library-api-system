using BookLibrary.Catalog.Domain;
using MongoDB.Driver;

namespace BookLibrary.Catalog.Data;

/// <summary>
/// Creates the indexes the insight aggregations rely on, once at startup. Index creation is
/// idempotent in Mongo, so running on every boot is safe. Loans carry the analytical load, so
/// they get indexes on the fields the pipelines group/filter by.
/// </summary>
public sealed class MongoIndexInitializer(LibraryDb db, ILogger<MongoIndexInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var keys = Builders<Loan>.IndexKeys;
        var models = new[]
        {
            new CreateIndexModel<Loan>(keys.Ascending(l => l.BookId)),
            new CreateIndexModel<Loan>(keys.Ascending(l => l.UserId)),
            new CreateIndexModel<Loan>(keys.Ascending(l => l.BorrowedAt)),
            // Supports the reading-pace lookup, which filters by user and book together.
            new CreateIndexModel<Loan>(keys.Ascending(l => l.UserId).Ascending(l => l.BookId)),
        };

        await db.Loans.Indexes.CreateManyAsync(models, cancellationToken);
        logger.LogInformation("Ensured {Count} Loan indexes.", models.Length);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
