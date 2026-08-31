using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewOrderedReleaseProcessToolAuthorityProvider(TimeProvider timeProvider) : ICustomLoopToolAuthorityProvider
{
    public Task<CustomLoopToolAuthoritySnapshot> ResolveAsync(string roleId, IReadOnlyList<CustomLoopToolAssignment> admittedMaximum, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var admitted = admittedMaximum.ToArray();
        var catalog = new[] { CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search };
        var roleHash = CustomLoopTraceContentHash.Compute(roleId + "\n" + string.Join('\n', admitted.OrderBy(value => value)));
        var catalogHash = CustomLoopTraceContentHash.Compute(string.Join('\n', catalog));
        return Task.FromResult(new CustomLoopToolAuthoritySnapshot(roleId, admitted, admitted, catalog, admitted, roleHash, catalogHash, timeProvider.GetUtcNow(), true, "The exact process verifier authority remains current."));
    }
}
