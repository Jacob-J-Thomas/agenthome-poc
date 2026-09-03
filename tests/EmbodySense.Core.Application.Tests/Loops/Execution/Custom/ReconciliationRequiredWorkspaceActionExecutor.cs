using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Application.Loops.Sequential.Actions;
using EmbodySense.Core.Application.Loops.Sequential.Actions.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

internal sealed class ReconciliationRequiredWorkspaceActionExecutor : IGovernedLoopWorkspaceActionExecutor
{
    private static readonly DateTimeOffset _now = new(2026, 9, 3, 1, 0, 0, TimeSpan.Zero);

    internal List<GovernedLoopWorkspaceActionExecutionRequest> Requests { get; } = [];

    public Task<GovernedLoopWorkspaceActionExecutionResult> ExecuteAsync(GovernedLoopWorkspaceActionExecutionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        var effectRequest = request.Dispatch;
        var pin = request.Dispatch.Anchor.AdapterBinding.AdmissionReceipt.Evidence.CapabilityAdmission.Pins.Single(candidate => candidate.DescriptorIdentity.Id.Value == "org.embodysense/workspace-command");
        var prepared = GovernedLoopEffectAttemptContract.Prepare(
            effectRequest.Anchor.AdapterBinding.ExecutionBinding,
            effectRequest.Node.NodeId,
            effectRequest.Attempt,
            pin.DescriptorIdentity,
            pin.Implementation,
            "workspace/write",
            Hash("workspace-operation"),
            "effect-workspace-reconciliation",
            "operation-workspace-reconciliation",
            1,
            Hash(request.InputJson),
            Hash("workspace-target"),
            Hash("workspace-precondition"),
            effectRequest.Anchor.AdapterBinding.AdmissionReceiptHash,
            "before-workspace-reconciliation",
            _now);
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash("workspace-authority"), _now.AddSeconds(1));
        var crossed = GovernedLoopEffectAttemptContract.Advance(authorized, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, _now.AddSeconds(2));
        var ambiguous = GovernedLoopEffectAttemptContract.Advance(crossed, GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete, null, null, _now.AddSeconds(3));
        var binding = GovernedLoopEffectReconciliationContract.CreateBinding(
            effectRequest.Anchor.AdapterBinding.WorkspaceId,
            effectRequest.Activation.ActivationOrdinal,
            effectRequest.Activation.VisitOrdinal,
            ambiguous);
        return Task.FromResult(new GovernedLoopWorkspaceActionExecutionResult(GovernedLoopWorkspaceActionExecutionStatus.NeedsReview, null, "The exact workspace effect requires reconciliation.", ReconciliationBinding: binding));
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
