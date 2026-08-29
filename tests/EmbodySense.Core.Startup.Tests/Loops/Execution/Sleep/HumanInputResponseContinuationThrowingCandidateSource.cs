using EmbodySense.Core.Application.HumanInput.Continuations;
using EmbodySense.Core.Application.HumanInput.Continuations.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanInputResponseContinuationThrowingCandidateSource : IHumanInputResponseContinuationCandidateSource
{
    internal int Calls { get; private set; }

    public Task<HumanInputResponseContinuationRecoveryPage> ListCandidatesAsync(
        int maximumCount,
        string? scanCursor,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        throw new IOException("recovery source unavailable");
    }
}
