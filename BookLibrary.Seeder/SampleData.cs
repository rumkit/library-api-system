using BookLibrary.Catalog.Domain;

namespace BookLibrary.Seeder;

/// <summary>
/// Deterministic, fixed-seed demo data. Ids are derived from small integers so documents are
/// stable across runs and easy to reference from the README. The loan set is hand-crafted to
/// exercise every insight and every reading-pace branch:
/// normal completed loans, an open loan, a sub-1-day (excluded) loan, a zero-page book, and
/// enough co-borrow overlap for the borrowing-pattern query to be meaningful.
/// Dates are relative to <c>now</c> so the year-to-date default window is always populated.
/// </summary>
public static class SampleData
{
    private static Guid BookId(int n) => new($"00000000-0000-0000-0001-{n:D12}");
    private static Guid UserId(int n) => new($"00000000-0000-0000-0002-{n:D12}");
    private static Guid LoanId(int n) => new($"00000000-0000-0000-0003-{n:D12}");

    public static IReadOnlyList<Book> Books { get; } =
    [
        new() { Id = BookId(1), Title = "Clean Code", Author = "Robert C. Martin", PageCount = 464 },
        new() { Id = BookId(2), Title = "The Pragmatic Programmer", Author = "Andrew Hunt", PageCount = 352 },
        new() { Id = BookId(3), Title = "Domain-Driven Design", Author = "Eric Evans", PageCount = 560 },
        new() { Id = BookId(4), Title = "Refactoring", Author = "Martin Fowler", PageCount = 448 },
        new() { Id = BookId(5), Title = "The Mythical Man-Month", Author = "Fred Brooks", PageCount = 322 },
        // Zero pages on purpose: exercises the "no positive page count" reading-pace branch.
        new() { Id = BookId(6), Title = "Uncatalogued Zine", Author = "Anonymous", PageCount = 0 },
    ];

    public static IReadOnlyList<User> Users { get; } =
    [
        new() { Id = UserId(1), Name = "Alice" },
        new() { Id = UserId(2), Name = "Bob" },
        new() { Id = UserId(3), Name = "Carol" },
        new() { Id = UserId(4), Name = "Dave" },
    ];

    public static IReadOnlyList<Loan> BuildLoans(DateTime now)
    {
        // (loan#, user#, book#, daysAgoBorrowed, heldDays or null=open or 0=same-day)
        (int Loan, int User, int Book, int DaysAgo, double? Held)[] plan =
        [
            (1, 1, 1, 40, 10),    // Alice: Clean Code, 10 days  -> pace 46.4 p/d
            (2, 1, 2, 30, 5),     // Alice: Pragmatic, 5 days     -> pace 70.4 p/d
            (3, 1, 3, 12, null),  // Alice: DDD, still open       -> counts; pace "open"
            (4, 1, 6, 20, 4),     // Alice: zero-page zine        -> counts; pace "no pages"
            (5, 2, 1, 45, 20),    // Bob: Clean Code, 20 days     -> pace 23.2 p/d
            (6, 2, 4, 15, 0),     // Bob: Refactoring, same-day    -> EXCLUDED everywhere
            (7, 2, 2, 25, 8),     // Bob: Pragmatic, 8 days
            (8, 3, 1, 50, 15),    // Carol: Clean Code, 15 days
            (9, 3, 3, 35, 30),    // Carol: DDD, 30 days
            (10, 4, 1, 10, null), // Dave: Clean Code, still open  -> counts
            (11, 4, 5, 18, 3),    // Dave: Mythical Man-Month, 3 days
        ];

        return plan.Select(p =>
        {
            var borrowedAt = now.AddDays(-p.DaysAgo);
            DateTime? returnedAt = p.Held switch
            {
                null => null,                          // open loan
                0 => borrowedAt.AddHours(2),           // same-day return (excluded)
                _ => borrowedAt.AddDays(p.Held.Value), // completed
            };
            return new Loan
            {
                Id = LoanId(p.Loan),
                UserId = UserId(p.User),
                BookId = BookId(p.Book),
                BorrowedAt = borrowedAt,
                ReturnedAt = returnedAt,
            };
        }).ToList();
    }
}
