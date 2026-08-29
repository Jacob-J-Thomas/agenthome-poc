using EmbodySense.Core.Application.HumanInput.Continuations;
using EmbodySense.Core.Application.HumanInput.Catalog;
using EmbodySense.Core.Application.HumanInput.Catalog.Models;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Publication;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Posture.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.HumanInput.Continuations;
using EmbodySense.Core.Persistence.HumanInput.Policies;
using EmbodySense.Core.Persistence.HumanInput.Requests;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Execution.Authority;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep;
using EmbodySense.Core.Persistence.Tests.HumanInput.Continuations;
using EmbodySense.Core.Persistence.Tests.HumanInput.Requests;
using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;
using EmbodySense.HumanInputContinuationHost;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

public sealed class HumanInputResponseContinuationWorkRunnerIntegrationTests
{
    [Fact]
    public async Task Production_publication_survives_restart_and_exposes_one_checkpoint_request_before_response_continues_once()
    {
        using var workspace = new TestWorkspace();
        var now = HumanInputResponseContinuationRecoveryFixture.Now.AddMinutes(1).AddSeconds(30);
        var pathsA = new WorkspacePaths(workspace.RootPath);
        var context = await SeedWaitingRunAsync(pathsA, now);
        var grantReference = new AuthorityGrantReference(context.Grant.GrantId, context.Grant.Revision, context.Grant.ContentHash);
        var activeGrant = new AuthorityGrantResolution(
            AuthorityGrantResolutionStatus.Active,
            grantReference,
            context.Grant,
            context.Grant.RequestedCeiling,
            new string('d', 64),
            now);
        using var runsA = new CustomLoopRunStore(pathsA, new HumanInputResponseContinuationFixedTimeProvider(now));
        var requestsA = new HumanInputRequestStore(pathsA);
        var publicationA = new HumanInputRequestPublicationService(
            runsA,
            requestsA,
            new RecordingAuthorityGrantResolver(activeGrant),
            new HumanInputContinuationAuthorityTransaction(),
            context.Binding.WorkspaceId,
            new HumanInputResponseContinuationFixedTimeProvider(now));
        var workerA = CreateWorker(pathsA, runsA, now, publicationA);

        var awaitingResponse = await workerA.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var catalogA = (IHumanInputRequestCatalog)requestsA;
        var listed = await catalogA.ListAsync(new HumanInputRequestCatalogPageRequest(4));
        var inspected = await catalogA.ReadAsync(context.Checkpoint.Request.RequestId);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, awaitingResponse?.Status);
        Assert.Equal(HumanInputRequestCatalogPageStatus.Ready, listed.Status);
        var visible = Assert.Single(listed.Entries);
        Assert.Equal(context.Checkpoint.Request.RequestId, visible.Lifecycle.Head.RequestId);
        Assert.Equal(HumanInputRequestCatalogReadStatus.Ready, inspected.Status);
        Assert.Equal(context.Checkpoint.Request.RequestHash, inspected.Entry?.Lifecycle.Head.CurrentRequest.RequestHash);

        var pathsB = new WorkspacePaths(workspace.RootPath);
        using var runsB = new CustomLoopRunStore(pathsB, new HumanInputResponseContinuationFixedTimeProvider(now));
        var requestsB = new HumanInputRequestStore(pathsB);
        var unavailableGrant = activeGrant with
        {
            Status = AuthorityGrantResolutionStatus.Unavailable,
            Grant = null,
            EffectiveCeiling = AuthorityCeilingIntersection.EmptyCeiling(),
            DependencyEvidenceHash = string.Empty,
            EvaluatedAtUtc = default,
        };
        var publicationB = new HumanInputRequestPublicationService(
            runsB,
            requestsB,
            new RecordingAuthorityGrantResolver(unavailableGrant),
            new HumanInputContinuationAuthorityTransaction(),
            context.Binding.WorkspaceId,
            new HumanInputResponseContinuationFixedTimeProvider(now));
        var workerB = CreateWorker(pathsB, runsB, now, publicationB);
        var replayedBeforeResponse = await workerB.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        await SubmitResponseAsync(requestsB, context, now);

