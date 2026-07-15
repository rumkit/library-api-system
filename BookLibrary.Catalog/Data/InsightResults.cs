using BookLibrary.Catalog.Domain;

namespace BookLibrary.Catalog.Data;

/// <summary>A book ranked by how many counted borrows it received.</summary>
public sealed record MostBorrowedResult(Book Book, long BorrowCount);

/// <summary>A user ranked by how many counted borrows they made in the window.</summary>
public sealed record TopBorrowerResult(User User, long BorrowCount);

/// <summary>A book ranked by how many distinct users borrowed it alongside the requested book.</summary>
public sealed record CoBorrowedResult(Book Book, long CoBorrowerCount);
