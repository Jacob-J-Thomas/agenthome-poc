using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Describes one exact application-authorized atomic lifecycle persistence operation.</summary>
/// <param name="GraphId">The exact graph being mutated.</param>
/// <param name="ExpectedStoreGeneration">The global generation read and revalidated by the application.</param>
/// <param name="Operation">The terminal operation evidence to persist exactly once.</param>
/// <param name="ArtifactToAppend">A new immutable artifact, or <see langword="null"/> for a receipt-only or lifecycle-only mutation.</param>
/// <param name="HeadToWrite">The next lifecycle head, or <see langword="null"/> for a terminal receipt that leaves graph state unchanged.</param>
public sealed record GovernedLoopRevisionStoreMutation(
    string GraphId,
    long ExpectedStoreGeneration,
    GovernedLoopRevisionOperationEvidence Operation,
    GovernedLoopRevisionArtifact? ArtifactToAppend,
    GovernedLoopRevisionLifecycleHead? HeadToWrite);
