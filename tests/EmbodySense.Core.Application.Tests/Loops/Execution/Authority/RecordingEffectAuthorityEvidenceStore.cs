using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Authority;

internal sealed class RecordingEffectAuthorityEvidenceStore : IGovernedLoopEffectAuthorityEvidenceStore
{
    internal GovernedLoopEffectAuthorityEvidenceStoreStatus Status { get; set; } = GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended;

    internal Queue<GovernedLoopEffectAuthorityEvidenceStoreStatus> Statuses { get; } = [];

    internal List<GovernedLoopEffectAuthorityDecision> Decisions { get; } = [];

    internal Exception? Exception { get; set; }

    internal Action<GovernedLoopEffectAuthorityDecision>? BeforeReturn { get; set; }

    public Task<GovernedLoopEffectAuthorityEvidenceStoreResult> AppendAsync(GovernedLoopEffectAuthorityDecision decision, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Exception is not null)
        {
            return Task.FromException<GovernedLoopEffectAuthorityEvidenceStoreResult>(Exception);
        }

        Decisions.Add(decision);
        var status = Statuses.Count > 0 ? Statuses.Dequeue() : Status;
        var hash = status is GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended or GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent
            ? decision.ContentHash
            : null;
        BeforeReturn?.Invoke(decision);
        return Task.FromResult(new GovernedLoopEffectAuthorityEvidenceStoreResult(status, hash));
    }
}
