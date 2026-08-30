using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Loops.Sequential.Actions;
using EmbodySense.Core.Application.Loops.Sequential.Actions.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

internal sealed class PreDispatchApprovalWorkspaceActionExecutor : IGovernedLoopWorkspaceActionExecutor
{
    internal List<GovernedLoopWorkspaceActionExecutionRequest> Requests { get; } = [];

    internal int ActuationCount { get; private set; }

    internal GovernedLoopEffectAttempt? PreparedEffectAttempt { get; private set; }

    public Task<GovernedLoopWorkspaceActionExecutionResult> ExecuteAsync(GovernedLoopWorkspaceActionExecutionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        if (request.HumanReviewRelease is null)
        {
            PreparedEffectAttempt = Prepare(request);
            return Task.FromResult(new GovernedLoopWorkspaceActionExecutionResult(
                GovernedLoopWorkspaceActionExecutionStatus.ApprovalRequired,
                null,
                "The test actuator retained one exact pre-dispatch effect.",
                PreparedEffectAttempt));
        }

        ActuationCount++;
        var output = WorkspaceActionResultContract.Encode(WorkspaceActionResultContract.Create(WorkspaceActionResultStatus.Committed, Hash("after"), 1));
        return Task.FromResult(new GovernedLoopWorkspaceActionExecutionResult(GovernedLoopWorkspaceActionExecutionStatus.Completed, output, "The reviewed test actuator completed exactly once."));
    }

    private static GovernedLoopEffectAttempt Prepare(GovernedLoopWorkspaceActionExecutionRequest request)
    {
        var adapter = request.Dispatch.Anchor.AdapterBinding;
        var capability = adapter.AdmissionReceipt.Evidence.CapabilityAdmission.Pins.Single(pin => string.Equals(pin.DescriptorIdentity.Id.Value, "org.embodysense/workspace-command", StringComparison.Ordinal));
        return GovernedLoopEffectAttemptContract.Prepare(
            adapter.ExecutionBinding,
            request.Dispatch.Node.NodeId,
            request.Dispatch.Attempt,
            capability.DescriptorIdentity,
            capability.Implementation,
            "workspace/write",
            Hash("workspace-write-descriptor"),
            "review-effect-one",
            request.AttemptOperationId,
            1,
            Hash(request.InputJson),
            Hash("notes.txt"),
            Hash("expected-absent"),
            adapter.AdmissionReceipt.ContentHash,
            "before-review-effect-one",
            adapter.AdmissionReceipt.RecordedAtUtc.AddTicks(1));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
