using System.Runtime.InteropServices;

namespace GameLingoAI.Infrastructure.Capture;

public static class GraphicsCaptureRecoveryPolicy
{
    public const int MaximumAttempts = 3;

    public const int MaximumStartupStateAttempts = 5;

    public static bool IsTransientStartupState(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is COMException && IsTransientStartupStateHResult(exception.HResult);
    }

    public static bool IsTransientStartupStateHResult(int hResult) =>
        hResult == unchecked((int)0x8007139F);

    public static bool IsRecoverableDeviceLoss(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is COMException comException && IsRecoverableDeviceLossHResult(comException.HResult))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsRecoverableDeviceLossHResult(int hResult) => hResult is
        unchecked((int)0x887A0005) or // DXGI_ERROR_DEVICE_REMOVED
        unchecked((int)0x887A0006) or // DXGI_ERROR_DEVICE_HUNG
        unchecked((int)0x887A0007);   // DXGI_ERROR_DEVICE_RESET
}
