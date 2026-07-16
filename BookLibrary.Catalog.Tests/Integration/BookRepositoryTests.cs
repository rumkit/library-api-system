using BookLibrary.Catalog.Data;
using BookLibrary.Catalog.Data.Paging;

namespace BookLibrary.Catalog.Tests.Integration;

/// <summary>
/// Exercises <see cref="BookRepository"/> cursor validation against a real MongoDB
/// (Testcontainers). Requires Docker.
/// </summary>
public class BookRepositoryTests
{
    [ClassDataSource<MongoFixture>(Shared = SharedType.PerAssembly)]
    public required MongoFixture Mongo { get; init; }

    [Test]
    public async Task ListAsync_WhenCursorStructurallyInvalid_ShouldThrowInvalidCursorException()
    {
        var repo = new BookRepository(Mongo.NewDatabase());

        await Assert.ThrowsAsync<InvalidCursorException>(async () =>
            await repo.ListAsync(10, "not-a-cursor", CancellationToken.None));
    }
}
