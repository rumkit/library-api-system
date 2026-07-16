namespace BookLibrary.Catalog.Data.Paging;

/// <summary>
/// Thrown by a repository's <c>ListAsync</c> when a caller-supplied cursor cannot be used: it is
/// structurally malformed (bad base64/JSON/id), or its sort key does not match the shape that
/// repository's sort expects (e.g. a title-sorted book cursor handed to the date-sorted loan
/// listing). Repositories own cursor decoding end-to-end, so this is the single explicit signal
/// they raise instead of silently falling back to page one; <c>CatalogGrpcService</c> catches it
/// and maps it to a gRPC <c>InvalidArgument</c> status.
/// </summary>
public sealed class InvalidCursorException(string message) : Exception(message);
