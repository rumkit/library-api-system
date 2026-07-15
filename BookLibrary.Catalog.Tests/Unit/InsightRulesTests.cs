using BookLibrary.Catalog.Insights;

namespace BookLibrary.Catalog.Tests.Unit;

/// <summary>The counted-borrow rule that gates every insight.</summary>
public class InsightRulesTests
{
    private static readonly DateTime Borrowed = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task IsCountedBorrow_WhenOpenLoan_ShouldCount()
    {
        await Assert.That(InsightRules.IsCountedBorrow(Borrowed, returnedAt: null)).IsTrue();
    }

    [Test]
    [Arguments(24, true)]   // exactly one day counts
    [Arguments(25, true)]
    [Arguments(48, true)]
    [Arguments(23, false)]  // under a day is dropped
    [Arguments(1, false)]
    public async Task IsCountedBorrow_WhenReturned_ShouldRespectMinimumDuration(int heldHours, bool expected)
    {
        var counted = InsightRules.IsCountedBorrow(Borrowed, Borrowed.AddHours(heldHours));

        await Assert.That(counted).IsEqualTo(expected);
    }
}
