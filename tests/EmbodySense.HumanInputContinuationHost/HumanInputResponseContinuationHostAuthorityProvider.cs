using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.HumanInputContinuationHost;

internal sealed class HumanInputResponseContinuationHostAuthorityProvider(TimeProvider timeProvider) : ICustomLoopToolAuthorityProvider
{
    private readonly TimeProvider _timeProvider = timeProvider;

    public Task<CustomLoopToolAuthoritySnapshot> ResolveAsync(string roleId, IReadOnlyList<CustomLoopToolAssignment> admittedMaximum, CancellationToken cancellationToken = default)
    {
        var assignments = admittedMaximum.ToArray();
        return Task.FromResult(new CustomLoopToolAuthoritySnapshot(
            roleId,
            assignments,
            assignments,
            assignments,
            assignments,
            new string('a', 64),
            new string('b', 64),
            _timeProvider.GetUtcNow(),
            true,
            "The bounded process fixture preserves the admitted empty tool authority."));
    }
}
