using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookLibrary.Api.Contracts;

/// <summary>
/// A <see cref="DateTime"/> wrapper that is always <see cref="DateTimeKind.Utc"/>, for query and
/// body timestamp binding at the REST edge.
///
/// Plain <see cref="DateTime"/> binding/parsing converts an offset-bearing value (e.g. trailing
/// "Z" or "+02:00") to the SERVER'S LOCAL timezone (producing <see cref="DateTimeKind.Local"/>);
/// relabeling that as UTC afterwards silently shifts the instant by the server's UTC offset, so
/// the same request means different things depending on where the process happens to run. This
/// type parses with <see cref="DateTimeStyles.AssumeUniversal"/> | <see cref="DateTimeStyles.AdjustToUniversal"/>
/// instead: a plain value ("2026-01-01T00:00:00") is ASSUMED to already be UTC, and an
/// offset/"Z" value is CONVERTED to UTC — the server's local timezone never participates.
/// </summary>
[JsonConverter(typeof(UtcDateTimeJsonConverter))]
public readonly record struct UtcDateTime(DateTime Value) : IParsable<UtcDateTime>
{
    /// <summary>Implicitly unwraps to the underlying UTC <see cref="DateTime"/>.</summary>
    public static implicit operator DateTime(UtcDateTime value) => value.Value;

    public static UtcDateTime Parse(string s, IFormatProvider? provider) =>
        TryParse(s, provider, out var result)
            ? result
            : throw new FormatException($"'{s}' is not a recognized UTC date/time.");

    public static bool TryParse(
        [NotNullWhen(true)] string? s, IFormatProvider? provider, out UtcDateTime result)
    {
        if (DateTime.TryParse(
                s,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            result = new UtcDateTime(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
            return true;
        }

        result = default;
        return false;
    }
}

/// <summary>JSON body binding for <see cref="UtcDateTime"/>, with the same server-timezone-independent
/// semantics as <see cref="IParsable{TSelf}"/> query binding.</summary>
public sealed class UtcDateTimeJsonConverter : JsonConverter<UtcDateTime>
{
    public override UtcDateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (!UtcDateTime.TryParse(s, CultureInfo.InvariantCulture, out var result))
            throw new JsonException($"'{s}' is not a recognized UTC date/time.");
        return result;
    }

    public override void Write(Utf8JsonWriter writer, UtcDateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value.ToString("o", CultureInfo.InvariantCulture));
}
