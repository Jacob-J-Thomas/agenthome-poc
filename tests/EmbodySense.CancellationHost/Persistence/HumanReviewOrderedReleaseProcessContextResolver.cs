using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Wait;
using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewOrderedReleaseProcessContextResolver : IGovernedLoopWaitOrderedResumePort
{
    public Task<GovernedLoopWaitOrderedContext?> ResolveAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var binding = run.SequentialAdapterBinding;
        var invocation = run.SequentialInvocationSnapshot;
        if (binding is null || invocation is null)
        {
            return Task.FromResult<GovernedLoopWaitOrderedContext?>(null);
        }
        var artifact = ResolveArtifact(binding);
        if (artifact is null)
        {
            return Task.FromResult<GovernedLoopWaitOrderedContext?>(null);
        }
        var plan = GovernedLoopSequentialPlanBuilder.Build(artifact);
        if (plan.Plan is null
            || !string.Equals(binding.GraphArtifactHash, artifact.ArtifactHash, StringComparison.Ordinal)
            || !string.Equals(binding.GraphLayoutHash, artifact.LayoutHash, StringComparison.Ordinal))
        {
            return Task.FromResult<GovernedLoopWaitOrderedContext?>(null);
        }

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
        return Task.FromResult<GovernedLoopWaitOrderedContext?>(anchor.Anchor is null ? null : new GovernedLoopWaitOrderedContext(anchor.Anchor, plan.Plan, artifact));
    }

    public Task<CustomLoopOrderedRunResult> ResumeAsync(GovernedLoopWaitOrderedResumeRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new CustomLoopOrderedRunResult(CustomLoopOrderedRunStatus.InvalidState, null, "The process verifier proves release persistence without owning production ordered re-entry."));

    private static GovernedLoopGraphRevisionArtifact? ResolveArtifact(GovernedLoopSequentialAdapterBinding binding)
        => new[] { HumanReviewOrderedReleaseGraphFixture.Artifact(), HumanReviewOrderedReleaseGraphFixture.PreDispatchEffectArtifact() }
            .SingleOrDefault(candidate => string.Equals(candidate.ArtifactHash, binding.GraphArtifactHash, StringComparison.Ordinal)
                && string.Equals(candidate.LayoutHash, binding.GraphLayoutHash, StringComparison.Ordinal));
}
