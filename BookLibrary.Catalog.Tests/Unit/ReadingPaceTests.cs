using BookLibrary.Catalog.Domain;
using BookLibrary.Catalog.Insights;

namespace BookLibrary.Catalog.Tests.Unit;

/// <summary>Covers every branch of the headline reading-pace calculation without a database.</summary>
public class ReadingPaceTests
{
    private static readonly DateTime Borrowed = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Book BookWith(int pageCount) =>
        new() { Id = Guid.NewGuid(), Title = "T", Author = "A", PageCount = pageCount };

    private static Loan Loan(DateTime borrowedAt, DateTime? returnedAt) =>
        new() { Id = Guid.NewGuid(), BookId = Guid.NewGuid(), UserId = Guid.NewGuid(), BorrowedAt = borrowedAt, ReturnedAt = returnedAt };

    [Test]
    public async Task Compute_WhenNoLoans_ShouldNotBeComputable()
    {
        var result = ReadingPace.Compute(BookWith(300), []);

        await Assert.That(result.Computable).IsFalse();
        await Assert.That(result.Reason).Contains("No loan");
    }

    [Test]
    public async Task Compute_WhenCompletedLoan_ShouldReturnPagesPerDay()
    {
        var loan = Loan(Borrowed, Borrowed.AddDays(10));

        var result = ReadingPace.Compute(BookWith(300), [loan]);

        await Assert.That(result.Computable).IsTrue();
        await Assert.That(result.PagesPerDay).IsEqualTo(30d).Within(1e-9); // 300 pages / 10 days
    }

    [Test]
    public async Task Compute_WhenLoanStillOpen_ShouldNotBeComputable()
    {
        var loan = Loan(Borrowed, returnedAt: null);

        var result = ReadingPace.Compute(BookWith(300), [loan]);

        await Assert.That(result.Computable).IsFalse();
        await Assert.That(result.Reason).Contains("open");
    }

    [Test]
    public async Task Compute_WhenReturnedSameDay_ShouldNotBeComputable()
    {
        var loan = Loan(Borrowed, Borrowed.AddHours(3)); // under the 1-day minimum

        var result = ReadingPace.Compute(BookWith(300), [loan]);

        await Assert.That(result.Computable).IsFalse();
        await Assert.That(result.Reason).Contains("under a day");
    }

    [Test]
    public async Task Compute_WhenPageCountNotPositive_ShouldNotBeComputable()
    {
        var loan = Loan(Borrowed, Borrowed.AddDays(5));

        var result = ReadingPace.Compute(BookWith(0), [loan]);

        await Assert.That(result.Computable).IsFalse();
        await Assert.That(result.Reason).Contains("page count");
    }

    [Test]
    public async Task Compute_WhenMultipleCompletedLoans_ShouldUseMostRecentReturn()
    {
        var older = Loan(Borrowed, Borrowed.AddDays(10));                       // 30 p/d
        var newer = Loan(Borrowed.AddDays(20), Borrowed.AddDays(20).AddDays(5)); // 60 p/d, returned later

        var result = ReadingPace.Compute(BookWith(300), [older, newer]);

        await Assert.That(result.Computable).IsTrue();
        await Assert.That(result.PagesPerDay).IsEqualTo(60d).Within(1e-9);
    }

    [Test]
    public async Task Compute_WhenOpenAndCompletedPresent_ShouldUseCompleted()
    {
        var open = Loan(Borrowed.AddDays(30), returnedAt: null);
        var completed = Loan(Borrowed, Borrowed.AddDays(6)); // 50 p/d

        var result = ReadingPace.Compute(BookWith(300), [open, completed]);

        await Assert.That(result.Computable).IsTrue();
        await Assert.That(result.PagesPerDay).IsEqualTo(50d).Within(1e-9);
    }
}
