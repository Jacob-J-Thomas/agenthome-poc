using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Startup.Loops.Execution.Effects;
using EmbodySense.Tests.Support;

namespace EmbodySense.CancellationHost.Persistence;

internal static class HumanReviewOrderedReleaseProcessRuntimeFactory
{
    internal static GovernedLoopSequentialOrderedRuntimeAdapter Create(
        CustomLoopRunStore runStore,
        WorkspacePaths paths,
        string markerPath,
        TimeProvider timeProvider,
        bool crashAfterMarker,
        string? ownerReadyPath = null,
        string? ownerReleasePath = null)
    {
        var transaction = new HumanReviewOrderedReleaseProcessAuthorityTransaction();
        var operation = new HumanReviewOrderedReleaseProcessMarkerOperation(markerPath, crashAfterMarker, ownerReadyPath, ownerReleasePath);
        var catalog = new HumanReviewOrderedReleaseProcessCatalog(operation);
        var attempts = new GovernedLoopEffectAttemptStore(paths);
        var effects = new GovernedLoopEffectAttemptService(
            catalog,
            attempts,
            new HumanReviewOrderedReleaseProcessEffectAuthority(transaction, timeProvider),
            new CanonicalHumanReviewEffectEvidenceSource(runStore, attempts),
            timeProvider);
        var workspaceActions = new GovernedLoopWorkspaceActionExecutor(new GovernedLoopEffectAttemptFacade(catalog, effects));
        var audit = new AuditLog(paths);
        var runner = new CustomLoopOrderedRunner(
            runStore,
            new CustomLoopContextResolver(),
            new HumanReviewOrderedReleaseProcessInferenceExecutor(),
            new HumanReviewOrderedReleaseProcessConversationPublisher(),
            audit,
            new HumanReviewOrderedReleaseProcessToolAuthorityProvider(timeProvider),
            timeProvider,
            capabilityAdmissionService: new TestCapabilityAdmissionService(),
            firstBoundRunCompletionBoundary: new GovernedLoopFirstBoundRunCompletionBoundary(new HumanReviewOrderedReleaseProcessAuthorityUsageStore(), transaction, timeProvider),
            workspaceActionExecutor: workspaceActions,
            humanReviewAdmissionService: new HumanReviewAdmissionService(runStore));
        return new GovernedLoopSequentialOrderedRuntimeAdapter(runner, runStore, runStore, audit);
    }
}
