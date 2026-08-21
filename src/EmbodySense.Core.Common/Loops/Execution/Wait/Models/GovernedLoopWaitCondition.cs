using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.Execution.Wait.Models;

/// <summary>Records one immutable typed condition admitted from the closed schema-1 Wait catalog.</summary>
/// <param name="SchemaVersion">The condition schema version, which must be 1.</param>
/// <param name="Descriptor">The exact admitted Wait descriptor.</param>
/// <param name="ParameterKind">The exact typed parameter kind selected by the descriptor.</param>
/// <param name="WakeDeadlineUtc">The exact UTC deadline, present only for timestamp waits.</param>
/// <param name="AuthenticatedEventReference">The bounded governed event reference, present only for authenticated-event waits.</param>
/// <param name="ContentHash">The canonical hash over the complete condition except this field.</param>
public sealed record GovernedLoopWaitCondition(
    int SchemaVersion,
    GovernedLoopNodeDescriptor Descriptor,
    GovernedLoopWaitParameterKind ParameterKind,
    DateTimeOffset? WakeDeadlineUtc,
    string? AuthenticatedEventReference,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental condition schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopWaitContractLimits.CurrentSchemaVersion;

    /// <summary>Gets a defensive copy of the admitted descriptor.</summary>
    public GovernedLoopNodeDescriptor Descriptor { get; } = Descriptor is null ? null! : Descriptor with { };
}
