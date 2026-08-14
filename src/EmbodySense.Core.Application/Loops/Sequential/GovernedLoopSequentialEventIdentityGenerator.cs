namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Generates collision-resistant sequential materialization event identities without exposing run-ID creation.</summary>
public sealed class GovernedLoopSequentialEventIdentityGenerator : IGovernedLoopSequentialEventIdentityGenerator
{
    /// <inheritdoc />
    public string NewEventId() => CustomLoopGeneratedIdentifier.New("event");
}
