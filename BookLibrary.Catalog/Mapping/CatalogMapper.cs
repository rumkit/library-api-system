using Riok.Mapperly.Abstractions;
using Contracts = BookLibrary.Contracts;
using Domain = BookLibrary.Catalog.Domain;

namespace BookLibrary.Catalog.Mapping;

/// <summary>
/// Source-generated mapping from domain models to gRPC contract messages. Mapperly emits plain
/// assignment code at compile time — no reflection — and the <see cref="GuidToString"/> helper
/// is picked up automatically wherever a <see cref="Guid"/> must become a proto string id.
/// </summary>
[Mapper]
public partial class CatalogMapper
{
    public partial Contracts.Book ToContract(Domain.Book book);

    public partial Contracts.User ToContract(Domain.User user);

    private static string GuidToString(Guid value) => value.ToString();
}
