using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Wait;
using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;

namespace EmbodySense.HumanInputContinuationHost;

internal sealed class HumanInputResponseContinuationHostContextPort : IGovernedLoopWaitOrderedResumePort
{
    public async Task<GovernedLoopWaitOrderedContext?> ResolveAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.SequentialAdapterBinding is not { } binding || run.SequentialInvocationSnapshot is not { } invocation)
        {
            return null;
        }

        var checkpoint = run.HumanInputWaitingCheckpoints.SingleOrDefault();
        if (checkpoint?.NodeConfiguration is not { } configuration)
        {
            return null;
        }

        var artifact = HumanInputResponseContinuationGraphFixture.CreateArtifact(configuration);
        var receipt = binding.AdmissionReceipt;
        var admission = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
            GovernedLoopAdmissionRequest.CurrentSchemaVersion,
            receipt.Intent.OperationId,
            binding.InvocationPayloadHash,
            string.Empty,
            receipt.Intent.Publication,
            receipt.Intent.AuthorityGrant,
            receipt.Intent.ActorId,
            receipt.Intent.Surface));
        var anchor = GovernedLoopSequentialRunAnchorGuard.Create(binding, admission, receipt, invocation, artifact);
        var plan = GovernedLoopSequentialPlanBuilder.Build(artifact);
        return anchor.Anchor is not null && plan.Plan is not null
            ? new GovernedLoopWaitOrderedContext(anchor.Anchor, plan.Plan, artifact)
            : null;
    }

    public Task<CustomLoopOrderedRunResult> ResumeAsync(GovernedLoopWaitOrderedResumeRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The external continuation host does not issue generic Wait re-entry.");
}
