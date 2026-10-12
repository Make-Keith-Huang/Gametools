namespace GameLingoAI.Core.Models;

public enum SessionState
{
    Stopped,
    Starting,
    Running,
    Paused,
    Stopping,
    Faulted
}

public static class SessionTransitions
{
    public static bool IsAllowed(SessionState current, SessionState next) => (current, next) switch
    {
        (SessionState.Stopped, SessionState.Starting) => true,
        (SessionState.Starting, SessionState.Running or SessionState.Stopping or SessionState.Faulted) => true,
        (SessionState.Running, SessionState.Paused or SessionState.Stopping or SessionState.Faulted) => true,
        (SessionState.Paused, SessionState.Running or SessionState.Stopping or SessionState.Faulted) => true,
        (SessionState.Stopping, SessionState.Stopped or SessionState.Faulted) => true,
        (SessionState.Faulted, SessionState.Starting or SessionState.Stopped) => true,
        _ => false
    };

    public static SessionState Transition(SessionState current, SessionState next) =>
        IsAllowed(current, next)
            ? next
            : throw new InvalidOperationException($"Session transition {current} -> {next} is not allowed.");
}

/// <summary>Rejects duplicate or late sequence numbers. The type is safe for concurrent stages.</summary>
public sealed class MonotonicSequenceGuard
{
    private long _lastAccepted = -1;

    public SequenceNumber? LastAccepted
    {
        get
        {
            var value = Volatile.Read(ref _lastAccepted);
            return value < 0 ? null : new SequenceNumber(value);
        }
    }

    public bool TryAccept(SequenceNumber candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref _lastAccepted);
            if (candidate.Value <= current)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _lastAccepted, candidate.Value, current) == current)
            {
                return true;
            }
        }
    }
}
