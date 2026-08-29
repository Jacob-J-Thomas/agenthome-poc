using EmbodySense.Core.Application.HumanInput.Continuations;
using EmbodySense.Core.Application.HumanInput.Continuations.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanInputResponseContinuationRecordingWakePort(
    params HumanInputResponseContinuationWakeStatus[] statuses) : IHumanInputResponseContinuationWakePort
{
    private readonly Queue<HumanInputResponseContinuationWakeStatus> _statuses = new(statuses);

    internal List<HumanInputResponseContinuationCandidate> Candidates { get; } = [];

    public Task<HumanInputResponseContinuationWakeResult> WakeAsync(
        HumanInputResponseContinuationCandidate? candidate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Candidates.Add(candidate!);
        return Task.FromResult(new HumanInputResponseContinuationWakeResult(
            _statuses.Count > 0 ? _statuses.Dequeue() : HumanInputResponseContinuationWakeStatus.Replayed));
    }
}
