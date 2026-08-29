using EmbodySense.Core.Application.HumanInput.Continuations;
using EmbodySense.Core.Application.HumanInput.Continuations.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanInputResponseContinuationMissingCandidateSource : IHumanInputResponseContinuationCandidateSource
{
    public Task<HumanInputResponseContinuationRecoveryPage> ListCandidatesAsync(
        int maximumCount,
        string? scanCursor,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<HumanInputResponseContinuationRecoveryPage>(null!);
    }
}
