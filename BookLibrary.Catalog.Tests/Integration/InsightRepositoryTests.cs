using BookLibrary.Catalog.Data;
using BookLibrary.Catalog.Domain;

namespace BookLibrary.Catalog.Tests.Integration;

/// <summary>
/// Exercises the aggregation pipelines directly against a real MongoDB (Testcontainers). Requires
/// Docker. The counted-borrow filter, the co-borrow self-exclusion and the window boundaries are
/// all behaviours that only a real server validates, so they live here rather than in unit tests.
/// </summary>
public class InsightRepositoryTests
{
    [ClassDataSource<MongoFixture>(Shared = SharedType.PerAssembly)]
    public required MongoFixture Mongo { get; init; }

    private static readonly DateTime Recent = DateTime.UtcNow.AddMonths(-1);

    // Fixed, readable ids so assertions can name the expected book/user.
    private static Guid B(int n) => new($"0000000a-0000-0000-0000-{n:D12}");
    private static Guid U(int n) => new($"0000000b-0000-0000-0000-{n:D12}");

    private static Book Book(int n) => new() { Id = B(n), Title = $"Book {n}", Author = "A", PageCount = 100 };
    private static User User(int n) => new() { Id = U(n), Name = $"User {n}" };

    private static Loan Loan(int user, int book, DateTime borrowedAt, DateTime? returnedAt) =>
        new() { Id = Guid.NewGuid(), UserId = U(user), BookId = B(book), BorrowedAt = borrowedAt, ReturnedAt = returnedAt };

    private async Task<(LibraryDb Db, InsightRepository Repo)> SeedAsync(
        IEnumerable<Book> books, IEnumerable<User> users, IEnumerable<Loan> loans)
    {
        var db = Mongo.NewDatabase();
        await db.Books.InsertManyAsync(books);
        await db.Users.InsertManyAsync(users);
        await db.Loans.InsertManyAsync(loans);
        return (db, new InsightRepository(db));
    }

    // Dataset shared by the ranking tests:
    //   A(1): U1,U2,U3 counted           B(2): U1,U2 counted
    //   C(3): U1 open (counts)           D(4): U2 same-day (excluded)
    private async Task<InsightRepository> SeedRankingDataAsync()
    {
        var loans = new[]
        {
            Loan(1, 1, Recent, Recent.AddDays(5)),
            Loan(1, 2, Recent, Recent.AddDays(3)),
            Loan(1, 3, Recent, null),                    // open -> counts
            Loan(2, 1, Recent, Recent.AddDays(10)),
            Loan(2, 2, Recent, Recent.AddDays(2)),
            Loan(2, 4, Recent, Recent.AddHours(2)),      // same-day -> excluded
            Loan(3, 1, Recent, Recent.AddDays(4)),
        };
        var (_, repo) = await SeedAsync(
            [Book(1), Book(2), Book(3), Book(4)], [User(1), User(2), User(3)], loans);
        return repo;
    }

    [Test]
    public async Task GetMostBorrowedBooks_ShouldRankByCountedBorrowsAndExcludeSameDay()
    {
        var repo = await SeedRankingDataAsync();

        var result = await repo.GetMostBorrowedBooksAsync(limit: 10, from: null, to: null, CancellationToken.None);

        await Assert.That(result.Select(r => r.Book.Id)).IsEquivalentTo(new[] { B(1), B(2), B(3) });
        await Assert.That(result[0].Book.Id).IsEqualTo(B(1));
        await Assert.That(result[0].BorrowCount).IsEqualTo(3);
        // Book D was only ever borrowed for under a day, so it never appears.
        await Assert.That(result.Any(r => r.Book.Id == B(4))).IsFalse();
    }

    [Test]
    public async Task GetTopBorrowers_ShouldRankUsersByCountedBorrows()
    {
        var repo = await SeedRankingDataAsync();

        var result = await repo.GetTopBorrowersAsync(
            from: Recent.AddYears(-1), to: Recent.AddYears(1), limit: 10, CancellationToken.None);

        await Assert.That(result[0].User.Id).IsEqualTo(U(1));
        await Assert.That(result[0].BorrowCount).IsEqualTo(3);
        await Assert.That(result.Single(r => r.User.Id == U(2)).BorrowCount).IsEqualTo(2); // D excluded
        await Assert.That(result.Single(r => r.User.Id == U(3)).BorrowCount).IsEqualTo(1);
    }

    [Test]
    public async Task GetCoBorrowedBooks_ShouldRankByDistinctCoBorrowersExcludingTheBookItself()
    {
        var repo = await SeedRankingDataAsync();

        // Borrowers of A are U1,U2,U3; their other counted borrows are B (U1,U2) and C (U1).
        var result = await repo.GetCoBorrowedBooksAsync(B(1), limit: 10, CancellationToken.None);

        await Assert.That(result.Select(r => r.Book.Id)).IsEquivalentTo(new[] { B(2), B(3) });
        await Assert.That(result[0].Book.Id).IsEqualTo(B(2));
        await Assert.That(result[0].CoBorrowerCount).IsEqualTo(2);
        await Assert.That(result.Single(r => r.Book.Id == B(3)).CoBorrowerCount).IsEqualTo(1);
        await Assert.That(result.Any(r => r.Book.Id == B(1))).IsFalse(); // never itself
    }

    [Test]
    public async Task GetMostBorrowedBooks_ShouldOnlyCountBorrowsInsideTheWindow()
    {
        // U1 borrowed A in January and in June; U2 borrowed A in June only.
        var jan = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var jun = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        var loans = new[]
        {
            Loan(1, 1, jan, jan.AddDays(3)),
            Loan(1, 1, jun, jun.AddDays(3)),
            Loan(2, 1, jun, jun.AddDays(3)),
        };
        var (_, repo) = await SeedAsync([Book(1)], [User(1), User(2)], loans);

        var june = await repo.GetMostBorrowedBooksAsync(
            limit: 10,
            from: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            to: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        await Assert.That(june).HasSingleItem();
        await Assert.That(june[0].BorrowCount).IsEqualTo(2); // the January borrow is outside the window
    }

    [Test]
    public async Task GetLoans_ShouldReturnOnlyLoansForThatUserAndBook()
    {
        var loans = new[]
        {
            Loan(1, 1, Recent, Recent.AddDays(5)),
            Loan(1, 2, Recent, Recent.AddDays(5)), // different book
            Loan(2, 1, Recent, Recent.AddDays(5)), // different user
        };
        var (_, repo) = await SeedAsync([Book(1), Book(2)], [User(1), User(2)], loans);

        var result = await repo.GetLoansAsync(U(1), B(1), CancellationToken.None);

        await Assert.That(result).HasSingleItem();
        await Assert.That(result[0].UserId).IsEqualTo(U(1));
        await Assert.That(result[0].BookId).IsEqualTo(B(1));
    }
}
