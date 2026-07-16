using MongoDB.Driver;

namespace BookLibrary.Catalog.Data;

/// <summary>
/// The single shared collation for case-insensitive Book/User listing order. Defined once so the
/// `(Title, Id)` / `(Name, Id)` indexes in <see cref="MongoIndexInitializer"/> and the
/// corresponding <c>Find</c> queries in <see cref="BookRepository"/> / <see cref="UserRepository"/>
/// can't drift apart — the query collation must match the index collation or Mongo falls back to a
/// collection scan instead of using the index.
/// </summary>
/// <remarks>
/// Strength 2 ("secondary") compares case-insensitively but still distinguishes accents/diacritics,
/// so "apple" and "Apple" sort together and compare equal, while accented letters keep their own
/// distinct ordering. Guid strings (used for the <c>Id</c> tiebreaker) are lowercase hex, which
/// strength-2 collation orders identically to binary comparison, so keyset paging's Id tiebreaker
/// is unaffected.
/// </remarks>
public static class ListingCollation
{
    public static readonly Collation Collation = new("en", strength: CollationStrength.Secondary);

    public static readonly FindOptions FindOptions = new() { Collation = Collation };
}
