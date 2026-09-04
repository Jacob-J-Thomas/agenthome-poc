using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.HumanInput.Requests;
using EmbodySense.Core.Persistence.Tests.HumanInput.Requests;
using EmbodySense.Core.Startup.HumanInput.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Tests.Runtime;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.HumanInput;

public sealed class HumanInputRuntimeFacadeRerouteReplayTests
{
    [Fact]
    public async Task Reroute_replay_requires_the_selected_candidate_to_match_durable_evidence()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var store = new HumanInputRequestStore(new WorkspacePaths(workspace.RootPath), new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath));
        var create = AgentRuntimeFactoryTests.CreateFreshHumanInputMutation(
            workspace.RootPath,
            "request-reroute-replay-selection",
            "version-reroute-replay-selection",
            "create-reroute-replay-selection",
            HumanInputRequestStoreTestData.HashA);
        var request = Assert.IsType<HumanInputRequest>(create.RequestToAppend);
        var head = Assert.IsType<HumanInputRequestLifecycleHead>(create.PrimaryHeadToWrite);
        var selectedCandidate = HumanInputRequestStoreTestData.Rehash(request with
        {
            RequestVersionId = "version-reroute-selected",
            EligibleRespondents = [new HumanInputEligibleRespondent("user-two", "role-two", "route-two")],
            RequestHash = string.Empty
        });
        var otherCandidate = HumanInputRequestStoreTestData.Rehash(request with
        {
            RequestVersionId = "version-reroute-other",
            EligibleRespondents = [new HumanInputEligibleRespondent("user-three", "role-three", "route-three")],
            RequestHash = string.Empty
        });
        var reroute = AgentRuntimeFactoryTests.CreateDurableLifecycleReplayMutation(
            HumanInputRequestLifecycleOperationKind.Reroute,
            request,
            head,
            1,
            "reroute-replay-selection",
            selectedCandidate);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(create)).Status);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(reroute)).Status);

        var provider = new HumanInputRuntimeFacadeTestAuthorityProvider
        {
            LifecycleGrantReference = reroute.Operation.GrantReference
        };
        provider.LifecycleCandidates.Add("candidate-selected", selectedCandidate);
        provider.LifecycleCandidates.Add("candidate-other", otherCandidate);
        await using var runtime = await AgentRuntimeFactoryTests.CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web, humanInputAuthorityProvider: provider);
        var input = new HumanInputLifecycleOperationInput(
            "reroute-replay-selection",
            HumanInputRequestLifecycleOperationKind.Reroute,
            request.RequestId,
            head.LifecycleVersion,
            head.Status,
            HumanInputRequestStoreTestData.Reference(request),
            "candidate-selected",
            "Replay one exact lifecycle operation.");

        var replayed = await runtime.HumanInput.SubmitLifecycleAsync(input);
        var conflict = await runtime.HumanInput.SubmitLifecycleAsync(input with { CandidateKey = "candidate-other" });
        provider.LifecycleTermsStatus = AgentRuntimeHumanInputAuthorityStatus.Unavailable;
        var replayedAfterRegistryLoss = await runtime.HumanInput.SubmitLifecycleAsync(input with { CandidateKey = null });
        var unknownCandidateAfterRegistryLoss = await runtime.HumanInput.SubmitLifecycleAsync(input with { CandidateKey = "candidate-unrelated" });
        provider.LifecycleTermsStatus = AgentRuntimeHumanInputAuthorityStatus.Ready;
        provider.ThrowDuringLifecycleTerms = true;
        var providerFailure = await runtime.HumanInput.SubmitLifecycleAsync(input with { CandidateKey = "candidate-provider-failure" });
        provider.ThrowDuringLifecycleTerms = false;
        provider.ReturnNullLifecycleTerms = true;
        var nullTerms = await runtime.HumanInput.SubmitLifecycleAsync(input with { CandidateKey = "candidate-null-terms" });
        provider.ReturnNullLifecycleTerms = false;
        provider.LifecycleTermsStatus = AgentRuntimeHumanInputAuthorityStatus.Denied;
        var denied = await runtime.HumanInput.SubmitLifecycleAsync(input with { CandidateKey = "candidate-denied" });
        provider.LifecycleTermsStatus = AgentRuntimeHumanInputAuthorityStatus.Ready;
        var missingCandidate = await runtime.HumanInput.SubmitLifecycleAsync(input with { CandidateKey = "candidate-missing" });
        provider.LifecycleGrantReference = null;
        var missingGrant = await runtime.HumanInput.SubmitLifecycleAsync(input);
        provider.LifecycleGrantReference = reroute.Operation.GrantReference;
        provider.LifecycleTermsStatus = AgentRuntimeHumanInputAuthorityStatus.Unknown;
        var unknownTerms = await runtime.HumanInput.SubmitLifecycleAsync(input);
        provider.LifecycleTermsStatus = AgentRuntimeHumanInputAuthorityStatus.Ready;
        provider.DelayLifecycleTermsUntilCancellation = true;
        using var cancellation = new CancellationTokenSource();
        provider.LifecycleTermsEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingCancellation = runtime.HumanInput.SubmitLifecycleAsync(input, cancellation.Token);
        await provider.LifecycleTermsEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingCancellation);
        provider.DelayLifecycleTermsUntilCancellation = false;
        provider.LifecycleTermsEntered = null;
        var posture = await runtime.HumanInput.ReadAsync(request.RequestId);
        var currentPosture = Assert.IsType<HumanInputRequestPosture>(posture.Request);
        var nonPersistedInput = new HumanInputLifecycleOperationInput(
            "cancel-non-persisted-terms-resolution",
            HumanInputRequestLifecycleOperationKind.Cancel,
            currentPosture.RequestId,
            currentPosture.LifecycleVersion,
            currentPosture.Status,
            currentPosture.CurrentRequest,
            null,
            "Cancel while resolving current lifecycle terms.");
        provider.DelayLifecycleTermsUntilCancellation = true;
        provider.LifecycleTermsEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var nonPersistedCancellation = new CancellationTokenSource())
        {
            var pendingNonPersistedCancellation = runtime.HumanInput.SubmitLifecycleAsync(nonPersistedInput, nonPersistedCancellation.Token);
            await provider.LifecycleTermsEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            nonPersistedCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingNonPersistedCancellation);
        }

        provider.DelayLifecycleTermsUntilCancellation = false;
        provider.LifecycleTermsEntered = null;

        Assert.Equal(HumanInputOperationStatus.Replayed, replayed.Status);
        Assert.Equal(HumanInputOperationStatus.Conflict, conflict.Status);
        Assert.Equal(HumanInputOperationStatus.Replayed, replayedAfterRegistryLoss.Status);
        Assert.Equal(HumanInputOperationStatus.Conflict, unknownCandidateAfterRegistryLoss.Status);
        Assert.Equal(HumanInputOperationStatus.Unavailable, providerFailure.Status);
        Assert.Equal(HumanInputOperationStatus.Unavailable, nullTerms.Status);
        Assert.Equal(HumanInputOperationStatus.Denied, denied.Status);
        Assert.Equal(HumanInputOperationStatus.Unavailable, missingCandidate.Status);
        Assert.Equal(HumanInputOperationStatus.Unavailable, missingGrant.Status);
        Assert.Equal(HumanInputOperationStatus.Unavailable, unknownTerms.Status);
        Assert.Equal(selectedCandidate.RequestVersionId, posture.Request!.CurrentRequest.RequestVersionId);
        Assert.Equal(12, provider.LifecycleTermsResolutions);
        Assert.Equal(2, provider.LifecycleAuthorizations);
    }
}
