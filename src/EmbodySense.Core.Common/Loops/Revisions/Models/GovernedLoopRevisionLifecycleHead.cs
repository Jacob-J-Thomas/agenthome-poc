using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.Revisions.Models;

/// <summary>Projects the exact optimistic heads of one append-only governed-loop revision lifecycle.</summary>
/// <param name="SchemaVersion">The lifecycle-head schema version.</param>
/// <param name="GraphId">The stable graph identifier.</param>
/// <param name="LifecycleVersion">The positive optimistic version of the projection.</param>
/// <param name="Status">The closed graph lifecycle posture.</param>
/// <param name="DraftRevision">The exact current draft head, when one exists.</param>
/// <param name="PublishedRevision">The exact current or disabled publication pin, when one exists.</param>
/// <param name="LastOperationId">The operation that produced this projection version.</param>
/// <param name="UpdatedAtUtc">The trusted UTC projection time.</param>
public sealed record GovernedLoopRevisionLifecycleHead(
    int SchemaVersion,
    string GraphId,
    long LifecycleVersion,
    GovernedLoopRevisionLifecycleStatus Status,
    GovernedLoopRevisionReference? DraftRevision,
    GovernedLoopRevisionPublicationPin? PublishedRevision,
    string LastOperationId,
    DateTimeOffset UpdatedAtUtc);
