using BookLibrary.Catalog.Data;
using BookLibrary.Catalog.Data.Paging;
using BookLibrary.Catalog.Domain;

namespace BookLibrary.Catalog.Tests.Integration;

/// <summary>
/// Exercises <see cref="BookRepository"/> cursor validation, case-insensitive listing order and
/// keyset paging against a real MongoDB (Testcontainers). Requires Docker.
/// </summary>
public class BookRepositoryTests
{
    [ClassDataSource<MongoFixture>(Shared = SharedType.PerAssembly)]
    public required MongoFixture Mongo { get; init; }

    private static Book NewBook(string title) =>
        new() { Id = Guid.NewGuid(), Title = title, Author = "A", PageCount = 100, Year = 2000 };

    [Test]
    public async Task ListAsync_WhenCursorStructurallyInvalid_ShouldThrowInvalidCursorException()
    {
        var repo = new BookRepository(Mongo.NewDatabase());

        await Assert.ThrowsAsync<InvalidCursorException>(async () =>
            await repo.ListAsync(10, "not-a-cursor", CancellationToken.None));
    }

    [Test]
    public async Task ListAsync_WhenTitlesMixedCase_ShouldOrderCaseInsensitively()
    {
        var db = Mongo.NewDatabase();
        var repo = new BookRepository(db);
        Book[] books = [NewBook("cherry"), NewBook("apple"), NewBook("Zebra"), NewBook("Banana")];
        await db.Books.InsertManyAsync(books);

        var page = await repo.ListAsync(10, null, CancellationToken.None);

        await Assert.That(page.Items.Select(b => b.Title)).IsEquivalentTo(["apple", "Banana", "cherry", "Zebra"]);
    }

    [Test]
    public async Task ListAsync_WhenBooksShareTitleCaseInsensitively_ShouldPageWithoutDuplicates()
    {
        var db = Mongo.NewDatabase();
        var repo = new BookRepository(db);
        Book[] books =
        [
            NewBook("apple"), NewBook("Apple"), NewBook("APPLE"),
            NewBook("Banana"), NewBook("banana"),
        ];
        await db.Books.InsertManyAsync(books);

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await repo.ListAsync(1, cursor, CancellationToken.None);
            seen.AddRange(page.Items.Select(b => b.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);

        await Assert.That(seen.Count).IsEqualTo(books.Length);
        await Assert.That(seen.Distinct().Count()).IsEqualTo(books.Length);
        await Assert.That(seen.ToHashSet()).IsEquivalentTo(books.Select(b => b.Id).ToHashSet());
    }
}
