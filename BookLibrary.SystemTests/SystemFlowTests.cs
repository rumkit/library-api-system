using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BookLibrary.Seeder;

namespace BookLibrary.SystemTests;

/// <summary>
/// Validates complete user flows through the deployed system: HTTP (Api) → gRPC (Catalog) →
/// MongoDB, against the seeded sample data. Asserts the insight answers the librarian would see.
/// </summary>
public class SystemFlowTests
{
    [ClassDataSource<AppHostFixture>(Shared = SharedType.PerAssembly)]
    public required AppHostFixture Host { get; init; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Well-known seeded entities.
    private static Guid CleanCodeId => SampleData.Books[0].Id;   // "Clean Code", 464 pages
    private static Guid PragmaticId => SampleData.Books[1].Id;   // "The Pragmatic Programmer"
    private static Guid DddId => SampleData.Books[2].Id;         // "Domain-Driven Design"
    private static Guid AliceId => SampleData.Users[0].Id;

    [Test]
    public async Task GetBooks_ShouldReturnAllSeededBooks()
    {
        // Other tests in this shared-host class create/delete their own books concurrently, so
        // assert a superset of the seeded ids rather than an exact count.
        var seen = await PageAllBooksAsync(100);

        await Assert.That(SampleData.Books.Select(b => b.Id).All(seen.Contains)).IsTrue();
    }

    [Test]
    public async Task GetBook_WhenUnknownId_ShouldReturn404ProblemDetails()
    {
        var response = await Host.ApiClient.GetAsync($"/books/{Guid.NewGuid()}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
    }

    [Test]
    public async Task PostBook_ThenGetBook_ShouldRoundTrip()
    {
        var response = await Host.ApiClient.PostAsJsonAsync("/books", new
        {
            Title = "System Test Book", Author = "System Author", PageCount = 123, Year = 2021,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<BookDto>(Json))!;
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo($"/books/{created.Id}");

        var fetched = await GetAsync<BookDto>($"/books/{created.Id}");
        await Assert.That(fetched.Title).IsEqualTo("System Test Book");

        // Clean up: the AppHost fixture (and its seeded data count) is shared across this class's
        // tests, so a book left behind here would leak into e.g. the paging-count tests.
        await Host.ApiClient.DeleteAsync($"/books/{created.Id}");
    }

    [Test]
    public async Task Books_WhenPagingWithCursor_ShouldEnumerateEverySeededBookExactlyOnce()
    {
        // Other tests in this shared-host class create/delete their own books concurrently, so
        // assert a superset of the seeded ids rather than an exact count.
        var seen = await PageAllBooksAsync(10);

        await Assert.That(SampleData.Books.Select(b => b.Id).All(seen.Contains)).IsTrue();
    }

    private async Task<HashSet<Guid>> PageAllBooksAsync(int limit)
    {
        var seen = new HashSet<Guid>();
        string? cursor = null;
        CursorPage<BookDto>? page = null;
        // Bounded so a cursor bug (loop/skip) fails the test instead of hanging.
        for (var i = 0; i < 1000 && (page is null || page.NextCursor is not null); i++)
        {
            var url = cursor is null
                ? $"/books?limit={limit}"
                : $"/books?limit={limit}&cursor={Uri.EscapeDataString(cursor)}";
            page = await GetAsync<CursorPage<BookDto>>(url);
            foreach (var b in page.Items)
                seen.Add(b.Id);
            cursor = page.NextCursor;
        }

        return seen;
    }

    [Test]
    public async Task MostBorrowed_ShouldRankCleanCodeFirst()
    {
        var result = await GetAsync<List<MostBorrowedDto>>("/insights/most-borrowed?limit=10");

        await Assert.That(result[0].Book.Id).IsEqualTo(CleanCodeId);
        await Assert.That(result[0].BorrowCount).IsEqualTo(18); // seeded so Clean Code is the runaway top
    }

    [Test]
    public async Task TopBorrowers_ShouldRankAliceFirst()
    {
        var result = await GetAsync<List<TopBorrowerDto>>("/insights/top-borrowers?limit=10");

        await Assert.That(result[0].User.Id).IsEqualTo(AliceId);
        await Assert.That(result[0].BorrowCount).IsEqualTo(20); // seeded so Alice is the runaway top
    }

    [Test]
    public async Task ReadingPace_ForAliceAndCleanCode_ShouldBe464Over10Days()
    {
        var pace = await GetAsync<ReadingPaceDto>(
            $"/insights/reading-pace?userId={AliceId}&bookId={CleanCodeId}");

        await Assert.That(pace.Computable).IsTrue();
        await Assert.That(pace.PagesPerDay!.Value).IsEqualTo(46.4).Within(0.01); // 464 pages / 10 days
    }

    [Test]
    public async Task CoBorrowed_ForCleanCode_ShouldIncludePragmaticAndDdd()
    {
        var result = await GetAsync<List<CoBorrowedDto>>($"/insights/co-borrowed/{CleanCodeId}?limit=10");

        var ids = result.Select(r => r.Book.Id).ToList();
        await Assert.That(ids).Contains(PragmaticId);
        await Assert.That(ids).Contains(DddId);
        await Assert.That(ids).DoesNotContain(CleanCodeId); // never co-borrowed with itself
    }

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await Host.ApiClient.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new Exception($"GET {url} -> {(int)response.StatusCode}: {body}");
        }
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private sealed record BookDto(Guid Id, string Title, string Author, int PageCount);
    private sealed record UserDto(Guid Id, string Name);
    private sealed record MostBorrowedDto(BookDto Book, long BorrowCount);
    private sealed record TopBorrowerDto(UserDto User, long BorrowCount);
    private sealed record CoBorrowedDto(BookDto Book, long CoBorrowerCount);
    private sealed record ReadingPaceDto(bool Computable, double? PagesPerDay, string? Reason);
    private sealed record CursorPage<T>(List<T> Items, string? NextCursor);
}
