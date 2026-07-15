using BookLibrary.Catalog.Domain;
using MongoDB.Driver;

namespace BookLibrary.Catalog.Data;

public interface IBookRepository
{
    Task<Book?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<Book>> ListAsync(int limit, int skip, CancellationToken cancellationToken);
}

public sealed class BookRepository(LibraryDb db) : IBookRepository
{
    public async Task<Book?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Books.Find(b => b.Id == id).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<Book>> ListAsync(int limit, int skip, CancellationToken cancellationToken) =>
        await db.Books
            .Find(FilterDefinition<Book>.Empty)
            .SortBy(b => b.Title)
            .Skip(skip)
            .Limit(limit)
            .ToListAsync(cancellationToken);
}
