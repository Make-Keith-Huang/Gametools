using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Core.Abstractions;

public interface IConfigurationStore
{
    Task<OperationResult<AppSettings>> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);

    Task<string> ExportSanitizedAsync(AppSettings settings, CancellationToken cancellationToken);
}
