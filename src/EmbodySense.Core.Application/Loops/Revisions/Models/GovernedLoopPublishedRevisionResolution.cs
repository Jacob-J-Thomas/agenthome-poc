using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Returns exact current and historical evidence for one caller-supplied publication pin.</summary>
/// <param name="Status">Whether the exact pin is active, disabled, archived, stale, absent, or unavailable.</param>
/// <param name="RequestedPin">The exact pin supplied by the caller, or <see langword="null"/> when an absent pin was rejected.</param>
/// <param name="Artifact">The exact immutable artifact when safely proved.</param>
/// <param name="ObservedLifecycleStatus">The observed graph lifecycle posture.</param>
/// <param name="ObservedLifecycleVersion">The observed optimistic lifecycle version, or zero when unavailable.</param>
/// <param name="ObservedLifecycleHeadOperationId">The operation that produced the observed head, or an empty value when unavailable.</param>
public sealed record GovernedLoopPublishedRevisionResolution(
    GovernedLoopPublishedRevisionResolutionStatus Status,
    GovernedLoopRevisionPublicationPin? RequestedPin,
    GovernedLoopRevisionArtifact? Artifact,
    GovernedLoopRevisionLifecycleStatus ObservedLifecycleStatus,
    long ObservedLifecycleVersion,
    string ObservedLifecycleHeadOperationId);
