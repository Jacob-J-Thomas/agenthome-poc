using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.Failures.Models;

/// <summary>Retains one exact value-free schema-1 failure classification bound to a durable node activation.</summary>
public sealed partial record GovernedLoopFailureEvidence(
    int SchemaVersion,
    int MappingVersion,
    string EvidenceId,
    string WorkspaceId,
    string RunId,
    GovernedLoopRevisionReference Revision,
    long ExecutionGeneration,
    int ActivationOrdinal,
    int VisitOrdinal,
    string NodeId,
    int Attempt,
    GovernedLoopFailureClass FailureClass,
    string ServerCode,
    GovernedLoopFailureSource Source,
    GovernedLoopFailureEffectCertainty EffectCertainty,
    GovernedLoopFailureAuthorityPosture AuthorityPosture,
    GovernedLoopFailureHumanPosture HumanPosture,
    GovernedLoopFailureRetrySafety RetrySafety,
    GovernedLoopFailureSeverity Severity,
    int Precedence,
    IReadOnlyList<GovernedLoopFailureEvidenceReference> CausalEvidence,
    string? SafeDetail,
    DateTimeOffset ObservedAtUtc,
    string ContentHash)
{
    /// <summary>Gets the only supported persisted schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the server-owned taxonomy mapping version.</summary>
    public const int CurrentMappingVersion = 1;
}
