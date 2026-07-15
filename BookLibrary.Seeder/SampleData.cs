using System.Reflection;
using System.Text.Json;
using BookLibrary.Catalog.Domain;

namespace BookLibrary.Seeder;

/// <summary>
/// Deterministic demo data loaded from the embedded JSON files under <c>Data/</c>
/// (<c>books.json</c>, <c>users.json</c>, <c>loans.json</c>). Books and users are fixed records;
/// loans are stored as <em>offsets</em> (days ago borrowed + days held) rather than absolute dates
/// and resolved against <c>now</c> at seed time, so the year-to-date default window is always
/// populated. Each loan snapshots the referenced book's title/author and the user's name at borrow
/// time (see <see cref="Loan"/>), copied here from the seeded books/users so the snapshot always
/// matches the seeded catalogue.
/// <para>
/// The dataset is hand-shaped so the insight system tests have stable answers: "Clean Code" is the
/// most-borrowed book, "Alice" the top borrower, Alice has exactly one (10-day, 464-page) Clean Code
/// loan for the reading-pace check, and Clean Code's co-borrowers also borrow Pragmatic and DDD.
/// </para>
/// </summary>
public static class SampleData
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<Book> Books { get; } = Load<Book>("books.json");

    public static IReadOnlyList<User> Users { get; } = Load<User>("users.json");

    /// <summary>
    /// Materialises the loan plan into concrete <see cref="Loan"/>s relative to <paramref name="now"/>,
    /// resolving each loan's book/user snapshot from <see cref="Books"/>/<see cref="Users"/>.
    /// </summary>
    public static IReadOnlyList<Loan> BuildLoans(DateTime now)
    {
        var booksById = Books.ToDictionary(b => b.Id);
        var usersById = Users.ToDictionary(u => u.Id);

        return Load<LoanSeed>("loans.json").Select((seed, i) =>
        {
            var borrowedAt = now.AddDays(-seed.DaysAgo);
            DateTime? returnedAt = seed.Held switch
            {
                null => null,                           // open loan
                0 => borrowedAt.AddHours(2),            // same-day return (excluded)
                _ => borrowedAt.AddDays(seed.Held.Value), // completed
            };
            var book = booksById[seed.BookId];
            var user = usersById[seed.UserId];
            return new Loan
            {
                Id = LoanId(i + 1),
                UserId = user.Id,
                UserName = user.Name,
                BookId = book.Id,
                BookTitle = book.Title,
                BookAuthor = book.Author,
                BorrowedAt = borrowedAt,
                ReturnedAt = returnedAt,
            };
        }).ToList();
    }

    private static Guid LoanId(int n) => new($"00000000-0000-0000-0003-{n:D12}");

    private static IReadOnlyList<T> Load<T>(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = $"{assembly.GetName().Name}.Data.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded seed resource '{resource}' not found.");
        return JsonSerializer.Deserialize<List<T>>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"Seed resource '{resource}' deserialized to null.");
    }

    /// <summary>Offset-based loan record as stored in <c>loans.json</c> (see class remarks).</summary>
    private sealed record LoanSeed(Guid BookId, Guid UserId, int DaysAgo, int? Held);
}
