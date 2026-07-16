using BookLibrary.Catalog.Data.Paging;
using BookLibrary.Catalog.Domain;
using MongoDB.Driver;

namespace BookLibrary.Catalog.Data;

public interface IBookRepository
{
    Task<Book?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<Page<Book>> ListAsync(int limit, string? cursor, CancellationToken cancellationToken);
    Task CreateAsync(Book book, CancellationToken cancellationToken);

    /// <returns>false when no book with that id existed.</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class BookRepository(LibraryDb db) : IBookRepository
{
    public async Task<Book?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Books.Find(b => b.Id == id).FirstOrDefaultAsync(cancellationToken);

    public async Task<Page<Book>> ListAsync(int limit, string? cursor, CancellationToken cancellationToken)
    {
        var builder = Builders<Book>.Filter;
        var filter = FilterDefinition<Book>.Empty;

        if (!string.IsNullOrEmpty(cursor))
        {
            var (lastTitle, lastId) = Cursor.Decode(cursor);
            filter = builder.Or(
                builder.Gt(b => b.Title, lastTitle),
                builder.And(
                    builder.Eq(b => b.Title, lastTitle),
                    builder.Gt(b => b.Id, lastId)));
        }

        var items = await db.Books
            .Find(filter, ListingCollation.FindOptions)
            .SortBy(b => b.Title)
            .ThenBy(b => b.Id)
            .Limit(limit + 1)
            .ToListAsync(cancellationToken);

        if (items.Count <= limit)
            return new Page<Book>(items, null);

        var page = items[..limit];
        var last = page[^1];
        return new Page<Book>(page, Cursor.Encode(last.Title, last.Id));
    }

    public async Task CreateAsync(Book book, CancellationToken cancellationToken) =>
        await db.Books.InsertOneAsync(book, cancellationToken: cancellationToken);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await db.Books.DeleteOneAsync(b => b.Id == id, cancellationToken);
        return result.DeletedCount == 1;
    }
}
