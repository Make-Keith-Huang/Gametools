namespace GameLingoAI.App.Services;

public interface IErrorReporter
{
    event EventHandler<string>? ErrorReported;

    void Report(Exception exception);
}

public sealed class ErrorReporter(GameLingoAI.Core.Abstractions.IDiagnosticEvents? diagnostics = null) : IErrorReporter
{
    public event EventHandler<string>? ErrorReported;

    public void Report(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        { diagnostics?.TryWrite(new(GameLingoAI.Core.Models.DiagnosticEventKind.CommandFailed)); }
        catch (Exception) { }
        ErrorReported?.Invoke(this, "The operation could not be completed. Review settings and try again.");
    }
}
