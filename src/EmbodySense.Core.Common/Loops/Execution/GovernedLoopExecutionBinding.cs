using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Binds every canonical execution plane to one run, exact immutable graph revision, and execution generation.</summary>
/// <remarks>Construction revalidates the exact revision reference rather than accepting a partially populated identity.</remarks>
public sealed record GovernedLoopExecutionBinding
{
    private GovernedLoopExecutionBinding(int schemaVersion, string runId, GovernedLoopRevisionReference revision, long executionGeneration)
    {
        SchemaVersion = schemaVersion;
        RunId = runId;
        Revision = revision;
        ExecutionGeneration = executionGeneration;
    }

    /// <summary>Gets the schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the stable admitted-run identity.</summary>
    public string RunId { get; }

    /// <summary>Gets the exact immutable executable graph revision.</summary>
    public GovernedLoopRevisionReference Revision { get; }

    /// <summary>Gets the positive execution generation used to distinguish a replacement or forked frontier.</summary>
    public long ExecutionGeneration { get; }

    /// <summary>Creates a validated exact execution binding.</summary>
    /// <param name="schemaVersion">The schema version, which must be 1.</param>
    /// <param name="runId">The stable run identity.</param>
    /// <param name="revision">The exact immutable executable graph revision.</param>
    /// <param name="executionGeneration">The positive bounded execution generation.</param>
    /// <returns>The validated binding.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="revision"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="executionGeneration"/> is outside the supported positive range.</exception>
    /// <exception cref="ArgumentException">Thrown when the schema, run identity, or exact revision reference is not canonical.</exception>
    public static GovernedLoopExecutionBinding Create(int schemaVersion, string runId, GovernedLoopRevisionReference revision, long executionGeneration)
    {
        GovernedLoopExecutionContractGuard.RequireSchema(schemaVersion, nameof(schemaVersion));
        ArgumentNullException.ThrowIfNull(revision);
        var revisionValidation = GovernedLoopRevisionReference.Create(revision.SchemaVersion, revision.GraphId, revision.RevisionId, revision.ExecutableHash);
        return new GovernedLoopExecutionBinding(
            schemaVersion,
            GovernedLoopExecutionContractGuard.RequireIdentifier(runId, nameof(runId)),
            revisionValidation,
            GovernedLoopExecutionContractGuard.RequirePositiveVersion(executionGeneration, nameof(executionGeneration), GovernedLoopExecutionLimits.MaxExecutionGeneration));
    }
}
