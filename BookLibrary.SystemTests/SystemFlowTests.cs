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
        var books = await GetAsync<List<BookDto>>("/books?limit=100");

        await Assert.That(books.Count).IsEqualTo(SampleData.Books.Count);
    }

    [Test]
    public async Task GetBook_WhenUnknownId_ShouldReturn404ProblemDetails()
    {
        var response = await Host.ApiClient.GetAsync($"/books/{Guid.NewGuid()}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
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
}
