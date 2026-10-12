using GameLingoAI.Core.Models;

namespace GameLingoAI.Core.Abstractions;

/// <summary>Nonblocking, content-free admission. False means the bounded destination did not accept the event.</summary>
public interface IDiagnosticEvents
{
    bool TryWrite(DiagnosticEvent entry);
}
