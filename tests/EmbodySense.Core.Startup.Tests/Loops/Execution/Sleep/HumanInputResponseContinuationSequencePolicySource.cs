using EmbodySense.Core.Application.HumanInput.Policies;
using EmbodySense.Core.Application.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanInputResponseContinuationSequencePolicySource(
    params Func<HumanInputPolicyReference, CancellationToken, Task<HumanInputPolicySourceReadResult>>[] reads) : IHumanInputPolicySource
{
    private readonly Queue<Func<HumanInputPolicyReference, CancellationToken, Task<HumanInputPolicySourceReadResult>>> _reads = new(reads);

    internal List<HumanInputPolicyReference> References { get; } = [];

    public Task<HumanInputPolicySourceReadResult> ReadAsync(
        HumanInputPolicyReference reference,
        CancellationToken cancellationToken = default)
    {
        References.Add(reference);
        return (_reads.Count > 0 ? _reads.Dequeue() : HealthyEmpty)(reference, cancellationToken);
    }

    private static Task<HumanInputPolicySourceReadResult> HealthyEmpty(
        HumanInputPolicyReference reference,
        CancellationToken cancellationToken)
    {
        _ = reference;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus.NotFound, null, 0));
    }
}
