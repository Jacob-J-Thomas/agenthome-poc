namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Projects one bounded, value-free, hash-authenticated canonical node-failure classification.</summary>
/// <param name="SchemaVersion">The failure evidence schema version.</param>
/// <param name="MappingVersion">The server-owned taxonomy mapping version.</param>
/// <param name="EvidenceId">The retained failure evidence identity.</param>
/// <param name="WorkspaceId">The exact bound workspace identity.</param>
/// <param name="RunId">The exact bound run identity.</param>
/// <param name="GraphId">The exact bound graph identity.</param>
/// <param name="RevisionId">The exact bound immutable revision identity.</param>
/// <param name="ExecutableHash">The exact bound executable revision hash.</param>
/// <param name="ExecutionGeneration">The exact bound execution generation.</param>
/// <param name="ActivationOrdinal">The exact activation-history ordinal.</param>
/// <param name="VisitOrdinal">The exact node visit ordinal.</param>
/// <param name="NodeId">The exact immutable graph node identity.</param>
/// <param name="Attempt">The exact node attempt.</param>
/// <param name="FailureClass">The canonical failure class.</param>
/// <param name="ServerCode">The bounded server-owned failure code.</param>
/// <param name="Source">The canonical source subsystem.</param>
/// <param name="EffectCertainty">The proved dispatch or effect certainty.</param>
/// <param name="AuthorityPosture">The current authority posture represented by the evidence.</param>
/// <param name="HumanPosture">The authenticated human disposition represented by the evidence.</param>
/// <param name="RetrySafety">The evidence-derived retry-safety posture; this grants no retry authority.</param>
/// <param name="Severity">The canonical severity.</param>
/// <param name="Precedence">The server-owned precedence retained when the observation set was classified.</param>
/// <param name="CausalEvidence">The exact ordered causal evidence references.</param>
/// <param name="SafeDetail">Optional bounded redacted detail.</param>
/// <param name="ObservedAtUtc">The trusted UTC classification time.</param>
/// <param name="ContentHash">The lowercase SHA-256 digest over the complete failure evidence.</param>
public sealed record LoopRunFailureEvidenceSnapshot(
    int SchemaVersion,
    int MappingVersion,
    string EvidenceId,
    string WorkspaceId,
    string RunId,
    string GraphId,
    string RevisionId,
    string ExecutableHash,
    long ExecutionGeneration,
    int ActivationOrdinal,
    int VisitOrdinal,
    string NodeId,
    int Attempt,
    string FailureClass,
    string ServerCode,
    string Source,
    string EffectCertainty,
    string AuthorityPosture,
    string HumanPosture,
    string RetrySafety,
    string Severity,
    int Precedence,
    IReadOnlyList<LoopRunFailureEvidenceReferenceSnapshot> CausalEvidence,
    string? SafeDetail,
    DateTimeOffset ObservedAtUtc,
    string ContentHash);
