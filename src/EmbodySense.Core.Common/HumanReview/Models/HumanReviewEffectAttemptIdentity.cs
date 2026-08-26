namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Binds a Human Review reservation to one exact canonical actuator-effect attempt without retaining effect input or payload values.</summary>
/// <param name="SchemaVersion">The identity schema version, which must be 1.</param>
/// <param name="RunId">The exact durable run identity.</param>
/// <param name="GraphId">The exact governed-loop graph identity.</param>
/// <param name="RevisionId">The exact immutable revision identity.</param>
/// <param name="RevisionHash">The exact immutable executable revision hash.</param>
/// <param name="ExecutionGeneration">The exact positive execution generation that distinguishes a replaced or forked frontier for the same run.</param>
/// <param name="FrontierId">The exact parked frontier identity.</param>
/// <param name="FrontierVersion">The exact parked frontier version.</param>
/// <param name="FrontierHash">The exact parked frontier hash.</param>
/// <param name="NodeId">The exact originating graph-node identity.</param>
/// <param name="ActivationOrdinal">The exact activation ordinal, when the review binding names an activation.</param>
/// <param name="VisitOrdinal">The exact visit ordinal, when the review binding names a visit.</param>
/// <param name="NodeAttempt">The exact positive node-attempt coordinate.</param>
/// <param name="EffectId">The exact canonical effect-attempt identity.</param>
/// <param name="OperationId">The exact idempotency operation identity.</param>
/// <param name="EffectGeneration">The exact positive operation generation.</param>
/// <param name="ActuatorOperationId">The exact admitted actuator operation identity.</param>
/// <param name="CapabilityId">The exact capability identity.</param>
/// <param name="CapabilityVersion">The exact capability version.</param>
/// <param name="CapabilityDescriptorHash">The exact capability descriptor hash.</param>
/// <param name="ProviderId">The exact implementation provider identity.</param>
/// <param name="ImplementationId">The exact implementation identity.</param>
/// <param name="IntentHash">The exact immutable value-free effect intent hash.</param>
/// <param name="IdentityHash">The canonical hash of every prior identity field.</param>
public sealed record HumanReviewEffectAttemptIdentity(
    int SchemaVersion,
    string RunId,
    string GraphId,
    string RevisionId,
    string RevisionHash,
    long ExecutionGeneration,
    string FrontierId,
    long FrontierVersion,
    string FrontierHash,
    string NodeId,
    int? ActivationOrdinal,
    int? VisitOrdinal,
    int NodeAttempt,
    string EffectId,
    string OperationId,
    long EffectGeneration,
    string ActuatorOperationId,
    string CapabilityId,
    string CapabilityVersion,
    string CapabilityDescriptorHash,
    string ProviderId,
    string ImplementationId,
    string IntentHash,
    string IdentityHash);
