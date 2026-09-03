using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Reconciliation;

internal sealed class GovernedLoopEffectReconciliationServiceInput : IGovernedLoopEffectReconciliationInputSource
{
    private GovernedLoopEffectReconciliationInputReadStatus _status = GovernedLoopEffectReconciliationInputReadStatus.Found;

    internal GovernedLoopEffectReconciliationCaseReference? LastCase { get; private set; }

    internal GovernedLoopEffectReconciliationBinding? LastBinding { get; private set; }

    internal int ReadCalls { get; private set; }

    internal GovernedLoopEffectAttempt? Effect { get; set; }

    internal GovernedLoopFrontierPosture? Frontier { get; set; }

    internal GovernedActuatorInputEvidence? Input { get; set; }

    internal bool ThrowOnRead { get; set; }

    internal bool ReturnNullOnRead { get; set; }

    internal Action<int>? BeforeRead { get; set; }

    internal void SetStatus(GovernedLoopEffectReconciliationInputReadStatus status) => _status = status;

    public Task<GovernedLoopEffectReconciliationInputReadResult> ReadAsync(GovernedLoopEffectReconciliationInputReadRequest request, CancellationToken cancellationToken = default)
    {
        ReadCalls++;
        BeforeRead?.Invoke(ReadCalls);
        if (ThrowOnRead)
        {
            throw new IOException("The test input source is unavailable.");
        }

        if (ReturnNullOnRead)
        {
            return Task.FromResult<GovernedLoopEffectReconciliationInputReadResult>(null!);
        }

        LastCase = request.Case;
        LastBinding = request.Binding;
        if (_status != GovernedLoopEffectReconciliationInputReadStatus.Found)
        {
            return Task.FromResult(new GovernedLoopEffectReconciliationInputReadResult(_status, null, null, null, null, null));
        }

        return Task.FromResult(new GovernedLoopEffectReconciliationInputReadResult(GovernedLoopEffectReconciliationInputReadStatus.Found, request.Case, request.Binding, Effect, Frontier, Input));
    }
}
