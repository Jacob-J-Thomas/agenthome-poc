using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Startup.Loops.Execution;

/// <summary>Projects the admitted read-only workspace catalog without creating a second runtime authority model.</summary>
/// <remarks>
/// This adapter only preserves the currently implemented List, Read, and Search assignment vocabulary for the ordered
/// runtime. The exact admitted grant and capability pin are revalidated by the governed effect-authority boundary before
/// provider transport, tool intake, and post-approval actuation.
/// </remarks>
/// <param name="timeProvider">The trusted clock used only to timestamp non-granting adapter evidence.</param>
public sealed class GovernedLoopReadOnlyWorkspaceToolAdapter(TimeProvider? timeProvider = null) : ICustomLoopToolAuthorityProvider
{
    private static readonly CustomLoopToolAssignment[] _catalog =
        [CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search];
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public Task<CustomLoopToolAuthoritySnapshot> ResolveAsync(
        string roleId,
        IReadOnlyList<CustomLoopToolAssignment> admittedMaximum,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);
        ArgumentNullException.ThrowIfNull(admittedMaximum);
        cancellationToken.ThrowIfCancellationRequested();

        var admitted = admittedMaximum.ToArray();
        var valid = admitted.Length <= _catalog.Length
            && admitted.Distinct().Count() == admitted.Length
            && admitted.All(_catalog.Contains);
        var effective = valid ? admitted.Order().ToArray() : [];
        var detail = valid
            ? "The non-granting read-only workspace adapter preserved the immutable admitted catalog subset; exact current grant authority is evaluated separately at every effect boundary."
            : "The admitted workspace assignment set was malformed or outside the implemented read-only catalog; no assignment was projected.";
        var evaluatedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();

        return Task.FromResult(new CustomLoopToolAuthoritySnapshot(
            roleId,
            admitted,
            _catalog.ToArray(),
            _catalog.ToArray(),
            effective,
            CustomLoopToolAuthorityProvider.ComputeRoleCeilingHash(roleId, _catalog),
            CustomLoopToolAuthorityProvider.ComputeCatalogHash(),
            evaluatedAtUtc,
            valid,
            detail));
    }
}
