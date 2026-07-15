using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace BookLibrary.Catalog.Domain;

/// <summary>
/// A book in the library's inventory. Domain and persistence model are deliberately
/// collapsed (see README) — a separate DTO layer would add ceremony without value at
/// this scale. Guids are persisted as strings so Mongo documents stay reviewer-readable.
/// </summary>
public sealed class Book
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; init; }

    public required string Title { get; init; }

    public required string Author { get; init; }

    public int PageCount { get; init; }

    /// <summary>Year of publication. May be <c>0</c> (unknown).</summary>
    public int Year { get; init; }
}
