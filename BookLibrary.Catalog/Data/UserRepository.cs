using BookLibrary.Catalog.Data.Paging;
using BookLibrary.Catalog.Domain;
using MongoDB.Driver;

namespace BookLibrary.Catalog.Data;

public interface IUserRepository
{
    Task<User?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<Page<User>> ListAsync(int limit, string? cursor, CancellationToken cancellationToken);
    Task CreateAsync(User user, CancellationToken cancellationToken);

    /// <returns>The updated user, or null if no user with that id existed.</returns>
    Task<User?> UpdateNameAsync(Guid id, string name, CancellationToken cancellationToken);

    /// <returns>false when no user with that id existed.</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class UserRepository(LibraryDb db) : IUserRepository
{
    public async Task<User?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Users.Find(u => u.Id == id).FirstOrDefaultAsync(cancellationToken);

    public async Task<Page<User>> ListAsync(int limit, string? cursor, CancellationToken cancellationToken)
    {
        var builder = Builders<User>.Filter;
        var filter = FilterDefinition<User>.Empty;

        if (!string.IsNullOrEmpty(cursor))
        {
            var (lastName, lastId) = Cursor.Decode(cursor);
            filter = builder.Or(
                builder.Gt(u => u.Name, lastName),
                builder.And(
                    builder.Eq(u => u.Name, lastName),
                    builder.Gt(u => u.Id, lastId)));
        }

        var items = await db.Users
            .Find(filter, ListingCollation.FindOptions)
            .SortBy(u => u.Name)
            .ThenBy(u => u.Id)
            .Limit(limit + 1)
            .ToListAsync(cancellationToken);

        if (items.Count <= limit)
            return new Page<User>(items, null);

        var page = items[..limit];
        var last = page[^1];
        return new Page<User>(page, Cursor.Encode(last.Name, last.Id));
    }

    public async Task CreateAsync(User user, CancellationToken cancellationToken) =>
        await db.Users.InsertOneAsync(user, cancellationToken: cancellationToken);

    public async Task<User?> UpdateNameAsync(Guid id, string name, CancellationToken cancellationToken) =>
        await db.Users.FindOneAndUpdateAsync(
            u => u.Id == id,
            Builders<User>.Update.Set(u => u.Name, name),
            new FindOneAndUpdateOptions<User> { ReturnDocument = ReturnDocument.After },
            cancellationToken);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await db.Users.DeleteOneAsync(u => u.Id == id, cancellationToken);
        return result.DeletedCount == 1;
    }
}
