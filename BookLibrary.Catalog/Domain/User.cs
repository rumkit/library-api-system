using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace BookLibrary.Catalog.Domain;

/// <summary>
/// A library borrower. Authentication/authorization are out of scope per the spec, so a
/// user is just an identity and a display name.
/// </summary>
public sealed class User
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; init; }

    public required string Name { get; init; }
}
