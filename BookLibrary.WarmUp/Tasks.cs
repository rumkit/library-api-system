using System.Buffers;
using JetBrains.Annotations;

namespace BookLibrary.WarmUp;

[PublicAPI]
public class Tasks
{
    // BCL already has BitOperations.IsPow2, which would be better for real-world scenario
    public static bool IsPowerOfTwo(int id)
        => (id & (id - 1)) == 0 && id > 0;
    
    // again for a real-world scenario 'title.Reverse()' looks much better
    public static IEnumerable<char> ReverseTitle(string title)
    {
        for (var i = title.Length - 1; i >= 0; i--)
            yield return title[i];
    }

    public static Span<char> GenerateReplicas(string title, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        
        var totalLength = title.Length * count;
        var pooledArray = ArrayPool<char>.Shared.Rent(totalLength).AsSpan(..totalLength);
        for (var i = 0; i < count; i++)
            title.CopyTo(pooledArray[(title.Length * i) .. ]);

        return pooledArray;
    }

    public static IEnumerable<int> ListOddNumbers() => Enumerable.Range(1, 100).Where(i => i % 2 == 1);
}