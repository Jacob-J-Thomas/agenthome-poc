using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation;

internal static class GovernedLoopEffectReconciliationOperationResultGuard
{
    internal static GovernedLoopEffectReconciliationOperationStatus RequireStatus(GovernedLoopEffectReconciliationOperationStatus status)
        => Enum.IsDefined(status) ? status : throw new ArgumentOutOfRangeException(nameof(status));

    internal static GovernedLoopEffectReconciliationCase? CopyCase(GovernedLoopEffectReconciliationOperationStatus status, GovernedLoopEffectReconciliationCase? value)
    {
        var requiresState = status is GovernedLoopEffectReconciliationOperationStatus.Applied
            or GovernedLoopEffectReconciliationOperationStatus.Replayed
            or GovernedLoopEffectReconciliationOperationStatus.Found;
        var mayContainState = requiresState || status == GovernedLoopEffectReconciliationOperationStatus.Conflict;
        if (!mayContainState && value is not null)
        {
            throw new ArgumentException("A failed reconciliation operation must not disclose case state.", nameof(value));
        }

        if (requiresState && value is null)
        {
            throw new ArgumentException("A state-bearing reconciliation operation requires its exact case.", nameof(value));
        }

        return value is null ? null : GovernedLoopEffectReconciliationContractCopy.Copy(value);
    }

    internal static GovernedLoopEffectAttempt? CopyEffect(GovernedLoopEffectReconciliationOperationStatus status, GovernedLoopEffectAttempt? value)
    {
        var mayContainState = status is GovernedLoopEffectReconciliationOperationStatus.Applied
            or GovernedLoopEffectReconciliationOperationStatus.Replayed
            or GovernedLoopEffectReconciliationOperationStatus.Conflict;
        if (status == GovernedLoopEffectReconciliationOperationStatus.Found)
        {
            if (value is not null)
            {
                throw new ArgumentException("A case-only read must not disclose an unrequested effect head.", nameof(value));
            }

            return null;
        }

        if (!mayContainState && value is not null)
        {
            throw new ArgumentException("A failed reconciliation operation must not disclose effect state.", nameof(value));
        }

        if (status is GovernedLoopEffectReconciliationOperationStatus.Applied or GovernedLoopEffectReconciliationOperationStatus.Replayed && value is null)
        {
            throw new ArgumentException("A state-bearing reconciliation operation requires its exact effect head.", nameof(value));
        }

        return value is null ? null : GovernedLoopEffectReconciliationApplicationCopy.Copy(value);
    }
}
