using BookLibrary.Catalog.Domain;

namespace BookLibrary.Catalog.Insights;

/// <summary>Outcome of a reading-pace estimate. When <see cref="Computable"/> is false,
/// <see cref="Reason"/> explains why and <see cref="PagesPerDay"/> is 0.</summary>
public readonly record struct ReadingPaceResult(bool Computable, double PagesPerDay, string Reason)
{
    public static ReadingPaceResult NotComputable(string reason) => new(false, 0, reason);
    public static ReadingPaceResult Computed(double pagesPerDay) => new(true, pagesPerDay, string.Empty);
}

/// <summary>
/// Estimates a user's reading pace for a book, in pages per day, assuming continuous reading
/// over the loan period. Pure and side-effect free so every branch is unit-testable without a
/// database — this is the headline correctness surface of the insights.
/// </summary>
public static class ReadingPace
{
    /// <summary>
    /// Computes pace from a user's loans of a single <paramref name="book"/>. Uses the most
    /// recent <em>completed, counted</em> loan (returned after at least the minimum borrow
    /// duration). Falls back to explaining why no estimate is possible.
    /// </summary>
    public static ReadingPaceResult Compute(Book book, IReadOnlyCollection<Loan> loansOfBook)
    {
        if (loansOfBook.Count == 0)
            return ReadingPaceResult.NotComputable("No loan found for this user and book.");

        Loan? bestCompleted = null;
        var hasOpenLoan = false;

        foreach (var loan in loansOfBook)
        {
            if (loan.ReturnedAt is null)
            {
                hasOpenLoan = true;
                continue;
            }

            if (!InsightRules.IsCountedBorrow(loan.BorrowedAt, loan.ReturnedAt))
                continue; // returned same day — treated as not actually read

            if (bestCompleted is null || loan.ReturnedAt > bestCompleted.ReturnedAt)
                bestCompleted = loan;
        }

        if (bestCompleted is null)
        {
            return hasOpenLoan
                ? ReadingPaceResult.NotComputable("Loan is still open; return date unknown.")
                : ReadingPaceResult.NotComputable("Book was returned in under a day; treated as not read.");
        }

        if (book.PageCount <= 0)
            return ReadingPaceResult.NotComputable("Book has no positive page count.");

        var days = (bestCompleted.ReturnedAt!.Value - bestCompleted.BorrowedAt).TotalDays;
        return ReadingPaceResult.Computed(book.PageCount / days);
    }
}
