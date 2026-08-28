using EmbodySense.Core.Application.HumanInput.Policies;
using EmbodySense.Core.Application.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;

namespace EmbodySense.Core.Application.Tests.HumanInput.Policies;

internal sealed class HumanInputPolicyResolutionTestSource : IHumanInputPolicySource
{
    internal Dictionary<HumanInputPolicyReference, HumanInputPolicySourceReadResult> Results { get; } = [];

    internal Action<HumanInputPolicyReference, CancellationToken>? BeforeRead { get; set; }

    public Task<HumanInputPolicySourceReadResult> ReadAsync(HumanInputPolicyReference reference, CancellationToken cancellationToken = default)
    {
        BeforeRead?.Invoke(reference, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Results.GetValueOrDefault(reference, new HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus.NotFound, null, 1)));
    }
}
