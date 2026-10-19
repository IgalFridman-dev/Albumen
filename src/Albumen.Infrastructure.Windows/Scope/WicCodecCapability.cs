using System.Runtime.InteropServices;
using Albumen.Domain.Scope;

namespace Albumen.Infrastructure.Windows.Scope;

internal sealed class WicCodecCapability(IComClassAvailability availability)
{
    private readonly IComClassAvailability availability =
        availability ?? throw new ArgumentNullException(nameof(availability));

    public bool IsAvailable(SupportedImageFormat format) =>
        availability.IsAvailable(DecoderClassId(format));

    private static Guid DecoderClassId(SupportedImageFormat format) => format switch
    {
        SupportedImageFormat.Jpeg => Guid.Parse("9456a480-e88b-43ea-9e73-0b2d9b71b1ca"),
        SupportedImageFormat.Png => Guid.Parse("389ea17b-5078-4cde-b6ef-25c15175c751"),
        SupportedImageFormat.Heic => Guid.Parse("e9a4a80a-44fe-4de4-8971-7150b10a5199"),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };
}

internal sealed class ComClassAvailability : IComClassAvailability
{
    private static readonly Guid UnknownInterfaceId =
        Guid.Parse("00000000-0000-0000-c000-000000000046");

    public bool IsAvailable(Guid classId)
    {
        var initializationResult = NativeMethods.CoInitializeEx(
            0,
            NativeMethods.CoInitializeMultithreaded);
        var uninitialize = initializationResult is
            NativeMethods.Success or NativeMethods.SuccessAlreadyInitialized;

        if (initializationResult < 0 && initializationResult != NativeMethods.RpcChangedMode)
        {
            return false;
        }

        nint instance = 0;
        try
        {
            var creationResult = NativeMethods.CoCreateInstance(
                in classId,
                0,
                NativeMethods.ClassContextInProcessServer,
                in UnknownInterfaceId,
                out instance);
            return creationResult >= 0 && instance != 0;
        }
        finally
        {
            if (instance != 0)
            {
                Marshal.Release(instance);
            }

            if (uninitialize)
            {
                NativeMethods.CoUninitialize();
            }
        }
    }
}
