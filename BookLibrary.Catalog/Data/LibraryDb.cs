using BookLibrary.Catalog.Domain;
using MongoDB.Driver;

namespace BookLibrary.Catalog.Data;

/// <summary>
/// Strongly-typed access point for the library's Mongo collections. A thin wrapper over
/// <see cref="IMongoDatabase"/> so collection names live in one place and callers depend on a
/// small surface rather than raw string lookups.
/// </summary>
public sealed class LibraryDb
{
    public const string BooksCollection = "Books";
    public const string UsersCollection = "Users";
    public const string LoansCollection = "Loans";

    public LibraryDb(IMongoDatabase database)
    {
        Books = database.GetCollection<Book>(BooksCollection);
        Users = database.GetCollection<User>(UsersCollection);
        Loans = database.GetCollection<Loan>(LoansCollection);
    }

    public IMongoCollection<Book> Books { get; }

    public IMongoCollection<User> Users { get; }

    public IMongoCollection<Loan> Loans { get; }
}
