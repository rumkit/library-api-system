namespace BookLibrary.Catalog.Data.Paging;

/// <summary>One page of a cursor-paginated list. <see cref="NextCursor"/> is null on the last page.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);
