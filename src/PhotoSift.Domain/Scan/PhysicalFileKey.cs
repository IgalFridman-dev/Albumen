namespace PhotoSift.Domain.Scan;

public readonly record struct FileId128(ulong High, ulong Low);

public readonly record struct PhysicalFileKey(ulong VolumeSerial, FileId128 FileId);
