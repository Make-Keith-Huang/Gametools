namespace GameLingoAI.QualityHarness;

public static class FaultProfiles
{
    private static readonly IReadOnlyList<ProviderStep> Recovery = Array.AsReadOnly(new ProviderStep[]
    {
        new(TimeSpan.FromMilliseconds(100), SimulatedOutcome.RateLimited),
        new(TimeSpan.FromMilliseconds(100), SimulatedOutcome.Success),
        new(TimeSpan.FromMilliseconds(500), SimulatedOutcome.ServerError),
        new(TimeSpan.FromMilliseconds(100), SimulatedOutcome.Success),
        new(TimeSpan.FromMilliseconds(750), SimulatedOutcome.Disconnect),
        new(TimeSpan.FromMilliseconds(100), SimulatedOutcome.Success),
        new(TimeSpan.FromMilliseconds(1500), SimulatedOutcome.PacketLoss),
        new(TimeSpan.FromMilliseconds(100), SimulatedOutcome.Success)
    });
    private static readonly IReadOnlyList<ProviderStep> Authentication = Array.AsReadOnly(new ProviderStep[]
        { new(TimeSpan.FromMilliseconds(100), SimulatedOutcome.AuthenticationRejected) });

    public static IReadOnlyList<ProviderStep> Select(string name) => name switch
    {
        "recovery" => Recovery,
        "auth" => Authentication,
        _ => throw new ArgumentException("Select a documented synthetic provider profile.", nameof(name))
    };
}
