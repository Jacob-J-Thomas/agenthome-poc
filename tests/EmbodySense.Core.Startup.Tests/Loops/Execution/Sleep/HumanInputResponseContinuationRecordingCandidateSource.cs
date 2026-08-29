using EmbodySense.Core.Application.HumanInput.Continuations;
using EmbodySense.Core.Application.HumanInput.Continuations.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanInputResponseContinuationRecordingCandidateSource(
    params HumanInputResponseContinuationRecoveryPage[] pages) : IHumanInputResponseContinuationCandidateSource
{
    private readonly Queue<HumanInputResponseContinuationRecoveryPage> _pages = new(pages);

    internal List<string?> Cursors { get; } = [];

    internal List<HumanInputResponseContinuationCandidate> Candidates { get; } = [];

    public Task<HumanInputResponseContinuationRecoveryPage> ListCandidatesAsync(
        int maximumCount,
        string? scanCursor,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(4, maximumCount);
        Cursors.Add(scanCursor);
        var page = _pages.Count > 0
            ? _pages.Dequeue()
            : new HumanInputResponseContinuationRecoveryPage(HumanInputResponseContinuationRecoveryPageStatus.Current, [], null, false);
        Candidates.AddRange(page.Candidates);
        return Task.FromResult(page);
    }
}
