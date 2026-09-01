using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Core.Startup.Tests.HumanReview;
using EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeFactoryTests
{
    [Fact]
    public async Task Public_human_review_decision_authority_cancellation_propagates_without_mutating_canonical_review()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var blueprint = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync("run-authority-edge-cancel", "admission-authority-edge-cancel");
        await PersistAuthorityPendingHumanReviewAsync(workspace, blueprint);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var provider = new HumanReviewCancellationAuthorizationProvider();
        await using var runtime = await CreateRuntimeWithHumanReviewProviderAsync(workspace, executablePath, provider);

        using var cancellation = new CancellationTokenSource();
        var decision = runtime.HumanReview.DecideAsync(blueprint.Id, await LifecycleVersionAsync(runtime, blueprint.Id), "decision-authority-edge-cancel", HumanReviewDecisionKind.Approve, cancellationToken: cancellation.Token);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decision);
        var detail = await runtime.HumanReview.ReadAsync(blueprint.Id);

        Assert.Equal(HumanReviewReadStatus.Ready, detail.Status);
        Assert.Empty(detail.Detail!.Decisions);
        Assert.Equal(HumanReviewLifecycleStatus.Pending, detail.Detail.Summary.LifecycleStatus);
    }

    [Fact]
    public async Task Public_human_review_decision_authority_rejects_malformed_server_echoes_without_mutation()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var blueprint = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync("run-authority-edge-echoes", "admission-authority-edge-echoes");
        await PersistAuthorityPendingHumanReviewAsync(workspace, blueprint);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var provider = new HumanReviewDelegateAuthorizationProvider(request => request.DecisionOperationId switch
        {
            "decision-authority-edge-actor" => ReadyAuthorization(request) with { ActorId = string.Empty },
            "decision-authority-edge-role" => ReadyAuthorization(request) with { ReviewerRoleId = string.Empty },
            "decision-authority-edge-correlation" => ReadyAuthorization(request) with { CorrelationId = string.Empty },
            "decision-authority-edge-scopes" => ReadyAuthorization(request) with { ScopeIds = ImmutableArray<string>.Empty },
            _ => ReadyAuthorization(request),
        });
        await using var runtime = await CreateRuntimeWithHumanReviewProviderAsync(workspace, executablePath, provider);

        var unknown = await runtime.HumanReview.DecideAsync(blueprint.Id, await LifecycleVersionAsync(runtime, blueprint.Id), "decision-authority-edge-unknown", HumanReviewDecisionKind.Unknown);
        Assert.Equal(HumanReviewDecisionStatus.Invalid, unknown.Status);

        foreach (var operationId in new[]
        {
            "decision-authority-edge-actor",
            "decision-authority-edge-role",
            "decision-authority-edge-correlation",
            "decision-authority-edge-scopes",
        })
        {
            var result = await runtime.HumanReview.DecideAsync(blueprint.Id, await LifecycleVersionAsync(runtime, blueprint.Id), operationId, HumanReviewDecisionKind.Approve);

            Assert.Equal(HumanReviewDecisionStatus.Unavailable, result.Status);
            Assert.Null(result.Evidence);
        }

        var detail = await runtime.HumanReview.ReadAsync(blueprint.Id);
        Assert.Equal(HumanReviewReadStatus.Ready, detail.Status);
        Assert.Empty(detail.Detail!.Decisions);
        Assert.Equal(HumanReviewLifecycleStatus.Pending, detail.Detail.Summary.LifecycleStatus);
    }
}
