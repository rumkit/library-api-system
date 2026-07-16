using Google.Protobuf.WellKnownTypes;
using Riok.Mapperly.Abstractions;
using Contracts = BookLibrary.Contracts;
using Domain = BookLibrary.Catalog.Domain;

namespace BookLibrary.Catalog.Mapping;

/// <summary>
/// Source-generated mapping from domain models to gRPC contract messages. Mapperly emits plain
/// assignment code at compile time — no reflection — and the <see cref="GuidToString"/>/
/// <see cref="ToTimestamp"/> helpers are picked up automatically wherever a <see cref="Guid"/> or
/// <see cref="DateTime"/> must become a proto string id / <see cref="Timestamp"/>.
/// </summary>
[Mapper]
public partial class CatalogMapper
{
    public partial Contracts.Book ToContract(Domain.Book book);

    public partial Contracts.User ToContract(Domain.User user);

    public partial Contracts.Loan ToContract(Domain.Loan loan);

    private static string GuidToString(Guid value) => value.ToString();

    // Mongo hands back DateTimes with Kind == Unspecified; Timestamp.FromDateTime throws unless
    // Kind == Utc, so force it (mirrors BookLibrary.Api.Contracts.ContractMapping.ToTimestamp).
    private static Timestamp ToTimestamp(DateTime value) =>
        Timestamp.FromDateTime(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
