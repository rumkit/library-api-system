using BookLibrary.Catalog.Data;
using BookLibrary.Catalog.Data.Paging;
using BookLibrary.Catalog.Domain;

namespace BookLibrary.Catalog.Tests.Integration;

/// <summary>
/// Exercises <see cref="UserRepository"/> cursor validation, case-insensitive listing order and
/// keyset paging against a real MongoDB (Testcontainers). Requires Docker.
/// </summary>
public class UserRepositoryTests
{
    [ClassDataSource<MongoFixture>(Shared = SharedType.PerAssembly)]
    public required MongoFixture Mongo { get; init; }

    private static User NewUser(string name) => new() { Id = Guid.NewGuid(), Name = name };

    [Test]
    public async Task ListAsync_WhenCursorStructurallyInvalid_ShouldThrowInvalidCursorException()
    {
        var repo = new UserRepository(Mongo.NewDatabase());

        await Assert.ThrowsAsync<InvalidCursorException>(async () =>
            await repo.ListAsync(10, "not-a-cursor", CancellationToken.None));
    }

    [Test]
    public async Task ListAsync_WhenNamesMixedCase_ShouldOrderCaseInsensitively()
    {
        var db = Mongo.NewDatabase();
        var repo = new UserRepository(db);
        User[] users = [NewUser("cherry"), NewUser("apple"), NewUser("Zebra"), NewUser("Banana")];
        await db.Users.InsertManyAsync(users);

        var page = await repo.ListAsync(10, null, CancellationToken.None);

        await Assert.That(page.Items.Select(u => u.Name)).IsEquivalentTo(["apple", "Banana", "cherry", "Zebra"]);
    }

    [Test]
    public async Task ListAsync_WhenUsersShareNameCaseInsensitively_ShouldPageWithoutDuplicates()
    {
        var db = Mongo.NewDatabase();
        var repo = new UserRepository(db);
        User[] users =
        [
            NewUser("apple"), NewUser("Apple"), NewUser("APPLE"),
            NewUser("Banana"), NewUser("banana"),
        ];
        await db.Users.InsertManyAsync(users);

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await repo.ListAsync(1, cursor, CancellationToken.None);
            seen.AddRange(page.Items.Select(u => u.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);

        await Assert.That(seen.Count).IsEqualTo(users.Length);
        await Assert.That(seen.Distinct().Count()).IsEqualTo(users.Length);
        await Assert.That(seen.ToHashSet()).IsEquivalentTo(users.Select(u => u.Id).ToHashSet());
    }
}
