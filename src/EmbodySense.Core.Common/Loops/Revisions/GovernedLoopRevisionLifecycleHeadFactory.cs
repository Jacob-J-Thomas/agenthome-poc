using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Loops.Revisions;

/// <summary>Creates validated optimistic governed-loop revision lifecycle heads.</summary>
public static class GovernedLoopRevisionLifecycleHeadFactory
{
    /// <summary>Creates one validated lifecycle-head projection.</summary>
    /// <param name="schemaVersion">The schema version, which must be 1.</param>
    /// <param name="graphId">The stable graph identifier.</param>
    /// <param name="lifecycleVersion">The positive optimistic version.</param>
    /// <param name="status">The closed lifecycle posture.</param>
    /// <param name="draftRevision">The exact current draft head, when present.</param>
    /// <param name="publishedRevision">The exact current or disabled publication pin, when present.</param>
    /// <param name="lastOperationId">The operation that produced the head.</param>
    /// <param name="updatedAtUtc">The trusted UTC projection time.</param>
    /// <returns>A validated immutable lifecycle head.</returns>
    /// <exception cref="ArgumentNullException">Thrown when a non-null publication pin has a <see langword="null"/> nested revision.</exception>
    /// <exception cref="ArgumentException">Thrown when the schema, graph, version, status, heads, operation, or timestamp is invalid.</exception>
    public static GovernedLoopRevisionLifecycleHead Create(
        int schemaVersion,
        string graphId,
        long lifecycleVersion,
        GovernedLoopRevisionLifecycleStatus status,
        GovernedLoopRevisionReference? draftRevision,
        GovernedLoopRevisionPublicationPin? publishedRevision,
        string lastOperationId,
        DateTimeOffset updatedAtUtc)
    {
        var head = new GovernedLoopRevisionLifecycleHead(
            schemaVersion,
            graphId,
            lifecycleVersion,
            status,
            GovernedLoopRevisionContractGuard.CopyOptionalRevision(draftRevision, nameof(draftRevision)),
            GovernedLoopRevisionContractGuard.CopyOptionalPin(publishedRevision, nameof(publishedRevision)),
            lastOperationId,
            updatedAtUtc);
        GovernedLoopRevisionContractGuard.RequireValid(GovernedLoopRevisionContractValidator.Validate(head), nameof(graphId));
        return head;
    }
}
