using BookLibrary.Catalog.Data;
using BookLibrary.Catalog.Data.Paging;
using BookLibrary.Catalog.Domain;
using MongoDB.Driver;

namespace BookLibrary.Catalog.Tests.Integration;

/// <summary>
/// Exercises <see cref="LoanRepository"/> against a real MongoDB (Testcontainers), in particular
/// the unique-partial-index double-loan guard and cursor paging. Requires Docker. The
/// <see cref="MongoIndexInitializer"/> hosted service does not run for these tests, so indexes are
/// created explicitly in <see cref="NewRepoAsync"/> — otherwise the duplicate-key tests would
/// silently pass for the wrong reason (no index enforcing anything).
/// </summary>
public class LoanRepositoryTests
{
    [ClassDataSource<MongoFixture>(Shared = SharedType.PerAssembly)]
    public required MongoFixture Mongo { get; init; }

    private static Guid B(int n) => new($"0000000a-0000-0000-0000-{n:D12}");
    private static Guid U(int n) => new($"0000000b-0000-0000-0000-{n:D12}");

    private static Loan Loan(Guid id, int book, int user, DateTime borrowedAt, DateTime? returnedAt = null) =>
        new()
        {
            Id = id,
            BookId = B(book), BookTitle = $"Book {book}", BookAuthor = "A",
            UserId = U(user), UserName = $"User {user}",
            BorrowedAt = borrowedAt, ReturnedAt = returnedAt,
        };

