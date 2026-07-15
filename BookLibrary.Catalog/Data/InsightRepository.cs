using BookLibrary.Catalog.Domain;
using BookLibrary.Catalog.Insights;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace BookLibrary.Catalog.Data;

public interface IInsightRepository
{
    Task<IReadOnlyList<MostBorrowedResult>> GetMostBorrowedBooksAsync(
        int limit, DateTime? from, DateTime? to, CancellationToken cancellationToken);

    Task<IReadOnlyList<TopBorrowerResult>> GetTopBorrowersAsync(
        DateTime from, DateTime to, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<CoBorrowedResult>> GetCoBorrowedBooksAsync(
        Guid bookId, int limit, CancellationToken cancellationToken);

    /// <summary>All loans of a given book by a given user, for the reading-pace calculation.</summary>
    Task<IReadOnlyList<Loan>> GetLoansAsync(Guid userId, Guid bookId, CancellationToken cancellationToken);
}

/// <summary>
/// Computes the borrowing insights on demand via Mongo aggregation pipelines. Every pipeline
/// applies the same "counted borrow" filter (see <see cref="InsightRules"/>) and joins the
/// referenced book/user in-database with <c>$lookup</c> so results come back hydrated in a
/// single round trip.
/// </summary>
public sealed class InsightRepository(LibraryDb db) : IInsightRepository
{
    // { $or: [ ReturnedAt == null, (ReturnedAt - BorrowedAt) >= 1 day ] }
    private static BsonDocument CountedBorrowExpr() => new("$or", new BsonArray
    {
        new BsonDocument("$eq", new BsonArray { "$ReturnedAt", BsonNull.Value }),
        new BsonDocument("$gte", new BsonArray
        {
            new BsonDocument("$subtract", new BsonArray { "$ReturnedAt", "$BorrowedAt" }),
            InsightRules.MinimumBorrowDurationMs,
        }),
    });

    public async Task<IReadOnlyList<MostBorrowedResult>> GetMostBorrowedBooksAsync(
        int limit, DateTime? from, DateTime? to, CancellationToken cancellationToken)
    {
        var pipeline = new List<BsonDocument>();
        AddWindowMatch(pipeline, from, to);
        pipeline.Add(new BsonDocument("$match", new BsonDocument("$expr", CountedBorrowExpr())));
        pipeline.Add(new BsonDocument("$group", new BsonDocument
        {
            { "_id", "$BookId" },
            { "count", new BsonDocument("$sum", 1) },
        }));
        AddSortLimitAndLookup(pipeline, limit, LibraryDb.BooksCollection, "book");

        var docs = await AggregateAsync(pipeline, cancellationToken);
        return docs
            .Select(d => new MostBorrowedResult(
                BsonSerializer.Deserialize<Book>(d["book"].AsBsonDocument),
                d["count"].ToInt64()))
            .ToList();
    }

    public async Task<IReadOnlyList<TopBorrowerResult>> GetTopBorrowersAsync(
        DateTime from, DateTime to, int limit, CancellationToken cancellationToken)
    {
        var pipeline = new List<BsonDocument>();
        AddWindowMatch(pipeline, from, to);
        pipeline.Add(new BsonDocument("$match", new BsonDocument("$expr", CountedBorrowExpr())));
        pipeline.Add(new BsonDocument("$group", new BsonDocument
        {
            { "_id", "$UserId" },
            { "count", new BsonDocument("$sum", 1) },
        }));
        AddSortLimitAndLookup(pipeline, limit, LibraryDb.UsersCollection, "user");

        var docs = await AggregateAsync(pipeline, cancellationToken);
        return docs
            .Select(d => new TopBorrowerResult(
                BsonSerializer.Deserialize<User>(d["user"].AsBsonDocument),
                d["count"].ToInt64()))
            .ToList();
    }

