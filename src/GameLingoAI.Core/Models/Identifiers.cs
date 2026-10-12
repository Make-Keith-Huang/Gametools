using System.Text.Json.Serialization;

namespace GameLingoAI.Core.Models;

public readonly record struct SessionId
{
    public SessionId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static SessionId New() => new(Guid.NewGuid());
}

public readonly record struct CorrelationId
{
    public CorrelationId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static CorrelationId New() => new(Guid.NewGuid());
}

public readonly record struct ProfileId
{
    [JsonConstructor]
    public ProfileId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static ProfileId New() => new(Guid.NewGuid());
}

public readonly record struct SequenceNumber : IComparable<SequenceNumber>
{
    public SequenceNumber(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public long Value { get; }

    public SequenceNumber Next() => Value == long.MaxValue
        ? throw new OverflowException("The pipeline sequence is exhausted.")
        : new SequenceNumber(Value + 1);

    public int CompareTo(SequenceNumber other) => Value.CompareTo(other.Value);

    public static bool operator <(SequenceNumber left, SequenceNumber right) => left.Value < right.Value;

    public static bool operator <=(SequenceNumber left, SequenceNumber right) => left.Value <= right.Value;

    public static bool operator >(SequenceNumber left, SequenceNumber right) => left.Value > right.Value;

    public static bool operator >=(SequenceNumber left, SequenceNumber right) => left.Value >= right.Value;
}

/// <summary>Metadata propagated unchanged through every stage of one pipeline item.</summary>
public sealed record PipelineMetadata
{
    public PipelineMetadata(
        SessionId sessionId,
        SequenceNumber sequence,
        DateTimeOffset capturedAtUtc,
        CorrelationId correlationId)
    {
        if (capturedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Capture timestamps must be UTC.", nameof(capturedAtUtc));
        }

        SessionId = sessionId;
        Sequence = sequence;
        CapturedAtUtc = capturedAtUtc;
        CorrelationId = correlationId;
    }

    public SessionId SessionId { get; }

    public SequenceNumber Sequence { get; }

    public DateTimeOffset CapturedAtUtc { get; }

    public CorrelationId CorrelationId { get; }
}
