using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

public sealed class ItemStatusTransitionCaptureTests
{
    private static ItemStatusTransitionCapture NewCapture() => new([7, 123, 456], 30026, "first", "second");

    [Theory]
    [InlineData("AB")]
    [InlineData("AABB")]
    [InlineData("ABAB")]
    [InlineData("ABA")]
    public void ExactRepeatedDeliveriesPreserveFirstOccurrenceOrder(string sequence)
    {
        var capture = NewCapture();
        foreach (var item in sequence) Record(capture, item);
        capture.Complete();
        Assert.Equal(2, capture.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("B")]
    [InlineData("BA")]
    [InlineData("ABC")]
    public void MissingReorderedAndNovelTransitionsFail(string sequence)
    {
        var capture = NewCapture();
        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (var item in sequence) Record(capture, item);
            capture.Complete();
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrWrongSenderFailsBeforeAcceptingPayload(bool wrongSender)
    {
        var capture = NewCapture();
        Assert.Throws<InvalidOperationException>(() => capture.Record(
            wrongSender ? [7, 999, 456] : null, 30026, "", "first"));
    }

    [Theory]
    [InlineData(30027, "", "first")]
    [InlineData(30026, "wrong", "first")]
    [InlineData(30026, "", "wrong")]
    [InlineData(30026, null, "first")]
    [InlineData(30026, "", null)]
    [InlineData(30026, 0, "first")]
    [InlineData(30026, "", 1)]
    public void PropertyValuesAndTypesMustMatch(int property, object? oldValue, object? newValue)
    {
        var capture = NewCapture();
        Assert.Throws<InvalidOperationException>(() => capture.Record([7, 123, 456], property, oldValue, newValue));
    }

    [Fact]
    public void LateWrongSenderIsNotSuppressedAsAnExactDuplicate()
    {
        var capture = NewCapture();
        Record(capture, 'A');
        Record(capture, 'B');
        Assert.Throws<InvalidOperationException>(() => capture.Record([7, 999, 456], 30026, "", "first"));
    }

    [Theory]
    [InlineData(30027, "", "first")]
    [InlineData(30026, "wrong", "first")]
    [InlineData(30026, "", "wrong")]
    [InlineData(30026, null, "first")]
    [InlineData(30026, "", 1)]
    public void LateChangedDeliveryIsNeverSuppressed(int property, object? oldValue, object? newValue)
    {
        var capture = NewCapture();
        Record(capture, 'A');
        Record(capture, 'B');
        Assert.Throws<InvalidOperationException>(() => capture.Record([7, 123, 456], property, oldValue, newValue));
    }

    [Fact]
    public void EachCaptureOwnsItsDuplicateState()
    {
        var first = NewCapture();
        Record(first, 'A');
        Record(first, 'B');
        first.Complete();
        var second = NewCapture();
        Record(second, 'A');
        Assert.Throws<InvalidOperationException>(second.Complete);
    }

    [Fact]
    public void DuplicateFloodCannotHideCaptureOverflow()
    {
        var capture = NewCapture();
        for (var index = 0; index < 256; index++) Record(capture, 'A');
        Assert.Throws<InvalidOperationException>(() => Record(capture, 'A'));
    }

    [Fact]
    public void RepeatedExpectedTransitionsAreRejectedInsteadOfCollapsed()
    {
        Assert.Throws<ArgumentException>(() => new ItemStatusTransitionCapture([7, 123, 456], 30026,
            "first", "second", "first", "second"));
    }

    private static void Record(ItemStatusTransitionCapture capture, char item)
    {
        // Allocate a fresh ID each time: equality must use contents, not array identity.
        var transition = item switch
        {
            'A' => ("", "first"),
            'B' => ("first", "second"),
            _ => ("second", "third")
        };
        capture.Record([7, 123, 456], 30026, transition.Item1, transition.Item2);
    }
}
