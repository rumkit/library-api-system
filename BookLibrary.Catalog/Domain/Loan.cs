using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace BookLibrary.Catalog.Domain;

/// <summary>
/// A single lending event: a user borrowed a book at <see cref="BorrowedAt"/> and, if
/// <see cref="ReturnedAt"/> is set, returned it then. Timestamps are UTC. An open loan
/// (<see cref="ReturnedAt"/> == null) means the book is currently off the shelf.
/// </summary>
public sealed class Loan
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; init; }

    [BsonRepresentation(BsonType.String)]
    public Guid BookId { get; init; }

    [BsonRepresentation(BsonType.String)]
    public Guid UserId { get; init; }

    public DateTime BorrowedAt { get; init; }

    public DateTime? ReturnedAt { get; init; }
}
