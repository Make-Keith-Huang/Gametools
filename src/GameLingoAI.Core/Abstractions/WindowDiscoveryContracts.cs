using GameLingoAI.Core.Models;

namespace GameLingoAI.Core.Abstractions;

public interface IWindowDiscovery
{
    Task<OperationResult<IReadOnlyList<CaptureWindow>>> DiscoverAsync(
        IReadOnlySet<WindowId> excludedWindows,
        CancellationToken cancellationToken);
}
