using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PhotoSift.Infrastructure.Windows.Action;

// Windows SDK ShObjIdl_core.idl order, including unused slots. Opaque pointers
// retain their native width; every HRESULT is inspected explicitly.
[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal partial interface IShellItem
{
    [PreserveSig] int BindToHandler(nint bindContext, in Guid handler, in Guid iid, out nint result);
    [PreserveSig] int GetParent(out nint parent);
    [PreserveSig] int GetDisplayName(uint nameKind, out nint name);
    [PreserveSig] int GetAttributes(uint mask, out uint attributes);
    [PreserveSig] int Compare(nint other, uint hint, out int order);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
internal partial interface IFileOperation
{
    [PreserveSig] int Advise(nint sink, out uint cookie);
    [PreserveSig] int Unadvise(uint cookie);
    [PreserveSig] int SetOperationFlags(uint flags);
    [PreserveSig] int SetProgressMessage(nint message);
    [PreserveSig] int SetProgressDialog(nint dialog);
    [PreserveSig] int SetProperties(nint properties);
    [PreserveSig] int SetOwnerWindow(nint owner);
    [PreserveSig] int ApplyPropertiesToItem(nint item);
    [PreserveSig] int ApplyPropertiesToItems(nint items);
    [PreserveSig] int RenameItem(nint item, nint newName, nint sink);
    [PreserveSig] int RenameItems(nint items, nint newName);
    [PreserveSig] int MoveItem(nint item, nint destination, nint newName, nint sink);
    [PreserveSig] int MoveItems(nint items, nint destination);
    [PreserveSig] int CopyItem(nint item, nint destination, nint newName, nint sink);
    [PreserveSig] int CopyItems(nint items, nint destination);
    [PreserveSig] int DeleteItem(nint item, nint sink);
    [PreserveSig] int DeleteItems(nint items);
    [PreserveSig] int NewItem(nint destination, uint attributes, nint name, nint template, nint sink);
    [PreserveSig] int PerformOperations();
    [PreserveSig] int GetAnyOperationsAborted(out int aborted);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
internal partial interface IFileOperationProgressSink
{
    [PreserveSig] int StartOperations();
    [PreserveSig] int FinishOperations(int result);
    [PreserveSig] int PreRenameItem(uint flags, nint item, nint newName);
    [PreserveSig] int PostRenameItem(uint flags, nint item, nint newName, int result, nint newlyCreated);
    [PreserveSig] int PreMoveItem(uint flags, nint item, nint destination, nint newName);
    [PreserveSig] int PostMoveItem(uint flags, nint item, nint destination, nint newName, int result, nint newlyCreated);
    [PreserveSig] int PreCopyItem(uint flags, nint item, nint destination, nint newName);
    [PreserveSig] int PostCopyItem(uint flags, nint item, nint destination, nint newName, int result, nint newlyCreated);
    [PreserveSig] int PreDeleteItem(uint flags, nint item);
    [PreserveSig] int PostDeleteItem(uint flags, nint item, int result, nint newlyCreated);
    [PreserveSig] int PreNewItem(uint flags, nint destination, nint newName);
    [PreserveSig] int PostNewItem(uint flags, nint destination, nint newName, nint template, uint attributes, int result, nint newlyCreated);
    [PreserveSig] int UpdateProgress(uint total, uint complete);
    [PreserveSig] int ResetTimer();
    [PreserveSig] int PauseTimer();
    [PreserveSig] int ResumeTimer();
}

internal interface IFileOperationDriver : IDisposable
{
    int InitializeCom();
    int CreateOperation();
    int CreateItem(string originalPath);
    bool IsExpectedItem(nint item);
    int Advise(FileOperationProgressSink sink);
    int SetOperationFlags(uint flags);
    int DeleteItem();
    int PerformOperations();
    int GetAnyOperationsAborted(out bool aborted);
    int Unadvise();
    void UninitializeCom();
}

internal sealed unsafe class NativeFileOperationDriver : IFileOperationDriver
{
    internal const uint RecycleOnlyFlags = 0x0004 | 0x0010 | 0x0400 | 0x00080000;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private IFileOperation? operation;
    private IShellItem? item;
    private nint operationPointer;
    private nint itemPointer;
    private nint sinkPointer;
    private uint cookie;
    private bool operationCreated;
    private bool itemCreated;
    private bool deleteQueued;
    private bool performed;
    private bool advised;
    private bool disposed;

    public int InitializeCom()
    {
        CheckThread();
        return FileOperationNative.CoInitializeEx(0, 0x2); // COINIT_APARTMENTTHREADED
    }

    public int CreateOperation()
    {
        CheckThread();
        if (operationCreated) throw new InvalidOperationException("Only one operation is permitted.");
        operationCreated = true;
        var classId = new Guid("3ad05575-8857-4850-9277-11b85bdb8e09");
        var iid = typeof(IFileOperation).GUID;
        var result = FileOperationNative.CoCreateInstance(in classId, 0, 1, in iid, out operationPointer);
        if (result >= 0)
        {
            if (operationPointer == 0) throw new InvalidOperationException("Missing operation interface.");
            operation = UniqueComInterfaceMarshaller<IFileOperation>.ConvertToManaged((void*)operationPointer);
        }
        return result;
    }

    public int CreateItem(string originalPath)
    {
        CheckThread();
        if (itemCreated) throw new InvalidOperationException("Only one original item is permitted.");
        itemCreated = true;
        var iid = typeof(IShellItem).GUID;
        var result = FileOperationNative.SHCreateItemFromParsingName(originalPath, 0, in iid, out itemPointer);
        if (result >= 0)
        {
            if (itemPointer == 0) throw new InvalidOperationException("Missing original item interface.");
            item = UniqueComInterfaceMarshaller<IShellItem>.ConvertToManaged((void*)itemPointer);
        }
        return result;
    }

    public bool IsExpectedItem(nint candidate)
    {
        CheckThread();
        if (candidate == 0 || item is null) return false;
        // The shell may clone its IShellItem wrapper for callbacks. ALLFIELDS
        // compares the exact namespace item across instances; a hard-link alias
        // is another item. Never use a display name or filesystem-path fallback.
        var result = item.Compare(candidate, 0x80000000, out var order); // SICHINT_ALLFIELDS
        Marshal.ThrowExceptionForHR(result);
        return result == 0 && order == 0; // S_FALSE is not an equality success.
    }

    public int Advise(FileOperationProgressSink sink)
    {
        CheckThread();
        if (sinkPointer != 0) throw new InvalidOperationException("Only one operation-wide sink is permitted.");
        sinkPointer = (nint)ComInterfaceMarshaller<IFileOperationProgressSink>.ConvertToUnmanaged(sink);
        var result = operation!.Advise(sinkPointer, out cookie);
        advised = result >= 0;
        return result;
    }

    public int SetOperationFlags(uint flags)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(flags, RecycleOnlyFlags);
        CheckThread();
        return operation!.SetOperationFlags(flags);
    }

    public int DeleteItem()
    {
        CheckThread();
        if (deleteQueued || itemPointer == 0 || !advised) throw new InvalidOperationException("Invalid single-item state.");
        deleteQueued = true;
        // The operation-wide registration is the sole callback subscription.
        return operation!.DeleteItem(itemPointer, 0);
    }

    public int PerformOperations()
    {
        CheckThread();
        if (performed || !deleteQueued) throw new InvalidOperationException("An operation cannot be repeated.");
        performed = true;
        return operation!.PerformOperations();
    }

    public int GetAnyOperationsAborted(out bool aborted)
    {
        CheckThread();
        var result = operation!.GetAnyOperationsAborted(out var nativeAborted);
        aborted = nativeAborted != 0;
        return result;
    }

    public int Unadvise()
    {
        CheckThread();
        if (!advised) return 0;
        advised = false;
        return operation!.Unadvise(cookie);
    }

    public void Dispose()
    {
        CheckThread();
        if (disposed) return;
        disposed = true;
        // Release the operation before our sink pointer even if Unadvise failed.
        // Unique wrappers own references separate from the native out pointers.
        try { ReleaseWrapper(ref operation); }
        finally
        {
            try { ReleasePointer(ref operationPointer); }
            finally
            {
                try { ReleaseWrapper(ref item); }
                finally
                {
                    try { ReleasePointer(ref itemPointer); }
                    finally
                    {
                        if (sinkPointer != 0)
                        {
                            var pointer = sinkPointer;
                            sinkPointer = 0;
                            ComInterfaceMarshaller<IFileOperationProgressSink>.Free((void*)pointer);
                        }
                    }
                }
            }
        }
    }

    public void UninitializeCom() { CheckThread(); FileOperationNative.CoUninitialize(); }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("COM ownership crossed its STA.");
    }
    private static void ReleaseWrapper<T>(ref T? wrapper) where T : class
    {
        var owned = wrapper;
        wrapper = null;
        if (owned is not null) ((ComObject)(object)owned).FinalRelease();
    }
    private static void ReleasePointer(ref nint pointer)
    {
        var owned = pointer;
        pointer = 0;
        if (owned != 0) Marshal.Release(owned);
    }
}

internal static partial class FileOperationNative
{
    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, uint apartment);
    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();
    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid iid, out nint result);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHCreateItemFromParsingName(string originalPath, nint bindContext, in Guid iid, out nint result);
}
