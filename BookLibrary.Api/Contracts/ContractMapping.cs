using Google.Protobuf.WellKnownTypes;
using Proto = BookLibrary.Contracts;

namespace BookLibrary.Api.Contracts;

/// <summary>Trivial, allocation-light translation between gRPC contract messages and the REST
/// DTOs. Manual by design — the shapes are one-to-one, so a mapping framework would add nothing.</summary>
internal static class ContractMapping
{
    public static BookDto ToDto(this Proto.Book book) =>
        new(Guid.Parse(book.Id), book.Title, book.Author, book.PageCount, book.Year);

    public static UserDto ToDto(this Proto.User user) =>
        new(Guid.Parse(user.Id), user.Name);

    public static MostBorrowedBookDto ToDto(this Proto.BorrowedBook b) =>
        new(b.Book.ToDto(), b.BorrowCount);

    public static TopBorrowerDto ToDto(this Proto.Borrower b) =>
        new(b.User.ToDto(), b.BorrowCount);

    public static CoBorrowedBookDto ToDto(this Proto.CoBorrowedBook b) =>
        new(b.Book.ToDto(), b.CoBorrowerCount);

    public static ReadingPaceDto ToDto(this Proto.ReadingPaceResponse r) =>
        r.Computable
            ? new ReadingPaceDto(true, r.PagesPerDay, null)
            : new ReadingPaceDto(false, null, r.Reason);

    /// <summary>Interprets a nullable query DateTime as UTC and converts it to a proto Timestamp.</summary>
    public static Timestamp? ToTimestamp(this DateTime? value) =>
        value is null ? null : Timestamp.FromDateTime(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
}
