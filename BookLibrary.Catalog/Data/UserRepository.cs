using BookLibrary.Catalog.Domain;
using MongoDB.Driver;

namespace BookLibrary.Catalog.Data;

public interface IUserRepository
{
    Task<User?> GetAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class UserRepository(LibraryDb db) : IUserRepository
{
    public async Task<User?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Users.Find(u => u.Id == id).FirstOrDefaultAsync(cancellationToken);
}
