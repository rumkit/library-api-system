using System.Globalization;
using JetBrains.Annotations;

namespace BookLibrary.WarmUp;

[PublicAPI]
public class Tasks
{
    // BCL already has the BitOperations.IsPow2() method, which would be better for a real-world scenario
    public static bool IsPowerOfTwo(int id)
        => (id & (id - 1)) == 0 && id > 0;
    
    // If the input string is in UTF-8, or we can guarantee all chars are single-byte,
    // then such an overengineering is not needed and the title.Reverse() will do.
    // See ReverseTitle_WhenTitleContainsCombiningDiacritic_ShouldKeepAccentedLetterIntact()
    public static string ReverseTitle(string title)
        => string.Create(title.Length, title, static (span, source) =>
        {
            var src = source.AsSpan();
            var remainingLength = span.Length;
            for (var i = 0; i < src.Length;)
            {
                var elementLength = StringInfo.GetNextTextElementLength(src[i..]);
                remainingLength -= elementLength;
                src.Slice(i, elementLength).CopyTo(span[remainingLength..]);
                i += elementLength;
            }
        });

    public static string GenerateReplicas(string title, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        return string.Create(checked(title.Length * count), (title, count), static (span, state) =>
        {
            var (title, count) = state;
            for (var i = 0; i < count; i++)
                title.CopyTo(span[(title.Length * i) ..]);
        });
    }

    public static IEnumerable<int> ListOddNumbers() => Enumerable.Range(1, 100).Where(i => i % 2 == 1);
}