        var pathsC = new WorkspacePaths(workspace.RootPath);
        using var runsC = new CustomLoopRunStore(pathsC, new HumanInputResponseContinuationFixedTimeProvider(now));
        var requestsC = new HumanInputRequestStore(pathsC);
        var publicationC = new HumanInputRequestPublicationService(
            runsC,
            requestsC,
            new RecordingAuthorityGrantResolver(unavailableGrant),
            new HumanInputContinuationAuthorityTransaction(),
            context.Binding.WorkspaceId,
            new HumanInputResponseContinuationFixedTimeProvider(now));
        var workerC = CreateWorker(pathsC, runsC, now, publicationC);
        var completedResult = await workerC.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var completed = await runsC.GetAsync(context.Run.Id);
        var cleanTail = await workerC.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var lifecycle = await requestsC.ReadAsync(context.Checkpoint.Request.RequestId);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, replayedBeforeResponse?.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Completed, completedResult?.Status);
        AssertCompletedWithoutPrivateResponse(Assert.IsType<CustomLoopRunRecord>(completed), await ReadAuditEvidenceAsync(pathsB));
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, cleanTail?.Status);
        var lifecycleSnapshot = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(lifecycle.PrimarySnapshot);
        Assert.Single(lifecycleSnapshot.RequestVersions);
        Assert.Single(lifecycleSnapshot.Operations);
    }

    [Fact]
    public async Task Fresh_worker_finishes_the_submitted_waiting_run_and_a_restart_reaches_the_clean_tail_without_duplicate_evidence()
    {
        using var workspace = new TestWorkspace();
        var now = HumanInputResponseContinuationRecoveryFixture.Now.AddMinutes(1).AddSeconds(30);
        var pathsA = new WorkspacePaths(workspace.RootPath);
        var context = await SeedSubmittedWaitingRunAsync(pathsA, now);
        using var runsA = new CustomLoopRunStore(pathsA, new HumanInputResponseContinuationFixedTimeProvider(now));
        var workerA = CreateWorker(pathsA, runsA, now);

        var submitted = await workerA.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);

        Assert.NotNull(submitted);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Completed, submitted.Status);
        Assert.Equal("human-input-continuation-submitted", submitted.ReasonCode);
        var completed = await runsA.GetAsync(context.Run.Id);
        Assert.NotNull(completed);
        AssertCompletedWithoutPrivateResponse(completed, await ReadAuditEvidenceAsync(pathsA));

        var wakeBefore = await new GovernedLoopSleepStore(pathsA).ReadAsync(new GovernedLoopOperationalEvidencePageRequest(4));
        Assert.Equal(GovernedLoopOperationalEvidenceReadStatus.Found, wakeBefore.Status);
        var wakeSnapshotBefore = Assert.Single(wakeBefore.Items);
        Assert.NotNull(wakeSnapshotBefore.Wake);
        var lifecycleVersion = completed.LifecycleVersion;
        var eventIds = completed.Events.Select(item => item.EventId).ToArray();
        var frontierHash = completed.Frontier!.Payload.ContentHash;
        var checkpointEvidenceHashes = completed.HumanInputWaitingCheckpoints.Single().Evidence.Select(item => item.EvidenceHash).ToArray();
        var wakeGeneration = wakeBefore.Generation;
        var wakeCheckpointHash = wakeSnapshotBefore.Checkpoint.ContentHash;
        var wakeEvidenceHash = wakeSnapshotBefore.Wake!.ContentHash;

        var pathsB = new WorkspacePaths(workspace.RootPath);
        using var runsB = new CustomLoopRunStore(pathsB, new HumanInputResponseContinuationFixedTimeProvider(now));
        var workerB = CreateWorker(pathsB, runsB, now);
        var afterCompletedCandidate = await workerB.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var afterCompletedRun = await workerB.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var cleanTail = await workerB.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);

        Assert.All([afterCompletedCandidate, afterCompletedRun, cleanTail], result => Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, result?.Status));
        Assert.Equal("human-input-candidates-empty", cleanTail?.ReasonCode);
        var restarted = await runsB.GetAsync(context.Run.Id);
        Assert.NotNull(restarted);
        var wakeAfter = await new GovernedLoopSleepStore(pathsB).ReadAsync(new GovernedLoopOperationalEvidencePageRequest(4));
        Assert.Equal(GovernedLoopOperationalEvidenceReadStatus.Found, wakeAfter.Status);
        var wakeSnapshotAfter = Assert.Single(wakeAfter.Items);

        Assert.Equal(lifecycleVersion, restarted.LifecycleVersion);
        Assert.Equal(eventIds, restarted.Events.Select(item => item.EventId));
        Assert.Equal(frontierHash, restarted.Frontier?.Payload.ContentHash);
        Assert.Equal(checkpointEvidenceHashes, restarted.HumanInputWaitingCheckpoints.Single().Evidence.Select(item => item.EvidenceHash));
        Assert.Equal(wakeGeneration, wakeAfter.Generation);
        Assert.Equal(wakeCheckpointHash, wakeSnapshotAfter.Checkpoint.ContentHash);
        Assert.Equal(wakeEvidenceHash, wakeSnapshotAfter.Wake?.ContentHash);
    }

    private static HumanInputResponseContinuationWorkRunner CreateWorker(
        WorkspacePaths paths,
        CustomLoopRunStore runs,
        DateTimeOffset now,
        IHumanInputRequestPublicationService? publication = null)
    {
        var clock = new HumanInputResponseContinuationFixedTimeProvider(now);
        var responses = new HumanInputRequestStore(paths);
        var sleepStore = new GovernedLoopSleepStore(paths);
        var posture = new HumanInputResponseContinuationHostCurrentPosturePort(runs, clock);
        var continuation = new HumanInputResponseContinuationService(
            runs,
            responses,
            sleepStore,
            posture,
            new HumanInputResponseContinuationHostContextPort(),
            new GovernedLoopSequentialOrderedRuntimeAdapter(
                new CustomLoopOrderedRunner(
                    runs,
                    new CustomLoopContextResolver(),
                    new HumanInputResponseContinuationHostInferenceExecutor(),
                    new HumanInputResponseContinuationHostConversationPublisher(),
                    new AuditLog(paths),
                    new HumanInputResponseContinuationHostAuthorityProvider(clock),
                    clock,
                    capabilityAdmissionService: new HumanInputResponseContinuationHostCapabilityAdmissionService(),
                    firstBoundRunCompletionBoundary: new GovernedLoopFirstBoundRunCompletionBoundary(
                        new GovernedLoopEffectAuthorityEvidenceStore(paths),
                        new HumanInputResponseContinuationHostCompletionTransaction(),
                        clock),
                    humanInputBindingSource: new HumanInputResponseContinuationBindingSource(responses)),
                runs,
                runs,
                new AuditLog(paths)),
            clock);
        var sleep = new GovernedLoopSleepService(sleepStore, posture, continuation, continuation, clock);
        continuation.BindSleep(sleep);
        return new HumanInputResponseContinuationWorkRunner(
            new HumanInputResponseContinuationNoOpWorkRunner(),
            new HumanInputResponseContinuationRecoveryStore(runs),
            new HumanInputPolicyFileStore(paths),
            publication ?? new HumanInputResponseContinuationRecordingPublicationService(),
            continuation,
            4,
            clock);
    }

    private static async Task<HumanInputContinuationRecoveryContext> SeedSubmittedWaitingRunAsync(
        WorkspacePaths paths,
        DateTimeOffset now)
    {
        var context = await SeedWaitingRunAsync(paths, now);
        await SeedSubmittedResponseAsync(paths, context, now);
        return context;
    }

    private static async Task<HumanInputContinuationRecoveryContext> SeedWaitingRunAsync(
        WorkspacePaths paths,
        DateTimeOffset now)
    {
        var fixture = HumanInputResponseContinuationRecoveryFixture.CreateWaitingContext();
        var running = fixture.RunningRun with
        {
            Events =
            [
                .. fixture.RunningRun.Events,
                new CustomLoopRunEvent(3, "human-input-continuation-running", fixture.RunningRun.UpdatedAtUtc, CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Run entered running.", [], null, null, null, null, null, null, null, null, null, null),
            ],
        };
        var waiting = fixture.Run with
        {
            Events = [.. running.Events, fixture.Run.Events[^1] with { Sequence = 4 }],
        };
        var context = fixture with { RunningRun = running, Run = waiting };
        using var runs = new CustomLoopRunStore(paths, new HumanInputResponseContinuationFixedTimeProvider(now));
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await runs.CreateAsync(context.AdmittedRun)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await runs.UpdateAsync(context.RunningRun, context.AdmittedRun.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await runs.UpdateAsync(context.Run, context.RunningRun.LifecycleVersion)).Status);
        return context;
    }

    private static async Task SeedSubmittedResponseAsync(
        WorkspacePaths paths,
        HumanInputContinuationRecoveryContext context,
        DateTimeOffset now)
    {
        var store = new HumanInputRequestStore(paths);
        var request = context.Checkpoint.Request;
        var head = HumanInputRequestStoreTestData.Head(request, 1, HumanInputRequestLifecycleStatus.Pending, 0, null, null, "human-input-continuation-create", request.Timing.RequestedAtUtc);
        var evidence = HumanInputRequestStoreTestData.Evidence(HumanInputRequestLifecycleOperationKind.Create, request.RequestId, "human-input-continuation-create", HumanInputRequestStoreTestData.HashA, request.Timing.RequestedAtUtc, null, head, request);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(new HumanInputRequestLifecycleStoreMutation(0, evidence, request, head, null))).Status);
        await SubmitResponseAsync(store, context, now);
    }

    private static async Task SubmitResponseAsync(
        HumanInputRequestStore store,
        HumanInputContinuationRecoveryContext context,
        DateTimeOffset now)
    {
        var request = context.Checkpoint.Request;
        var lifecycle = await store.ReadAsync(request.RequestId);
        var head = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(lifecycle.PrimarySnapshot).Head;
        Assert.True(AuthorityActorId.TryParse("user-one", out var actor, out _));
        var command = HumanInputResponseLifecycleCommandHash.Apply(new HumanInputResponseLifecycleCommand(
            HumanInputResponseLifecycleCommand.CurrentSchemaVersion,
            "human-input-continuation-submit",
            HumanInputResponseOperationKind.Submit,
            request.RequestId,
            head.LifecycleVersion,
            HumanInputRequestLifecycleStatus.Pending,
            HumanInputRequestStoreTestData.Reference(request),
            request.Binding,
            "human-input-continuation-response",
            new HumanInputResponseValue(HumanInputResponseKind.Confirmation, null, null, true, null, null),
            null,
            [],
            string.Empty));
        var result = await new HumanInputResponseLifecycleService(
            store,
            new HumanInputContinuationResponseActorAuthenticator(actor!),
            new HumanInputContinuationAuthorityTransaction(),
            request.Binding.WorkspaceId,
            new HumanInputResponseContinuationFixedTimeProvider(now)).MutateAsync(command);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, result.Status);
    }

    private static async Task<string> ReadAuditEvidenceAsync(WorkspacePaths paths)
    {
        if (!Directory.Exists(paths.AuditPath))
        {
            return string.Empty;
        }

        var evidence = new List<string>();
        foreach (var path in Directory.EnumerateFiles(paths.AuditPath, "*", SearchOption.AllDirectories))
        {
            evidence.Add(await File.ReadAllTextAsync(path));
        }

        return string.Join(Environment.NewLine, evidence);
    }

    private static void AssertCompletedWithoutPrivateResponse(CustomLoopRunRecord run, string auditEvidence)
    {
        Assert.Equal(CustomLoopRunStatus.Completed, run.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Completed, run.Frontier?.Payload.Status);
        Assert.Equal("Continue the exact waiting Human Input request.", run.FinalOutput);
        var checkpoint = Assert.Single(run.HumanInputWaitingCheckpoints);
        Assert.Equal(3, checkpoint.Evidence.Length);
        Assert.DoesNotContain("Accepted response.", System.Text.Json.JsonSerializer.Serialize(checkpoint));
        Assert.DoesNotContain("Accepted response.", System.Text.Json.JsonSerializer.Serialize(run.Events));
        Assert.DoesNotContain("Accepted response.", auditEvidence, StringComparison.Ordinal);
    }
}
