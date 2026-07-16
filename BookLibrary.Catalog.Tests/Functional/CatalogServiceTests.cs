using BookLibrary.Catalog.Data;
using BookLibrary.Catalog.Data.Paging;
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

    [Test]
    public async Task ListBooks_WhenCursorSupplied_ShouldReturnNextPageWithoutOverlap()
    {
        var (client, factory) = await StartAsync(db => db.Books.InsertManyAsync(
            Enumerable.Range(1, 5).Select(n => new Book
            {
                Id = B(n), Title = $"Title {n:D2}", Author = "A", PageCount = 100,
            })));
        using (factory)
        {
            var first = await client.ListBooksAsync(new ListBooksRequest { Limit = 2 });
            await Assert.That(first.Books.Count).IsEqualTo(2);
            await Assert.That(first.HasNextCursor).IsTrue();

            var second = await client.ListBooksAsync(new ListBooksRequest { Limit = 2, Cursor = first.NextCursor });
            await Assert.That(second.Books.Count).IsEqualTo(2);

            var firstIds = first.Books.Select(b => b.Id).ToHashSet();
            var secondIds = second.Books.Select(b => b.Id).ToHashSet();
            await Assert.That(firstIds.Overlaps(secondIds)).IsFalse();
        }
    }

    [Test]
    public async Task ListBooks_WhenLastPage_ShouldReturnNoNextCursor()
    {
        var (client, factory) = await StartAsync(db => db.Books.InsertOneAsync(
            new Book { Id = B(1), Title = "Solo", Author = "A", PageCount = 100 }));
        using (factory)
        {
            var reply = await client.ListBooksAsync(new ListBooksRequest { Limit = 20 });

            await Assert.That(reply.HasNextCursor).IsFalse();
        }
    }

    [Test]
    public async Task ListBooks_WhenCursorMalformed_ShouldThrowInvalidArgument()
    {
        var (client, factory) = await StartAsync();
        using (factory)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.ListBooksAsync(new ListBooksRequest { Limit = 10, Cursor = "not-a-cursor" }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        }
    }

    [Test]
    public async Task ListBooks_WhenBooksShareTitle_ShouldPageWithoutDuplicates()
    {
        var (client, factory) = await StartAsync(db => db.Books.InsertManyAsync(
            Enumerable.Range(1, 6).Select(n => new Book
            {
                Id = B(n), Title = "Same Title", Author = "A", PageCount = 100,
            })));
        using (factory)
        {
            var seen = new HashSet<string>();
            string? cursor = null;
            ListBooksResponse? page = null;
            for (var i = 0; i < 10 && (page is null || page.HasNextCursor); i++)
            {
                var request = new ListBooksRequest { Limit = 2 };
                if (cursor is not null) request.Cursor = cursor;
                page = await client.ListBooksAsync(request);
                foreach (var b in page.Books)
                    seen.Add(b.Id);
                cursor = page.HasNextCursor ? page.NextCursor : null;
            }

            await Assert.That(seen.Count).IsEqualTo(6);
        }
    }

    [Test]
    public async Task CreateBook_WhenValid_ShouldReturnBookWithGeneratedId()
    {
        var (client, factory) = await StartAsync();
        using (factory)
        {
            var book = await client.CreateBookAsync(new CreateBookRequest
            {
                Title = "New Book", Author = "New Author", PageCount = 200, Year = 2020,
            });

            await Assert.That(Guid.TryParse(book.Id, out _)).IsTrue();
            await Assert.That(book.Title).IsEqualTo("New Book");
            await Assert.That(book.Author).IsEqualTo("New Author");
            await Assert.That(book.PageCount).IsEqualTo(200);
            await Assert.That(book.Year).IsEqualTo(2020);
        }
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task CreateBook_WhenTitleBlank_ShouldThrowInvalidArgument(string title)
    {
        var (client, factory) = await StartAsync();
        using (factory)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.CreateBookAsync(new CreateBookRequest
                {
                    Title = title, Author = "A", PageCount = 100, Year = 2020,
                }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        }
    }

    [Test]
    public async Task CreateBook_WhenPageCountNegative_ShouldThrowInvalidArgument()
    {
        var (client, factory) = await StartAsync();
        using (factory)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.CreateBookAsync(new CreateBookRequest
                {
                    Title = "T", Author = "A", PageCount = -1, Year = 2020,
                }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        }
    }

    [Test]
    public async Task CreateBook_WhenYearInFarFuture_ShouldThrowInvalidArgument()
    {
        var (client, factory) = await StartAsync();
        using (factory)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.CreateBookAsync(new CreateBookRequest
                {
                    Title = "T", Author = "A", PageCount = 100, Year = DateTime.UtcNow.Year + 50,
                }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        }
    }

    [Test]
    public async Task DeleteBook_WhenUnknown_ShouldThrowNotFound()
    {
        var (client, factory) = await StartAsync();
        using (factory)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.DeleteBookAsync(new DeleteBookRequest { Id = B(99).ToString() }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.NotFound);
        }
    }

    [Test]
    public async Task DeleteBook_WhenBookHasOpenLoan_ShouldCloseLoanAndReport()
    {
        var loanId = Guid.NewGuid();
        var (client, factory) = await StartAsync(async db =>
        {
            await db.Books.InsertOneAsync(new Book { Id = B(1), Title = "T", Author = "A", PageCount = 100 });
            await db.Users.InsertOneAsync(new User { Id = U(1), Name = "U1" });
            await db.Loans.InsertOneAsync(new Loan
            {
                Id = loanId,
                BookId = B(1), BookTitle = "T", BookAuthor = "A",
                UserId = U(1), UserName = "U1",
                BorrowedAt = Recent, ReturnedAt = null,
            });
        });
        using (factory)
        {
            var reply = await client.DeleteBookAsync(new DeleteBookRequest { Id = B(1).ToString() });

            await Assert.That(reply.ClosedLoans).IsEqualTo(1);

            var db = new LibraryDb(new MongoClient(Mongo.ConnectionString)
                .GetDatabase(factory.DatabaseName));
            var loan = await db.Loans.Find(l => l.Id == loanId).FirstOrDefaultAsync();
            await Assert.That(loan).IsNotNull();
            await Assert.That(loan!.ReturnedAt).IsNotNull();
            await Assert.That(loan.BookTitle).IsEqualTo("T");
        }
    }

    [Test]
    public async Task ListUsers_WhenCursorSupplied_ShouldReturnNextPageWithoutOverlap()
    {
        var (client, factory) = await StartAsync(db => db.Users.InsertManyAsync(
            Enumerable.Range(1, 5).Select(n => new User { Id = U(n), Name = $"Name {n:D2}" })));
        using (factory)
        {
            var first = await client.ListUsersAsync(new ListUsersRequest { Limit = 2 });
            await Assert.That(first.Users.Count).IsEqualTo(2);
            await Assert.That(first.HasNextCursor).IsTrue();

            var second = await client.ListUsersAsync(new ListUsersRequest { Limit = 2, Cursor = first.NextCursor });
            await Assert.That(second.Users.Count).IsEqualTo(2);

            var firstIds = first.Users.Select(u => u.Id).ToHashSet();
            var secondIds = second.Users.Select(u => u.Id).ToHashSet();
            await Assert.That(firstIds.Overlaps(secondIds)).IsFalse();
        }
    }

    [Test]
    public async Task ListUsers_WhenUsersShareName_ShouldPageWithoutDuplicates()
    {
        var (client, factory) = await StartAsync(db => db.Users.InsertManyAsync(
            Enumerable.Range(1, 6).Select(n => new User { Id = U(n), Name = "Same Name" })));
        using (factory)
        {
            var seen = new HashSet<string>();
            string? cursor = null;
            ListUsersResponse? page = null;
            for (var i = 0; i < 10 && (page is null || page.HasNextCursor); i++)
            {
                var request = new ListUsersRequest { Limit = 2 };
                if (cursor is not null) request.Cursor = cursor;
                page = await client.ListUsersAsync(request);
                foreach (var u in page.Users)
                    seen.Add(u.Id);
                cursor = page.HasNextCursor ? page.NextCursor : null;
            }

            await Assert.That(seen.Count).IsEqualTo(6);
        }
    }

    [Test]
    public async Task CreateUser_WhenValid_ShouldReturnUserWithGeneratedId()
    {
        var (client, factory) = await StartAsync();
        using (factory)
        {
            var user = await client.CreateUserAsync(new CreateUserRequest { Name = "New User" });

            await Assert.That(Guid.TryParse(user.Id, out _)).IsTrue();
            await Assert.That(user.Name).IsEqualTo("New User");
        }
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task CreateUser_WhenNameBlank_ShouldThrowInvalidArgument(string name)
    {
        var (client, factory) = await StartAsync();
        using (factory)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.CreateUserAsync(new CreateUserRequest { Name = name }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        }
    }

    [Test]
    public async Task UpdateUser_WhenUnknown_ShouldThrowNotFound()
    {
        var (client, factory) = await StartAsync();
        using (factory)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.UpdateUserAsync(new UpdateUserRequest { Id = U(99).ToString(), Name = "X" }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.NotFound);
        }
    }

    [Test]
    public async Task UpdateUser_WhenRenamed_ShouldNotRewriteLoanHistory()
    {
        var loanId = Guid.NewGuid();
        var (client, factory) = await StartAsync(async db =>
        {
            await db.Users.InsertOneAsync(new User { Id = U(1), Name = "Old Name" });
            await db.Books.InsertOneAsync(new Book { Id = B(1), Title = "T", Author = "A", PageCount = 100 });
            await db.Loans.InsertOneAsync(new Loan
            {
                Id = loanId,
                BookId = B(1), BookTitle = "T", BookAuthor = "A",
                UserId = U(1), UserName = "Old Name",
                BorrowedAt = Recent, ReturnedAt = Recent.AddDays(5),
            });
        });
        using (factory)
        {
            var updated = await client.UpdateUserAsync(new UpdateUserRequest { Id = U(1).ToString(), Name = "New Name" });
            await Assert.That(updated.Name).IsEqualTo("New Name");

            var db = new LibraryDb(new MongoClient(Mongo.ConnectionString).GetDatabase(factory.DatabaseName));
            var loan = await db.Loans.Find(l => l.Id == loanId).FirstOrDefaultAsync();
            await Assert.That(loan!.UserName).IsEqualTo("Old Name");
        }
    }

    [Test]
    public async Task DeleteUser_WhenNoOpenLoans_ShouldDelete()
    {
        var (client, factory) = await StartAsync(db => db.Users.InsertOneAsync(new User { Id = U(1), Name = "U1" }));
        using (factory)
        {
            await client.DeleteUserAsync(new DeleteUserRequest { Id = U(1).ToString() });

            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.GetUserAsync(new GetUserRequest { Id = U(1).ToString() }));
            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.NotFound);
        }
    }

    [Test]
    public async Task DeleteUser_WhenOpenLoanExists_ShouldThrowFailedPrecondition()
    {
        var (client, factory) = await StartAsync(async db =>
        {
            await db.Users.InsertOneAsync(new User { Id = U(1), Name = "U1" });
            await db.Books.InsertOneAsync(new Book { Id = B(1), Title = "T", Author = "A", PageCount = 100 });
            await db.Loans.InsertOneAsync(new Loan
            {
                Id = Guid.NewGuid(),
                BookId = B(1), BookTitle = "T", BookAuthor = "A",
                UserId = U(1), UserName = "U1",
                BorrowedAt = Recent, ReturnedAt = null,
            });
        });
        using (factory)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.DeleteUserAsync(new DeleteUserRequest { Id = U(1).ToString() }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.FailedPrecondition);
        }
    }

    [Test]
    public async Task DeleteUser_WhenOnlyClosedLoans_ShouldDelete()
    {
        var (client, factory) = await StartAsync(async db =>
        {
            await db.Users.InsertOneAsync(new User { Id = U(1), Name = "U1" });
            await db.Books.InsertOneAsync(new Book { Id = B(1), Title = "T", Author = "A", PageCount = 100 });
            await db.Loans.InsertOneAsync(new Loan
            {
                Id = Guid.NewGuid(),
                BookId = B(1), BookTitle = "T", BookAuthor = "A",
                UserId = U(1), UserName = "U1",
                BorrowedAt = Recent, ReturnedAt = Recent.AddDays(5),
            });
        });
        using (factory)
        {
            await client.DeleteUserAsync(new DeleteUserRequest { Id = U(1).ToString() });

            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.GetUserAsync(new GetUserRequest { Id = U(1).ToString() }));
            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.NotFound);
        }
    }

    [Test]
    public async Task CreateLoan_WhenValid_ShouldSnapshotBookAndUserFields()
    {
        var (client, factory) = await StartAsync(async db =>
        {
            await db.Books.InsertOneAsync(new Book { Id = B(1), Title = "Snapshot Title", Author = "Snapshot Author", PageCount = 100 });
            await db.Users.InsertOneAsync(new User { Id = U(1), Name = "Snapshot User" });
        });
        using (factory)
        {
            var loan = await client.CreateLoanAsync(new CreateLoanRequest
            {
                BookId = B(1).ToString(), UserId = U(1).ToString(),
            });

            await Assert.That(loan.BookTitle).IsEqualTo("Snapshot Title");
            await Assert.That(loan.BookAuthor).IsEqualTo("Snapshot Author");
            await Assert.That(loan.UserName).IsEqualTo("Snapshot User");
        }
    }

    [Test]
    public async Task CreateLoan_WhenBookUnknown_ShouldThrowNotFound()
    {
        var (client, factory) = await StartAsync(db => db.Users.InsertOneAsync(new User { Id = U(1), Name = "U1" }));
        using (factory)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.CreateLoanAsync(new CreateLoanRequest
                {
                    BookId = B(99).ToString(), UserId = U(1).ToString(),
                }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.NotFound);
        }
    }

    [Test]
    public async Task CreateLoan_WhenUserUnknown_ShouldThrowNotFound()
    {
        var (client, factory) = await StartAsync(db =>
            db.Books.InsertOneAsync(new Book { Id = B(1), Title = "T", Author = "A", PageCount = 100 }));
        using (factory)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.CreateLoanAsync(new CreateLoanRequest
                {
                    BookId = B(1).ToString(), UserId = U(99).ToString(),
                }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.NotFound);
        }
    }

    [Test]
    public async Task CreateLoan_WhenBookAlreadyOnLoan_ShouldThrowFailedPrecondition()
    {
        var (client, factory) = await StartAsync(async db =>
        {
            await db.Books.InsertOneAsync(new Book { Id = B(1), Title = "T", Author = "A", PageCount = 100 });
            await db.Users.InsertManyAsync([new User { Id = U(1), Name = "U1" }, new User { Id = U(2), Name = "U2" }]);
        });
        using (factory)
        {
            await client.CreateLoanAsync(new CreateLoanRequest { BookId = B(1).ToString(), UserId = U(1).ToString() });

            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.CreateLoanAsync(new CreateLoanRequest
                {
                    BookId = B(1).ToString(), UserId = U(2).ToString(),
                }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.FailedPrecondition);
        }
    }

    [Test]
    public async Task CreateLoan_WhenBookPreviouslyReturned_ShouldSucceed()
    {
        var (client, factory) = await StartAsync(async db =>
        {
            await db.Books.InsertOneAsync(new Book { Id = B(1), Title = "T", Author = "A", PageCount = 100 });
            await db.Users.InsertManyAsync([new User { Id = U(1), Name = "U1" }, new User { Id = U(2), Name = "U2" }]);
            await db.Loans.InsertOneAsync(new Loan
            {
                Id = Guid.NewGuid(),
                BookId = B(1), BookTitle = "T", BookAuthor = "A",
                UserId = U(1), UserName = "U1",
                BorrowedAt = Recent, ReturnedAt = Recent.AddDays(5),
            });
        });
        using (factory)
        {
            var loan = await client.CreateLoanAsync(new CreateLoanRequest
            {
                BookId = B(1).ToString(), UserId = U(2).ToString(),
            });

            await Assert.That(loan.UserId).IsEqualTo(U(2).ToString());
        }
    }

    [Test]
    public async Task CreateLoan_WhenBorrowedAtInFuture_ShouldThrowInvalidArgument()
    {
        var (client, factory) = await StartAsync(async db =>
        {
            await db.Books.InsertOneAsync(new Book { Id = B(1), Title = "T", Author = "A", PageCount = 100 });
            await db.Users.InsertOneAsync(new User { Id = U(1), Name = "U1" });
        });
        using (factory)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.CreateLoanAsync(new CreateLoanRequest
                {
                    BookId = B(1).ToString(), UserId = U(1).ToString(),
                    BorrowedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow.AddDays(1)),
                }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        }
    }

    [Test]
    public async Task ReturnLoan_WhenOpen_ShouldCloseAndReturnLoan()
    {
        var loanId = Guid.NewGuid();
        var (client, factory) = await StartAsync(async db =>
        {
            await db.Books.InsertOneAsync(new Book { Id = B(1), Title = "T", Author = "A", PageCount = 100 });
            await db.Users.InsertOneAsync(new User { Id = U(1), Name = "U1" });
            await db.Loans.InsertOneAsync(new Loan
            {
                Id = loanId,
                BookId = B(1), BookTitle = "T", BookAuthor = "A",
                UserId = U(1), UserName = "U1",
                BorrowedAt = Recent, ReturnedAt = null,
            });
        });
        using (factory)
        {
            var loan = await client.ReturnLoanAsync(new ReturnLoanRequest { Id = loanId.ToString() });

            await Assert.That(loan.ReturnedAt).IsNotNull();
        }
    }

    [Test]
    public async Task ReturnLoan_WhenAlreadyReturned_ShouldThrowFailedPrecondition()
    {
        var loanId = Guid.NewGuid();
        var (client, factory) = await StartAsync(async db =>
        {
            await db.Books.InsertOneAsync(new Book { Id = B(1), Title = "T", Author = "A", PageCount = 100 });
            await db.Users.InsertOneAsync(new User { Id = U(1), Name = "U1" });
            await db.Loans.InsertOneAsync(new Loan
            {
                Id = loanId,
                BookId = B(1), BookTitle = "T", BookAuthor = "A",
                UserId = U(1), UserName = "U1",
                BorrowedAt = Recent, ReturnedAt = Recent.AddDays(5),
            });
        });
        using (factory)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.ReturnLoanAsync(new ReturnLoanRequest { Id = loanId.ToString() }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.FailedPrecondition);
        }
    }

    [Test]
    public async Task ReturnLoan_WhenUnknown_ShouldThrowNotFound()
    {
        var (client, factory) = await StartAsync();
        using (factory)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.ReturnLoanAsync(new ReturnLoanRequest { Id = Guid.NewGuid().ToString() }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.NotFound);
        }
    }

    [Test]
    public async Task ReturnLoan_WhenReturnedAtBeforeBorrowedAt_ShouldThrowInvalidArgument()
    {
        var loanId = Guid.NewGuid();
        var (client, factory) = await StartAsync(async db =>
        {
            await db.Books.InsertOneAsync(new Book { Id = B(1), Title = "T", Author = "A", PageCount = 100 });
            await db.Users.InsertOneAsync(new User { Id = U(1), Name = "U1" });
            await db.Loans.InsertOneAsync(new Loan
            {
                Id = loanId,
                BookId = B(1), BookTitle = "T", BookAuthor = "A",
                UserId = U(1), UserName = "U1",
                BorrowedAt = Recent, ReturnedAt = null,
            });
        });
        using (factory)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.ReturnLoanAsync(new ReturnLoanRequest
                {
                    Id = loanId.ToString(),
                    ReturnedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(Recent.AddDays(-1)),
                }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        }
    }

    [Test]
    public async Task ListLoans_WhenOpenOnly_ShouldReturnOnlyOpenLoans()
    {
        var (client, factory) = await StartAsync(async db =>
        {
            await db.Books.InsertManyAsync([
                new Book { Id = B(1), Title = "B1", Author = "A", PageCount = 100 },
                new Book { Id = B(2), Title = "B2", Author = "A", PageCount = 100 },
            ]);
            await db.Users.InsertOneAsync(new User { Id = U(1), Name = "U1" });
            await db.Loans.InsertManyAsync([
                new Loan
                {
                    Id = Guid.NewGuid(), BookId = B(1), BookTitle = "B1", BookAuthor = "A",
                    UserId = U(1), UserName = "U1", BorrowedAt = Recent, ReturnedAt = null,
                },
                new Loan
                {
                    Id = Guid.NewGuid(), BookId = B(2), BookTitle = "B2", BookAuthor = "A",
                    UserId = U(1), UserName = "U1", BorrowedAt = Recent, ReturnedAt = Recent.AddDays(5),
                },
            ]);
        });
        using (factory)
        {
            var reply = await client.ListLoansAsync(new ListLoansRequest { Limit = 20, OpenOnly = true });

            await Assert.That(reply.Loans.Count).IsEqualTo(1);
            await Assert.That(reply.Loans[0].BookId).IsEqualTo(B(1).ToString());
        }
    }

    [Test]
    public async Task ListLoans_WhenCursorMalformed_ShouldThrowInvalidArgument()
    {
        var (client, factory) = await StartAsync();
        using (factory)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.ListLoansAsync(new ListLoansRequest { Limit = 10, Cursor = "not-a-cursor" }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        }
    }

    // Regression test: a structurally well-formed cursor whose sort key does not parse as the
    // loan listing's DateTime sort (e.g. one produced by /books, sort key = a book title) must be
    // rejected as InvalidArgument, not surface as an unhandled FormatException -> gRPC Unknown ->
    // HTTP 500. See BookLibrary.Catalog/Data/LoanRepository.cs ListAsync and
    // BookLibrary.Catalog/Services/CatalogGrpcService.cs ListLoans.
    [Test]
    public async Task ListLoans_WhenCursorHasWrongSortKeyShape_ShouldThrowInvalidArgumentNotUnknown()
    {
        var (client, factory) = await StartAsync();
        using (factory)
        {
            // Structurally a valid cursor (as produced by BookRepository: title sort key + id),
            // but not a DateTime — the shape LoanRepository's sort requires.
            var bookShapedCursor = Cursor.Encode("Moby Dick", Guid.NewGuid());

            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.ListLoansAsync(new ListLoansRequest { Limit = 10, Cursor = bookShapedCursor }));

            await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        }
    }

    [Test]
    public async Task ListLoans_WhenFilteredByUser_ShouldReturnOnlyThatUsersLoans()
    {
        var (client, factory) = await StartAsync(async db =>
        {
            await db.Books.InsertManyAsync([
                new Book { Id = B(1), Title = "B1", Author = "A", PageCount = 100 },
                new Book { Id = B(2), Title = "B2", Author = "A", PageCount = 100 },
            ]);
            await db.Users.InsertManyAsync([new User { Id = U(1), Name = "U1" }, new User { Id = U(2), Name = "U2" }]);
            await db.Loans.InsertManyAsync([
                new Loan
                {
                    Id = Guid.NewGuid(), BookId = B(1), BookTitle = "B1", BookAuthor = "A",
                    UserId = U(1), UserName = "U1", BorrowedAt = Recent, ReturnedAt = Recent.AddDays(5),
                },
                new Loan
                {
                    Id = Guid.NewGuid(), BookId = B(2), BookTitle = "B2", BookAuthor = "A",
                    UserId = U(2), UserName = "U2", BorrowedAt = Recent, ReturnedAt = Recent.AddDays(5),
                },
            ]);
        });
        using (factory)
        {
            var reply = await client.ListLoansAsync(new ListLoansRequest { Limit = 20, UserId = U(1).ToString() });

            await Assert.That(reply.Loans.Count).IsEqualTo(1);
            await Assert.That(reply.Loans[0].UserId).IsEqualTo(U(1).ToString());
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