    private async Task<LoanRepository> NewRepoAsync()
    {
        var db = Mongo.NewDatabase();
        var keys = Builders<Loan>.IndexKeys;
        await db.Loans.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<Loan>(
                keys.Ascending(l => l.BookId),
                new CreateIndexOptions<Loan>
                {
                    Name = "ux_loans_open_book",
                    Unique = true,
                    PartialFilterExpression = Builders<Loan>.Filter.Eq(l => l.ReturnedAt, null),
                }),
            new CreateIndexModel<Loan>(keys.Ascending(l => l.BorrowedAt).Ascending(l => l.Id)),
        ]);
        return new LoanRepository(db);
    }

    [Test]
    public async Task CreateAsync_WhenBookHasNoOpenLoan_ShouldInsert()
    {
        var repo = await NewRepoAsync();

        var (outcome, loan) = await repo.CreateAsync(Loan(Guid.NewGuid(), 1, 1, DateTime.UtcNow), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(CreateLoanOutcome.Created);
        await Assert.That(loan).IsNotNull();
    }

    [Test]
    public async Task CreateAsync_WhenBookAlreadyOnLoan_ShouldReturnBookAlreadyOnLoan()
    {
        var repo = await NewRepoAsync();
        await repo.CreateAsync(Loan(Guid.NewGuid(), 1, 1, DateTime.UtcNow), CancellationToken.None);

        var (outcome, loan) = await repo.CreateAsync(Loan(Guid.NewGuid(), 1, 2, DateTime.UtcNow), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(CreateLoanOutcome.BookAlreadyOnLoan);
        await Assert.That(loan).IsNull();
    }

    [Test]
    public async Task CreateAsync_WhenPreviousLoanReturned_ShouldInsert()
    {
        var repo = await NewRepoAsync();
        await repo.CreateAsync(
            Loan(Guid.NewGuid(), 1, 1, DateTime.UtcNow.AddDays(-5), DateTime.UtcNow.AddDays(-1)),
            CancellationToken.None);

        var (outcome, loan) = await repo.CreateAsync(Loan(Guid.NewGuid(), 1, 2, DateTime.UtcNow), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(CreateLoanOutcome.Created);
        await Assert.That(loan).IsNotNull();
    }

    [Test]
    public async Task CreateAsync_WhenSameBookConcurrently_ShouldInsertExactlyOne()
    {
        var repo = await NewRepoAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => repo.CreateAsync(Loan(Guid.NewGuid(), 1, i, DateTime.UtcNow), CancellationToken.None)));

        await Assert.That(results.Count(r => r.Outcome == CreateLoanOutcome.Created)).IsEqualTo(1);
        await Assert.That(results.Count(r => r.Outcome == CreateLoanOutcome.BookAlreadyOnLoan)).IsEqualTo(7);
    }

    [Test]
    public async Task CloseAsync_WhenOpen_ShouldSetReturnedAtAndReturnLoan()
    {
        var repo = await NewRepoAsync();
        var id = Guid.NewGuid();
        await repo.CreateAsync(Loan(id, 1, 1, DateTime.UtcNow.AddDays(-1)), CancellationToken.None);
        // Mongo stores DateTime at millisecond precision, so truncate before comparing.
        var returnedAt = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

        var closed = await repo.CloseAsync(id, returnedAt, CancellationToken.None);

        await Assert.That(closed).IsNotNull();
        await Assert.That(closed!.ReturnedAt).IsEqualTo(returnedAt);
    }

    [Test]
    public async Task CloseAsync_WhenAlreadyClosed_ShouldReturnNull()
    {
        var repo = await NewRepoAsync();
        var id = Guid.NewGuid();
        await repo.CreateAsync(
            Loan(id, 1, 1, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddHours(-1)), CancellationToken.None);

        var closed = await repo.CloseAsync(id, DateTime.UtcNow, CancellationToken.None);

        await Assert.That(closed).IsNull();
    }

    [Test]
    public async Task CloseAsync_WhenConcurrent_ShouldCloseOnlyOnce()
    {
        var repo = await NewRepoAsync();
        var id = Guid.NewGuid();
        await repo.CreateAsync(Loan(id, 1, 1, DateTime.UtcNow.AddDays(-1)), CancellationToken.None);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => repo.CloseAsync(id, DateTime.UtcNow, CancellationToken.None)));

        await Assert.That(results.Count(r => r is not null)).IsEqualTo(1);
    }

    [Test]
    public async Task CloseOpenByBookAsync_ShouldCloseOnlyOpenLoansOfThatBook()
    {
        var repo = await NewRepoAsync();
        var openForBook1 = Guid.NewGuid();
        await repo.CreateAsync(Loan(openForBook1, 1, 1, DateTime.UtcNow.AddDays(-1)), CancellationToken.None);
        var closedForBook1 = Guid.NewGuid();
        await repo.CreateAsync(
            Loan(closedForBook1, 1, 2, DateTime.UtcNow.AddDays(-10), DateTime.UtcNow.AddDays(-5)),
            CancellationToken.None);
        var openForBook2 = Guid.NewGuid();
        await repo.CreateAsync(Loan(openForBook2, 2, 3, DateTime.UtcNow.AddDays(-1)), CancellationToken.None);

        var count = await repo.CloseOpenByBookAsync(B(1), DateTime.UtcNow, CancellationToken.None);

        await Assert.That(count).IsEqualTo(1);
        var book2Loan = await repo.GetAsync(openForBook2, CancellationToken.None);
        await Assert.That(book2Loan!.ReturnedAt).IsNull();
    }

    [Test]
    public async Task CountOpenByUserAsync_ShouldCountOnlyOpenLoans()
    {
        var repo = await NewRepoAsync();
        await repo.CreateAsync(Loan(Guid.NewGuid(), 1, 1, DateTime.UtcNow.AddDays(-1)), CancellationToken.None);
        await repo.CreateAsync(Loan(Guid.NewGuid(), 2, 1, DateTime.UtcNow.AddDays(-1)), CancellationToken.None);
        await repo.CreateAsync(
            Loan(Guid.NewGuid(), 3, 1, DateTime.UtcNow.AddDays(-10), DateTime.UtcNow.AddDays(-5)),
            CancellationToken.None);

        var count = await repo.CountOpenByUserAsync(U(1), CancellationToken.None);

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task ListAsync_WhenPagingWithCursor_ShouldReturnEachLoanExactlyOnce()
    {
        var repo = await NewRepoAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 25; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            await repo.CreateAsync(Loan(id, i, i, DateTime.UtcNow.AddMinutes(-i)), CancellationToken.None);
        }

        var seen = new HashSet<Guid>();
        string? cursor = null;
        Page<Loan>? page = null;
        for (var i = 0; i < 10 && (page is null || page.NextCursor is not null); i++)
        {
            page = await repo.ListAsync(10, cursor, new LoanFilter(null, null, false), CancellationToken.None);
            foreach (var loan in page.Items)
                seen.Add(loan.Id);
            cursor = page.NextCursor;
        }

        await Assert.That(seen.Count).IsEqualTo(25);
        await Assert.That(seen.SetEquals(ids)).IsTrue();
        await Assert.That(page!.NextCursor).IsNull();
    }

    [Test]
    public async Task ListAsync_WhenLoansShareBorrowedAt_ShouldStillPageWithoutDuplicates()
    {
        var repo = await NewRepoAsync();
        var shared = DateTime.UtcNow;
        var ids = new List<Guid>();
        for (var i = 0; i < 15; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            await repo.CreateAsync(Loan(id, i, i, shared), CancellationToken.None);
        }

        var seen = new HashSet<Guid>();
        string? cursor = null;
        Page<Loan>? page = null;
        for (var i = 0; i < 10 && (page is null || page.NextCursor is not null); i++)
        {
            page = await repo.ListAsync(5, cursor, new LoanFilter(null, null, false), CancellationToken.None);
            foreach (var loan in page.Items)
                seen.Add(loan.Id);
            cursor = page.NextCursor;
        }

        await Assert.That(seen.Count).IsEqualTo(15);
        await Assert.That(seen.SetEquals(ids)).IsTrue();
    }
}
