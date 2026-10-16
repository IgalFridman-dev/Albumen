namespace PhotoSift.Scenarios.Tests.Shell;

// Test-only validation for a capture whose expected transitions are distinct.
// Product publication multiplicity is checked separately by the UIA scenario.
internal sealed class ItemStatusTransitionCapture
{
    private readonly int[] expectedRuntimeId;
    private readonly int propertyId;
    private readonly (string Old, string New)[] expected;
    private readonly List<Delivery> accepted = [];
    private int rawCount;
    internal int Count => accepted.Count;

    internal ItemStatusTransitionCapture(int[] runtimeId, int propertyId, params string[] payloads)
    {
        if (runtimeId.Length == 0) throw new ArgumentException("Expected runtime ID is empty.");
        expectedRuntimeId = (int[])runtimeId.Clone();
        this.propertyId = propertyId;
        expected = payloads.Select((payload, index) =>
            (index == 0 ? string.Empty : payloads[index - 1], payload)).ToArray();
        if (expected.Length == 0 || expected.Distinct().Count() != expected.Length)
            throw new ArgumentException("Expected transitions must be nonempty and distinct.");
    }

    internal void Record(int[]? runtimeId, int observedPropertyId, object? oldValue, object? newValue)
    {
        if (++rawCount > 256) throw new InvalidOperationException("Native capture exceeded 256 deliveries.");
        if (runtimeId is null || !expectedRuntimeId.AsSpan().SequenceEqual(runtimeId)
            || observedPropertyId != propertyId || oldValue is not string oldText || newValue is not string newText)
            throw new InvalidOperationException("ItemStatus delivery has an unexpected sender, property, or value type.");
        var delivery = new Delivery(runtimeId, observedPropertyId, oldValue, newValue);
        // Native callbacks can repeat an earlier complete tuple after a later tuple.
        // Preserve first-occurrence order; never suppress a changed sender/property/value.
        // The caller retains every raw delivery and independently proves product publication counts.
        if (accepted.Any(prior => Same(prior, delivery))) return;
        var index = Count;
        if (index >= expected.Length)
            throw new InvalidOperationException($"Unexpected normalized ItemStatus transition: '{oldValue}' -> '{newValue}'.");
        if (oldText != expected[index].Old || newText != expected[index].New)
            throw new InvalidOperationException($"Expected ItemStatus transition '{expected[index].Old}' -> '{expected[index].New}', but received '{oldValue}' -> '{newValue}'.");
        accepted.Add(delivery with { RuntimeId = (int[])runtimeId.Clone() });
    }

    internal void Complete()
    {
        if (Count != expected.Length)
            throw new InvalidOperationException($"Expected {expected.Length} transitions, received {Count}.");
    }

    private static bool Same(Delivery left, Delivery right) =>
        (left.RuntimeId is null ? right.RuntimeId is null
            : right.RuntimeId is not null && left.RuntimeId.AsSpan().SequenceEqual(right.RuntimeId))
        && left.PropertyId == right.PropertyId && Equals(left.OldValue, right.OldValue)
        && Equals(left.NewValue, right.NewValue);

    private readonly record struct Delivery(int[]? RuntimeId, int PropertyId, object? OldValue, object? NewValue);
}
