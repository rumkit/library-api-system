using System.Globalization;
using System.Text.Json;
using BookLibrary.Api.Contracts;

namespace BookLibrary.Api.Tests;

/// <summary>UtcDateTime must be server-timezone-independent: plain values are assumed UTC,
/// offset/Z values are converted to UTC, and every parsed result carries Kind=Utc.</summary>
public class UtcDateTimeTests
{
    [Test]
    public async Task TryParse_WhenPlainValue_ShouldAssumeUtc()
    {
        var ok = UtcDateTime.TryParse("2026-01-01T00:00:00", CultureInfo.InvariantCulture, out var result);

        await Assert.That(ok).IsTrue();
        await Assert.That(result.Value.Kind).IsEqualTo(DateTimeKind.Utc);
        await Assert.That(result.Value).IsEqualTo(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task TryParse_WhenTrailingZ_ShouldProduceSameInstant()
    {
        var ok = UtcDateTime.TryParse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture, out var result);

        await Assert.That(ok).IsTrue();
        await Assert.That(result.Value.Kind).IsEqualTo(DateTimeKind.Utc);
        await Assert.That(result.Value).IsEqualTo(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task TryParse_WhenOffsetSupplied_ShouldConvertToUtc()
    {
        var ok = UtcDateTime.TryParse(
            "2026-01-01T02:00:00+02:00", CultureInfo.InvariantCulture, out var result);

        await Assert.That(ok).IsTrue();
        await Assert.That(result.Value.Kind).IsEqualTo(DateTimeKind.Utc);
        await Assert.That(result.Value).IsEqualTo(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task TryParse_WhenGarbage_ShouldReturnFalse()
    {
        var ok = UtcDateTime.TryParse("not-a-date", CultureInfo.InvariantCulture, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Json_WhenPlainValue_ShouldDeserializeAsAssumedUtc()
    {
        var result = JsonSerializer.Deserialize<UtcDateTime>("\"2026-01-01T00:00:00\"");

        await Assert.That(result.Value.Kind).IsEqualTo(DateTimeKind.Utc);
        await Assert.That(result.Value).IsEqualTo(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task Json_WhenTrailingZ_ShouldDeserializeToSameInstant()
    {
        var result = JsonSerializer.Deserialize<UtcDateTime>("\"2026-01-01T00:00:00Z\"");

        await Assert.That(result.Value.Kind).IsEqualTo(DateTimeKind.Utc);
        await Assert.That(result.Value).IsEqualTo(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task Json_WhenOffsetSupplied_ShouldDeserializeConvertedToUtc()
    {
        var result = JsonSerializer.Deserialize<UtcDateTime>("\"2026-01-01T02:00:00+02:00\"");

        await Assert.That(result.Value.Kind).IsEqualTo(DateTimeKind.Utc);
        await Assert.That(result.Value).IsEqualTo(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task Json_RoundTrip_ShouldSerializeAsParseableUtcString()
    {
        var original = new UtcDateTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<UtcDateTime>(json);

        await Assert.That(json.TrimEnd('"').EndsWith('Z')).IsTrue();
        await Assert.That(roundTripped).IsEqualTo(original);
    }
}
