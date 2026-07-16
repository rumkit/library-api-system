namespace BookLibrary.Api.Contracts;

// Small, explicit REST response shapes. Mapping proto messages onto these keeps the JSON clean
// (plain DateTime, no protobuf machinery) and gives the HTTP surface a contract of its own.

public sealed record BookDto(Guid Id, string Title, string Author, int PageCount, int Year);

public sealed record UserDto(Guid Id, string Name);

/// <summary>One page of a cursor-paginated list. NextCursor is null on the last page.</summary>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);

public sealed record CreateBookRequestDto(string Title, string Author, int PageCount, int Year);

public sealed record DeleteBookResultDto(int ClosedLoans);

public sealed record MostBorrowedBookDto(BookDto Book, long BorrowCount);

public sealed record TopBorrowerDto(UserDto User, long BorrowCount);

public sealed record CoBorrowedBookDto(BookDto Book, long CoBorrowerCount);

/// <summary>Reading-pace estimate. <see cref="PagesPerDay"/> is null when the estimate could
/// not be computed; <see cref="Reason"/> then explains why.</summary>
public sealed record ReadingPaceDto(bool Computable, double? PagesPerDay, string? Reason);
