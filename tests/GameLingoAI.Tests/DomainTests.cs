using GameLingoAI.Core.Models;
using Xunit;

namespace GameLingoAI.Tests;

[Trait("Suite", "Default")]
public sealed class DomainTests
{
    [Fact]
    public void TestModelEquality()
    {
        var session = new SessionId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var correlation = new CorrelationId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var first = new PipelineMetadata(session, new SequenceNumber(7), DateTimeOffset.UnixEpoch, correlation);
        var second = new PipelineMetadata(session, new SequenceNumber(7), DateTimeOffset.UnixEpoch, correlation);
        Assert(first == second, "Equivalent immutable metadata should compare equal.");
    }

    [Fact]
    public void TestRegionBoundaries()
    {
        var wholeFrame = new NormalizedRegion(0, 0, 1, 1);
        Assert(wholeFrame == new NormalizedRegion(0, 0, 1, 1), "Regions should use value equality.");
        Assert(!NormalizedRegion.TryCreate(-0.1, 0, 0.5, 0.5, out _), "Negative origin must fail.");
        Assert(!NormalizedRegion.TryCreate(0, 0, 0, 0.5, out _), "Zero width must fail.");
        Assert(!NormalizedRegion.TryCreate(0.5, 0.5, 0.6, 0.5, out _), "Overflow must fail.");
        Assert(!NormalizedRegion.TryCreate(double.NaN, 0, 0.5, 0.5, out _), "NaN must fail.");
        AssertThrows<ArgumentOutOfRangeException>(() => _ = new NormalizedRegion(0, 0, 2, 1));
    }

    [Fact]
    public void TestMetadataValidation()
    {
        AssertThrows<ArgumentOutOfRangeException>(() => _ = new SessionId(Guid.Empty));
        AssertThrows<ArgumentOutOfRangeException>(() => _ = new SequenceNumber(-1));
        AssertThrows<ArgumentException>(() => _ = new PipelineMetadata(
            SessionId.New(),
            new SequenceNumber(0),
            DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(8)),
            CorrelationId.New()));
        AssertThrows<OverflowException>(() => new SequenceNumber(long.MaxValue).Next());
    }

    [Fact]
    public void TestFrameValidationAndDisposal()
    {
        var metadata = new PipelineMetadata(
            SessionId.New(),
            new SequenceNumber(0),
            DateTimeOffset.UnixEpoch,
            CorrelationId.New());
        var lease = new TestLease();
        using (var frame = new CaptureFrame(metadata, 1, 1, new byte[4], lease))
        {
            Assert(frame.BgraPixels.Length == 4, "Frame should expose declared pixels.");
        }

        Assert(lease.DisposeCount == 1, "Frame must dispose its lease exactly once.");
        AssertThrows<ArgumentException>(() => _ = new CaptureFrame(metadata, 2, 2, new byte[4]));
    }

    [Fact]
    public void TestMonotonicSequence()
    {
        var guard = new MonotonicSequenceGuard();
        Assert(guard.TryAccept(new SequenceNumber(0)), "First sequence should be accepted.");
        Assert(guard.TryAccept(new SequenceNumber(2)), "A newer sequence should be accepted.");
        Assert(!guard.TryAccept(new SequenceNumber(2)), "Duplicate sequence must be rejected.");
        Assert(!guard.TryAccept(new SequenceNumber(1)), "Late sequence must be rejected.");
        Assert(guard.LastAccepted == new SequenceNumber(2), "Latest accepted sequence should be retained.");
    }

    [Fact]
    public void TestSessionTransitions()
    {
        var allowed = new[]
        {
            (SessionState.Stopped, SessionState.Starting),
            (SessionState.Starting, SessionState.Running),
            (SessionState.Running, SessionState.Paused),
            (SessionState.Paused, SessionState.Running),
            (SessionState.Running, SessionState.Stopping),
            (SessionState.Stopping, SessionState.Stopped),
            (SessionState.Faulted, SessionState.Starting),
            (SessionState.Faulted, SessionState.Stopped),
        };

        foreach (var transition in allowed)
        {
            Assert(
                SessionTransitions.Transition(transition.Item1, transition.Item2) == transition.Item2,
                $"Expected {transition.Item1} -> {transition.Item2} to be allowed.");
        }

        AssertThrows<InvalidOperationException>(() =>
            SessionTransitions.Transition(SessionState.Stopped, SessionState.Running));
        AssertThrows<InvalidOperationException>(() =>
            SessionTransitions.Transition(SessionState.Running, SessionState.Running));
    }

    static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class TestLease : IFrameLease
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
