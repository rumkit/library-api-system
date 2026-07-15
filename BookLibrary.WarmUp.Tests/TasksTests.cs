using System.Text;

namespace BookLibrary.WarmUp.Tests;

public class TasksTests
{
    // ---------------------------------------------------------------------
    // IsPowerOfTwo
    // ---------------------------------------------------------------------

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(4)]
    [Arguments(16)]
    [Arguments(1024)]
    [Arguments(1 << 30)]
    public async Task IsPowerOfTwo_WhenValueIsPowerOfTwo_ShouldReturnTrue(int id)
    {
        var result = Tasks.IsPowerOfTwo(id);

        await Assert.That(result).IsTrue();
    }

    [Test]
    [Arguments(3)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(100)]
    [Arguments(int.MaxValue)]
    public async Task IsPowerOfTwo_WhenValueIsNotPowerOfTwo_ShouldReturnFalse(int id)
    {
        var result = Tasks.IsPowerOfTwo(id);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsPowerOfTwo_WhenValueIsZero_ShouldReturnFalse()
    {
        var result = Tasks.IsPowerOfTwo(0);

        await Assert.That(result).IsFalse();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(-2)]
    [Arguments(-4)]
    [Arguments(int.MinValue)] // -2^31: a power of two in magnitude, but negative
    public async Task IsPowerOfTwo_WhenValueIsNegative_ShouldReturnFalse(int id)
    {
        var result = Tasks.IsPowerOfTwo(id);

        await Assert.That(result).IsFalse();
    }

    // ---------------------------------------------------------------------
    // ReverseTitle
    // ---------------------------------------------------------------------

    [Test]
    public async Task ReverseTitle_WhenTitleHasMultipleCharacters_ShouldReturnCharactersInReverseOrder()
    {
        var result = new string(Tasks.ReverseTitle("Dune").ToArray());

        await Assert.That(result).IsEqualTo("enuD");
    }

    [Test]
    public async Task ReverseTitle_WhenTitleIsSingleCharacter_ShouldReturnSameCharacter()
    {
        var result = new string(Tasks.ReverseTitle("a").ToArray());

        await Assert.That(result).IsEqualTo("a");
    }

    [Test]
    public async Task ReverseTitle_WhenTitleIsPalindrome_ShouldReturnEqualSequence()
    {
        var result = new string(Tasks.ReverseTitle("level").ToArray());

        await Assert.That(result).IsEqualTo("level");
    }

    [Test]
    public async Task ReverseTitle_WhenTitleIsEmpty_ShouldReturnEmptySequence()
    {
        var result = new string(Tasks.ReverseTitle(string.Empty).ToArray());

        await Assert.That(result).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ReverseTitle_WhenTitleContainsCombiningDiacritic_ShouldKeepAccentedLetterIntact()
    {
        // Decompose to NFD so the accent becomes a separate combining char (U+0301),
        // making the accented letter a two-char grapheme that char-by-char reversal corrupts.
        var result = new string(Tasks.ReverseTitle("Les Misérables".Normalize(NormalizationForm.FormD)).ToArray());

        await Assert.That(result).IsEqualTo("selbarésiM seL".Normalize(NormalizationForm.FormD));
    }

    [Test]
    public async Task ReverseTitle_WhenTitleIsNull_ShouldThrowNullReferenceExceptionOnEnumeration()
    {
        // The method uses deferred execution (yield), so the exception surfaces
        // only once the sequence is enumerated, not when the method is called.
        await Assert.That(() => Tasks.ReverseTitle(null!).ToArray())
            .Throws<NullReferenceException>();
    }

    // ---------------------------------------------------------------------
    // GenerateReplicas
    // ---------------------------------------------------------------------

    [Test]
    public async Task GenerateReplicas_WhenCountIsOne_ShouldReturnSingleCopyOfTitle()
    {
        var result = Tasks.GenerateReplicas("Dune", 1);

        await Assert.That(result).IsEqualTo("Dune");
    }

    [Test]
    public async Task GenerateReplicas_WhenCountIsGreaterThanOne_ShouldRepeatTitleThatManyTimes()
    {
        var result = Tasks.GenerateReplicas("It", 3);

        await Assert.That(result).IsEqualTo("ItItIt");
    }

    [Test]
    public async Task GenerateReplicas_WhenTitleIsEmpty_ShouldReturnEmptyResult()
    {
        var result = Tasks.GenerateReplicas(string.Empty, 5);

        await Assert.That(result).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task GenerateReplicas_WhenCalled_ShouldReturnLengthEqualToTitleLengthTimesCount()
    {
        var length = Tasks.GenerateReplicas("Dune", 4).Length;

        await Assert.That(length).IsEqualTo("Dune".Length * 4);
    }

    [Test]
    public async Task GenerateReplicas_WhenCountIsZero_ShouldReturnEmptyString()
    {
        var result = Tasks.GenerateReplicas("Dune", 0);
        await Assert.That(result).IsEmpty();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(-10)]
    public async Task GenerateReplicas_WhenCountIsNegative_ShouldThrowArgumentOutOfRangeException(int count)
    {
        await Assert.That(() => { _ = Tasks.GenerateReplicas("Dune", count); })
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task GenerateReplicas_WhenTitleIsEmptyAndCountIsNegative_ShouldThrowArgumentOutOfRangeException()
    {
        // The negative-count guard must hold regardless of title length,
        // not merely as a side effect of the computed result length.
        await Assert.That(() => { _ = Tasks.GenerateReplicas(string.Empty, -1); })
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task GenerateReplicas_WhenResultLengthOverflowsInt32_ShouldThrowOverflowException()
    {
        await Assert.That(() => { _ = Tasks.GenerateReplicas("It", int.MaxValue); })
            .Throws<OverflowException>();
    }

    // ---------------------------------------------------------------------
    // ListOddNumbers
    // ---------------------------------------------------------------------

    [Test]
    public async Task ListOddNumbers_WhenCalled_ShouldReturnFiftyNumbers()
    {
        var result = Tasks.ListOddNumbers().ToList();

        await Assert.That(result.Count).IsEqualTo(50);
    }

    [Test]
    public async Task ListOddNumbers_WhenCalled_ShouldContainOnlyOddNumbers()
    {
        var result = Tasks.ListOddNumbers();

        await Assert.That(result.All(n => n % 2 == 1)).IsTrue();
    }

    [Test]
    public async Task ListOddNumbers_WhenCalled_ShouldStartAtOneAndEndAtNinetyNine()
    {
        var result = Tasks.ListOddNumbers().ToList();

        await Assert.That(result[0]).IsEqualTo(1);
        await Assert.That(result[^1]).IsEqualTo(99);
    }
}
