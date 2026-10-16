namespace PhotoSift.Application.Scan;

public sealed class BoundedScanExecutor(
    IScanEnumerator enumerator, IProtectedFileReader reader, IExactContentAnalyzer analyzer) : IScanExecutor
{
    public const int WorkCapacity = 32;
    public const int WorkerCount = 1;

    public IScanRun Start(ScanRunContext context, IScanResultSink sink)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sink);
        return new ScanRun(context, sink, enumerator, new ProtectedExactWorker(reader, analyzer));
    }
}
