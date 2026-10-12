namespace GameLingoAI.App.Services;

public interface IStoppedSessionMaintenance
{
    /// <summary>Excludes concurrent startup and runs only while the actual controller is stopped.</summary>
    Task RunAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken);
}
