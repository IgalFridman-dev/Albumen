namespace PhotoSift.Domain.Scope;

public sealed record DirectoryIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdHigh,
    ulong FileIdLow);
