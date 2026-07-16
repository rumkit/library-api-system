using BookLibrary.Catalog.Data;
using BookLibrary.Catalog.Data.Paging;

namespace BookLibrary.Catalog.Tests.Integration;

/// <summary>
/// Exercises <see cref="UserRepository"/> cursor validation against a real MongoDB
/// (Testcontainers). Requires Docker.
/// </summary>
public class UserRepositoryTests
{
    [ClassDataSource<MongoFixture>(Shared = SharedType.PerAssembly)]
    public required MongoFixture Mongo { get; init; }

    [Test]
    public async Task ListAsync_WhenCursorStructurallyInvalid_ShouldThrowInvalidCursorException()
    {
        var repo = new UserRepository(Mongo.NewDatabase());

        await Assert.ThrowsAsync<InvalidCursorException>(async () =>
            await repo.ListAsync(10, "not-a-cursor", CancellationToken.None));
    }
}
