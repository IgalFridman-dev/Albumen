namespace Albumen.Domain.Scan;

public enum ExactDuplicateMode
{
    Disabled,
    Enabled,
}

public enum NearDuplicateMode
{
    Disabled,
    Conservative,
}

public sealed record ScanOptions(
    ExactDuplicateMode Exact,
    NearDuplicateMode Near)
{
    public static ScanOptions Default { get; } = new(
        ExactDuplicateMode.Enabled,
        NearDuplicateMode.Conservative);

    public bool IsValid => Exact == ExactDuplicateMode.Enabled
        && Near is NearDuplicateMode.Disabled or NearDuplicateMode.Conservative;

    public string ValidationGuidance => IsValid
        ? string.Empty
        : "Exact must remain enabled. Near must be Disabled or Conservative.";
}
