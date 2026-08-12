namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Identifies one already-retained ordered-runtime event that must be authenticated before canonical evidence is committed.</summary>
/// <param name="SchemaVersion">The request schema version, which must be 1.</param>
/// <param name="Dispatch">The exact guarded canonical node-dispatch coordinates.</param>
/// <param name="Disposition">The closed disposition projected from the ordered runtime result.</param>
/// <param name="OrderedLifecycleVersion">The exact durable ordered-run lifecycle version containing the event.</param>
/// <param name="OrderedEventSequence">The exact positive append-only event sequence.</param>
/// <param name="OrderedEventId">The exact durable ordered event identity.</param>
/// <remarks>
/// These coordinates are untrusted lookup hints. An implementation must authenticate the current durable run and exact-match
/// its canonical run/node/attempt/disposition/event/lifecycle coordinates before deriving or committing evidence. Exact replay
/// is idempotent; reuse of one canonical identity with a divergent event or outcome digest must be rejected.
/// </remarks>
public sealed record GovernedLoopSequentialOrderedNodeEvidenceRequest(
    int SchemaVersion,
    GovernedLoopSequentialNodeDispatchRequest Dispatch,
    GovernedLoopSequentialNodeHandlerResultStatus Disposition,
    int OrderedLifecycleVersion,
    long OrderedEventSequence,
    string OrderedEventId)
{
    /// <summary>Gets the only supported experimental request schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
