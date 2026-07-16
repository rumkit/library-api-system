using BookLibrary.Catalog.Data.Paging;

namespace BookLibrary.Catalog.Tests.Unit;

/// <summary>Round-trip and malformed-input behaviour of the opaque keyset-paging cursor.</summary>
public class CursorTests
{
    [Test]
    [Arguments("Clean Code")]
    [Arguments("")]
    [Arguments("Война и мир")]
    [Arguments("A \"quoted\" {title} | with: punctuation")]
    public async Task Encode_ThenTryDecode_ShouldRoundTripSortKeyAndId(string sortKey)
    {
        var id = Guid.NewGuid();

        var encoded = Cursor.Encode(sortKey, id);
        var decoded = Cursor.TryDecode(encoded, out var decodedKey, out var decodedId);

        await Assert.That(decoded).IsTrue();
        await Assert.That(decodedKey).IsEqualTo(sortKey);
        await Assert.That(decodedId).IsEqualTo(id);
    }

    [Test]
    public async Task Encode_ThenTryDecode_ShouldRoundTrip500CharTitle()
    {
        var sortKey = new string('x', 500);
        var id = Guid.NewGuid();

        var encoded = Cursor.Encode(sortKey, id);
        var decoded = Cursor.TryDecode(encoded, out var decodedKey, out var decodedId);

        await Assert.That(decoded).IsTrue();
        await Assert.That(decodedKey).IsEqualTo(sortKey);
        await Assert.That(decodedId).IsEqualTo(id);
    }

    [Test]
    public async Task TryDecode_WhenNotBase64_ShouldReturnFalse()
    {
        var decoded = Cursor.TryDecode("not-valid-base64!!!", out _, out _);

        await Assert.That(decoded).IsFalse();
    }

    [Test]
    public async Task TryDecode_WhenBase64ButNotJson_ShouldReturnFalse()
    {
        var notJson = Convert.ToBase64String("this is not json"u8.ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var decoded = Cursor.TryDecode(notJson, out _, out _);

        await Assert.That(decoded).IsFalse();
    }

    [Test]
    [Arguments("""{"k":"Clean Code"}""")]
    [Arguments("""{"i":"0000000a-0000-0000-0000-000000000001"}""")]
    [Arguments("{}")]
    public async Task TryDecode_WhenJsonMissingFields_ShouldReturnFalse(string json)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var decoded = Cursor.TryDecode(encoded, out _, out _);

        await Assert.That(decoded).IsFalse();
    }

    [Test]
    public async Task TryDecode_WhenIdNotAGuid_ShouldReturnFalse()
    {
        var json = """{"k":"Clean Code","i":"not-a-guid"}""";
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var decoded = Cursor.TryDecode(encoded, out _, out _);

        await Assert.That(decoded).IsFalse();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task TryDecode_WhenNullOrEmpty_ShouldReturnFalse(string? value)
    {
        var decoded = Cursor.TryDecode(value, out _, out _);

        await Assert.That(decoded).IsFalse();
    }

    [Test]
    public async Task Encode_ShouldNotContainQueryStringUnsafeCharacters()
    {
        var encoded = Cursor.Encode(new string('x', 500) + "\"{|:", Guid.NewGuid());

        await Assert.That(encoded).DoesNotContain('+');
        await Assert.That(encoded).DoesNotContain('/');
        await Assert.That(encoded).DoesNotContain('=');
    }
}
