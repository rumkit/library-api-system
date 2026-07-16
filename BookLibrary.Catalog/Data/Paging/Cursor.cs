using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace BookLibrary.Catalog.Data.Paging;

/// <summary>
/// Opaque keyset-paging cursor: base64url of a compact JSON object carrying the sort key and id
/// of the last item on the previous page. Created and interpreted by Catalog only — the REST edge
/// passes it through untouched, so sort-key knowledge stays in the layer that owns the sort.
/// </summary>
internal sealed record CursorPayload(string K, string I);

internal static class Cursor
{
    public static string Encode(string sortKey, Guid id)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new CursorPayload(sortKey, id.ToString()));
        return Base64Url.EncodeToString(json);
    }

    /// <returns>false when the value is not a well-formed cursor (caller maps to InvalidArgument).</returns>
    public static bool TryDecode(string? value, out string sortKey, out Guid id)
    {
        sortKey = "";
        id = Guid.Empty;

        if (string.IsNullOrEmpty(value))
            return false;

        byte[] json;
        try
        {
            json = Base64Url.DecodeFromChars(value);
        }
        catch (FormatException)
        {
            return false;
        }

        CursorPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CursorPayload>(json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload is null || payload.K is null || payload.I is null || !Guid.TryParse(payload.I, out var parsedId))
            return false;

        sortKey = payload.K;
        id = parsedId;
        return true;
    }
}
