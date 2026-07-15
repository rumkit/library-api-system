using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace BookLibrary.Catalog.Domain;

/// <summary>
/// A single lending event: a user borrowed a book at <see cref="BorrowedAt"/> and, if
/// <see cref="ReturnedAt"/> is set, returned it then. Timestamps are UTC. An open loan
/// (<see cref="ReturnedAt"/> == null) means the book is currently off the shelf.
/// </summary>
/// <remarks>
/// The book's title/author and the borrower's name are <b>snapshotted</b> onto the loan at
/// borrow time — like <see cref="BorrowedAt"/>, they record a historical fact about the event,
/// not a live pointer. This keeps loan history intact and readable even after the referenced
/// book or user is deleted, and preserves the borrower's name as it was at the time of the loan
/// (a later rename does not rewrite history). The <see cref="BookId"/>/<see cref="UserId"/>
/// references are kept alongside as the identity and join key for insights and live lookups.
/// </remarks>
public sealed class Loan
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; init; }

    [BsonRepresentation(BsonType.String)]
    public Guid BookId { get; init; }

    /// <summary>The book's title as it was when borrowed (snapshot; see remarks).</summary>
    public required string BookTitle { get; init; }

    /// <summary>The book's author as it was when borrowed (snapshot; see remarks).</summary>
    public required string BookAuthor { get; init; }

    [BsonRepresentation(BsonType.String)]
    public Guid UserId { get; init; }

    /// <summary>The borrower's name as it was when borrowed (snapshot; see remarks).</summary>
    public required string UserName { get; init; }

    public DateTime BorrowedAt { get; init; }

    public DateTime? ReturnedAt { get; init; }
}