    public async Task<IReadOnlyList<CoBorrowedResult>> GetCoBorrowedBooksAsync(
        Guid bookId, int limit, CancellationToken cancellationToken)
    {
        var bookIdStr = bookId.ToString();
        var pipeline = new List<BsonDocument>
        {
            // Counted loans of the requested book...
            new("$match", new BsonDocument
            {
                { "BookId", bookIdStr },
                { "$expr", CountedBorrowExpr() },
            }),
            // ...collapse to the distinct set of users who borrowed it.
            new("$group", new BsonDocument
            {
                { "_id", BsonNull.Value },
                { "users", new BsonDocument("$addToSet", "$UserId") },
            }),
            // Pull every other counted loan made by those users.
            new("$lookup", new BsonDocument
            {
                { "from", LibraryDb.LoansCollection },
                { "let", new BsonDocument("users", "$users") },
                {
                    "pipeline", new BsonArray
                    {
                        new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
                        {
                            new BsonDocument("$in", new BsonArray { "$UserId", "$$users" }),
                            new BsonDocument("$ne", new BsonArray { "$BookId", bookIdStr }),
                            CountedBorrowExpr(),
                        }))),
                    }
                },
                { "as", "others" },
            }),
            new("$unwind", "$others"),
            // Rank other books by DISTINCT co-borrowers, so one heavy user can't skew a title.
            new("$group", new BsonDocument
            {
                { "_id", "$others.BookId" },
                { "coBorrowers", new BsonDocument("$addToSet", "$others.UserId") },
            }),
            new("$project", new BsonDocument("count", new BsonDocument("$size", "$coBorrowers"))),
        };
        AddSortLimitAndLookup(pipeline, limit, LibraryDb.BooksCollection, "book");

        var docs = await AggregateAsync(pipeline, cancellationToken);
        return docs
            .Select(d => new CoBorrowedResult(
                BsonSerializer.Deserialize<Book>(d["book"].AsBsonDocument),
                d["count"].ToInt64()))
            .ToList();
    }

    public async Task<IReadOnlyList<Loan>> GetLoansAsync(
        Guid userId, Guid bookId, CancellationToken cancellationToken) =>
        await db.Loans
            .Find(l => l.UserId == userId && l.BookId == bookId)
            .ToListAsync(cancellationToken);

    private static void AddWindowMatch(List<BsonDocument> pipeline, DateTime? from, DateTime? to)
    {
        if (from is null && to is null)
            return;

        var range = new BsonDocument();
        if (from is not null)
            range.Add("$gte", new BsonDateTime(DateTime.SpecifyKind(from.Value, DateTimeKind.Utc)));
        if (to is not null)
            range.Add("$lt", new BsonDateTime(DateTime.SpecifyKind(to.Value, DateTimeKind.Utc)));

        pipeline.Add(new BsonDocument("$match", new BsonDocument("BorrowedAt", range)));
    }

    // Shared tail: rank by count desc (id as a stable tie-break), take the top N, then join the
    // referenced document and flatten it. Orphan references (deleted book/user) are dropped by $unwind.
    private static void AddSortLimitAndLookup(
        List<BsonDocument> pipeline, int limit, string lookupCollection, string as_)
    {
        pipeline.Add(new BsonDocument("$sort", new BsonDocument { { "count", -1 }, { "_id", 1 } }));
        pipeline.Add(new BsonDocument("$limit", limit));
        pipeline.Add(new BsonDocument("$lookup", new BsonDocument
        {
            { "from", lookupCollection },
            { "localField", "_id" },
            { "foreignField", "_id" },
            { "as", as_ },
        }));
        pipeline.Add(new BsonDocument("$unwind", "$" + as_));
    }

    private async Task<List<BsonDocument>> AggregateAsync(
        List<BsonDocument> stages, CancellationToken cancellationToken)
    {
        PipelineDefinition<Loan, BsonDocument> pipeline = stages;
        using var cursor = await db.Loans.AggregateAsync(pipeline, cancellationToken: cancellationToken);
        return await cursor.ToListAsync(cancellationToken);
    }
}
