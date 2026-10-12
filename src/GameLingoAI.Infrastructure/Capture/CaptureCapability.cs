using System.Runtime.InteropServices;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Infrastructure.Capture;

public sealed record CaptureCapability(bool IsSupported, string Message);

public interface IWindowsGraphicsCaptureSourceFactory
{
    CaptureCapability DetectCapability();

    OperationResult<ICaptureSource> Create(ConfirmedCaptureWindow target, SessionId sessionId);
}

public sealed class WindowsGraphicsCaptureSourceFactory : IWindowsGraphicsCaptureSourceFactory
{
    public CaptureCapability DetectCapability()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
        {
            return new CaptureCapability(false, "Window capture requires Windows 10 version 1903 or later.");
        }

        try
        {
            return Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported()
                ? new CaptureCapability(
                    true,
                    "Windows Graphics Capture is available. Exclusive-fullscreen targets may require borderless or windowed mode.")
                : new CaptureCapability(
                    false,
                    "Windows Graphics Capture is disabled or unsupported. Try a normal desktop session and borderless or windowed mode.");
        }
        catch (Exception exception) when (exception is TypeLoadException or COMException)
        {
            return new CaptureCapability(false, "Windows Graphics Capture is unavailable in this desktop session.");
        }
    }

    public OperationResult<ICaptureSource> Create(ConfirmedCaptureWindow target, SessionId sessionId)
    {
        ArgumentNullException.ThrowIfNull(target);
        var capability = DetectCapability();
        if (!capability.IsSupported)
        {
            return OperationResult.Failed<ICaptureSource>(new PipelineFailure(
                FailureCode.UnsupportedPlatform,
                capability.Message,
                CorrelationId.New(),
                false));
        }

        if (target.Window.IsMinimized)
        {
            return OperationResult.Failed<ICaptureSource>(new PipelineFailure(
                FailureCode.TargetUnavailable,
                "Restore the selected window before starting capture.",
                CorrelationId.New(),
                true));
        }

        return OperationResult.Success<ICaptureSource>(new WindowsGraphicsCaptureSource(target, sessionId));
    }
}
