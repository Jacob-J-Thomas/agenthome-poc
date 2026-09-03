using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

internal sealed class RecordingGovernedLoopEffectReconciliationAdmissionService(
    GovernedLoopEffectReconciliationAdmissionStatus status,
    Func<CustomLoopRunRecord?> readDurableRun) : IGovernedLoopEffectReconciliationAdmissionService
{
    internal List<(CustomLoopRunRecord Run, GovernedLoopEffectReconciliationBinding Binding)> Requests { get; } = [];

    internal bool ObservedExactDurableRun { get; private set; }

    public Task<GovernedLoopEffectReconciliationAdmissionResult> AdmitAsync(
        CustomLoopRunRecord run,
        GovernedLoopEffectReconciliationBinding binding,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add((run, binding));
        var durable = readDurableRun();
        ObservedExactDurableRun = durable is not null && CustomLoopRunValidator.HasSameDurableVersion(durable, run);
        return Task.FromResult(new GovernedLoopEffectReconciliationAdmissionResult(status));
    }
}
