using BookLibrary.Catalog.Data;
using BookLibrary.Contracts;
using Grpc.Core;
using Grpc.Net.Client;
using MongoDB.Driver;
using Book = BookLibrary.Catalog.Domain.Book;
using User = BookLibrary.Catalog.Domain.User;
using Loan = BookLibrary.Catalog.Domain.Loan;

namespace BookLibrary.Catalog.Tests.Functional;

/// <summary>
/// Drives the gRPC service surface end-to-end (validation, status codes, mapping and behaviour)
/// against the in-memory host and a real Mongo. Requires Docker.
/// </summary>
public class CatalogServiceTests
{
    [ClassDataSource<MongoFixture>(Shared = SharedType.PerAssembly)]
    public required MongoFixture Mongo { get; init; }

    private static readonly DateTime Recent = DateTime.UtcNow.AddMonths(-1);
    private static Guid B(int n) => new($"0000000c-0000-0000-0000-{n:D12}");
    private static Guid U(int n) => new($"0000000d-0000-0000-0000-{n:D12}");

    // Starts a host wired to a fresh database seeded by <paramref name="seed"/>. The returned
    // factory owns the host and must be disposed by the caller.
    private async Task<(CatalogService.CatalogServiceClient Client, CatalogApiFactory Factory)> StartAsync(
        Func<LibraryDb, Task>? seed = null)
    {
        var dbName = "func_" + Guid.NewGuid().ToString("N");
        if (seed is not null)
        {
            var db = new LibraryDb(new MongoClient(Mongo.ConnectionString).GetDatabase(dbName));
            await seed(db);
        }

        var factory = new CatalogApiFactory(Mongo.ConnectionString, dbName);
        var channel = GrpcChannel.ForAddress(
            factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
        return (new CatalogService.CatalogServiceClient(channel), factory);
    }

    [Test]
    public async Task GetBook_WhenBookExists_ShouldReturnMappedBook()
    {
        var (client, factory) = await StartAsync(db => db.Books.InsertOneAsync(
            new Book { Id = B(1), Title = "Clean Code", Author = "Martin", PageCount = 464 }));
        using (factory)
        {
            var book = await client.GetBookAsync(new GetBookRequest { Id = B(1).ToString() });

            await Assert.That(book.Id).IsEqualTo(B(1).ToString());
            await Assert.That(book.Title).IsEqualTo("Clean Code");
            await Assert.That(book.PageCount).IsEqualTo(464);
        }
    }

    [Test]
    public async Task GetBook_WhenBookMissing_ShouldThrowNotFound()
    {
        var (client, factory) = await StartAsync();
        using (factory)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.GetBookAsync(new GetBookRequest { Id = B(99).ToString() }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.NotFound);
        }
    }

    [Test]
    public async Task GetBook_WhenIdMalformed_ShouldThrowInvalidArgument()
    {
        var (client, factory) = await StartAsync();
        using (factory)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.GetBookAsync(new GetBookRequest { Id = "not-a-guid" }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        }
    }

    [Test]
    public async Task ListBooks_WhenLimitNotPositive_ShouldThrowInvalidArgument()
    {
        var (client, factory) = await StartAsync();
        using (factory)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.ListBooksAsync(new ListBooksRequest { Limit = 0 }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        }
    }

    [Test]
    public async Task GetMostBorrowedBooks_ShouldReturnRankedBorrowedBooks()
    {
        var (client, factory) = await StartAsync(async db =>
        {
            await db.Books.InsertManyAsync([
                new Book { Id = B(1), Title = "Popular", Author = "A", PageCount = 100 },
                new Book { Id = B(2), Title = "Niche", Author = "A", PageCount = 100 },
            ]);
            await db.Users.InsertManyAsync([
                new User { Id = U(1), Name = "U1" }, new User { Id = U(2), Name = "U2" },
            ]);
            await db.Loans.InsertManyAsync([
                Loan(1, 1), Loan(2, 1), Loan(1, 2),
            ]);
        });
        using (factory)
        {
            var reply = await client.GetMostBorrowedBooksAsync(new GetMostBorrowedBooksRequest { Limit = 10 });

            await Assert.That(reply.Books[0].Book.Id).IsEqualTo(B(1).ToString());
            await Assert.That(reply.Books[0].BorrowCount).IsEqualTo(2);
        }
    }

    [Test]
    public async Task GetReadingPace_WhenCompletedLoan_ShouldReturnComputablePace()
    {
        var (client, factory) = await StartAsync(async db =>
        {
            await db.Books.InsertOneAsync(new Book { Id = B(1), Title = "T", Author = "A", PageCount = 300 });
            await db.Users.InsertOneAsync(new User { Id = U(1), Name = "U1" });
            await db.Loans.InsertOneAsync(new Loan
            {
                Id = Guid.NewGuid(),
                BookId = B(1), BookTitle = "T", BookAuthor = "A",
                UserId = U(1), UserName = "U1",
                BorrowedAt = Recent, ReturnedAt = Recent.AddDays(10),
            });
        });
        using (factory)
        {
            var reply = await client.GetReadingPaceAsync(new GetReadingPaceRequest
            {
                UserId = U(1).ToString(), BookId = B(1).ToString(),
            });

            await Assert.That(reply.Computable).IsTrue();
            await Assert.That(reply.PagesPerDay).IsEqualTo(30d).Within(1e-6);
        }
    }

    [Test]
    public async Task GetReadingPace_WhenBookMissing_ShouldThrowNotFound()
    {
        var (client, factory) = await StartAsync();
        using (factory)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.GetReadingPaceAsync(new GetReadingPaceRequest
                {
                    UserId = U(1).ToString(), BookId = B(1).ToString(),
                }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.NotFound);
        }
    }

    private static Loan Loan(int user, int book) => new()
    {
        Id = Guid.NewGuid(),
        UserId = U(user), UserName = $"User {user}",
        BookId = B(book), BookTitle = $"Book {book}", BookAuthor = "A",
        BorrowedAt = Recent, ReturnedAt = Recent.AddDays(5),
    };
}
