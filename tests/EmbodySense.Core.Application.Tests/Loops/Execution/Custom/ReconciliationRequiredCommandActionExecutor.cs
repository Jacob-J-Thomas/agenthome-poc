using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Loops.Sequential.Actions;
using EmbodySense.Core.Application.Loops.Sequential.Actions.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

internal sealed class ReconciliationRequiredCommandActionExecutor : IGovernedLoopCommandActionExecutor
{
    private static readonly DateTimeOffset _now = new(2026, 9, 3, 1, 0, 0, TimeSpan.Zero);

    internal List<GovernedLoopCommandActionExecutionRequest> Requests { get; } = [];

    public Task<GovernedLoopCommandActionExecutionResult> ExecuteAsync(GovernedLoopCommandActionExecutionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        var dispatch = request.Dispatch;
        var pin = dispatch.Anchor.AdapterBinding.AdmissionReceipt.Evidence.CapabilityAdmission.Pins.Single(candidate => candidate.DescriptorIdentity.Id.Value == "org.example/command");
        var prepared = GovernedLoopEffectAttemptContract.Prepare(
            dispatch.Anchor.AdapterBinding.ExecutionBinding,
            dispatch.Node.NodeId,
            dispatch.Attempt,
            pin.DescriptorIdentity,
            pin.Implementation,
            "command/run",
            Hash("command-operation"),
            "effect-command-reconciliation",
            "operation-command-reconciliation",
            1,
            Hash("command-input"),
            Hash("command-target"),
            Hash("command-precondition"),
            dispatch.Anchor.AdapterBinding.AdmissionReceiptHash,
            null,
            _now);
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash("command-authority"), _now.AddSeconds(1));
        var crossed = GovernedLoopEffectAttemptContract.Advance(authorized, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, _now.AddSeconds(2));
        var ambiguous = GovernedLoopEffectAttemptContract.Advance(crossed, GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete, null, null, _now.AddSeconds(3));
        var binding = GovernedLoopEffectReconciliationContract.CreateBinding(
            dispatch.Anchor.AdapterBinding.WorkspaceId,
            dispatch.Activation.ActivationOrdinal,
            dispatch.Activation.VisitOrdinal,
            ambiguous);
        return Task.FromResult(new GovernedLoopCommandActionExecutionResult(GovernedLoopCommandActionExecutionStatus.NeedsReview, null, "The exact command effect requires reconciliation.", ReconciliationBinding: binding));
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
