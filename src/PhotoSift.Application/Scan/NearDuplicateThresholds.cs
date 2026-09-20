using System.Numerics;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Application.Scan;

public static class NearDuplicateThresholds
{
    public const int NearHammingMaximum = 4;
    public const int NearColorMaximum = 12;
    public const int UncertainHammingMaximum = 16;
    public const int UncertainColorMaximum = 40;

    public static NearDuplicateEvidence Classify(
        PerceptualSignature left,
        PerceptualSignature right,
        OccurrenceToken? leftToken = null,
        OccurrenceToken? rightToken = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (!string.Equals(left.ImplementationRevision, right.ImplementationRevision,
            StringComparison.Ordinal))
            throw new ArgumentException("Perceptual signatures must use the same implementation revision.");

        var hamming = BitOperations.PopCount(left.DifferenceHash ^ right.DifferenceHash);
        var color = Math.Max(Math.Abs(left.AverageBlue - right.AverageBlue),
            Math.Max(Math.Abs(left.AverageGreen - right.AverageGreen),
                Math.Abs(left.AverageRed - right.AverageRed)));
        var kind = hamming <= NearHammingMaximum && color <= NearColorMaximum
            ? NearRelationshipKind.Near
            : hamming <= UncertainHammingMaximum && color <= UncertainColorMaximum
                ? NearRelationshipKind.Uncertain
                : NearRelationshipKind.NotRelated;
        return new(leftToken ?? Placeholder(), rightToken ?? Placeholder(), hamming, color, kind);
    }

    private static OccurrenceToken Placeholder() =>
        new(new ScanPin(Guid.Empty, 0, 0, 0), 0, 0, 0);
}
