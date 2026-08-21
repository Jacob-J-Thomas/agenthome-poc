using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Wait;
using EmbodySense.Core.Common.Loops.Execution.Wait.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Authority;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep;
using EmbodySense.Core.Persistence.Loops.GraphAuthoring;
using EmbodySense.Core.Persistence.Loops.Revisions;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Persistence.Triggers;
using EmbodySense.Core.Persistence.Triggers.Schedules;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Triggers;
using EmbodySense.Core.Startup.Triggers.Models;
using EmbodySense.Core.Startup.Triggers.Schedules;
using EmbodySense.Core.Startup.Triggers.Schedules.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Startup.Tests.Triggers;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

// Scenario methods stay centralized so the scheduling wrappers preserve the exact existing behavior.
// Every scenario owns its fixture and provider process; this type must not gain mutable shared state.
internal static class GovernedLoopRuntimeTests
{
    private const string WaitRestartChildMode = "EMBODYSENSE_WAIT_RESTART_CHILD";
    private const string WaitRestartCodexPath = "EMBODYSENSE_WAIT_RESTART_CODEX_PATH";
    private const string WaitRestartRunId = "EMBODYSENSE_WAIT_RESTART_RUN_ID";
    private const string WaitRestartTrustRoot = "EMBODYSENSE_WAIT_RESTART_TRUST_ROOT";
    private const string WaitRestartWorkspace = "EMBODYSENSE_WAIT_RESTART_WORKSPACE";
    private const string WaitHostHolderChildMode = "EMBODYSENSE_WAIT_HOST_HOLDER_CHILD";
    private const string WaitHostHolderReadyPath = "EMBODYSENSE_WAIT_HOST_HOLDER_READY_PATH";
    private const string WaitHostHolderReleasePath = "EMBODYSENSE_WAIT_HOST_HOLDER_RELEASE_PATH";
    private const string WaitHostHolderWorkspace = "EMBODYSENSE_WAIT_HOST_HOLDER_WORKSPACE";
    private const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    private const string ModelInferenceCapabilityId = "org.embodysense/model-inference";
    private const string ScheduleTriggerCapabilityId = "org.embodysense/triggers/time";

    internal static async Task Production_runtime_parks_and_wakes_a_canonical_wait_after_restart()
    {
        if (string.Equals(Environment.GetEnvironmentVariable(WaitRestartChildMode), "1", StringComparison.Ordinal))
        {
            await RunWaitRestartChildAsync();
            return;
        }

        using var fixture = await GovernedRuntimeFixture.CreateAsync(
            waitDelay: TimeSpan.FromSeconds(45));
        var deadline = Assert.IsType<DateTimeOffset>(fixture.WaitDeadlineUtc);
        var input = fixture.Input("invoke-canonical-wait", "produce a result and then wait durably");
        string runId;

        await using (var runtime = await fixture.CreateRuntimeAsync())
        {
            Assert.True(
                DateTimeOffset.UtcNow < deadline,
                "The external restart fixture did not retain enough future time to prove pre-due parking.");
            var waiting = await runtime.InvokeGovernedLoopAsync(input);

            Assert.True(string.Equals("Executed", waiting.Status, StringComparison.Ordinal), waiting.Detail);
            Assert.True(
                string.Equals("Waiting", waiting.ExecutionStatus, StringComparison.Ordinal),
                $"{waiting.Detail} Run failure: {waiting.Run?.FailureCode}/{waiting.Run?.FailureDetail}");
            Assert.Equal(CustomLoopRunStatus.Waiting.ToString(), waiting.Run?.Status);
            Assert.NotEqual("canonical_wait_executor_unavailable", waiting.Run?.FailureCode);
            runId = Assert.IsType<string>(waiting.Run?.Id);
            using var store = new CustomLoopRunStore(fixture.Paths);
            var durable = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(runId));
            var wait = Assert.Single(durable.WaitEvidence);
            var park = Assert.IsType<GovernedLoopWaitParkEvidence>(wait.ParkEvidence);
            Assert.Null(wait.ContinuationEvidence);
            Assert.Equal(deadline, park.Checkpoint.WakeDeadlineUtc);
            Assert.True(park.ParkedAtUtc < deadline);
            Assert.Equal(GovernedLoopFrontierStatus.Waiting, durable.Frontier?.Payload.Status);
            var coordinator = await new GovernedLoopCoordinatorEvidenceStore(fixture.Paths)
                .ReadAsync("local-background");
            Assert.Equal(GovernedLoopCoordinatorReadStatus.NotFound, coordinator?.Status);
        }

        using var child = StartWaitRestartChild(fixture, runId);
        var standardOutput = child.StandardOutput.ReadToEndAsync();
        var standardError = child.StandardError.ReadToEndAsync();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
        {
            try
            {
                await child.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                child.Kill(entireProcessTree: true);
                throw new Xunit.Sdk.XunitException("The external governed Wait restart host did not finish within 120 seconds.");
            }
        }

        Assert.True(child.ExitCode == 0, await standardError + Environment.NewLine + await standardOutput);
        using (var store = new CustomLoopRunStore(fixture.Paths))
        {
            var completed = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(runId));
            Assert.Equal(CustomLoopRunStatus.Completed, completed.Status);
            Assert.Null(completed.FailureCode);
            var wait = Assert.Single(completed.WaitEvidence);
            var park = Assert.IsType<GovernedLoopWaitParkEvidence>(wait.ParkEvidence);
            var continuation = Assert.IsType<GovernedLoopWaitContinuationEvidence>(wait.ContinuationEvidence);
            var wake = await new GovernedLoopSleepStore(fixture.Paths)
                .ReadWakeAsync(continuation.PreparedWakeEvidence.Identity.WakeId);
            Assert.Equal(GovernedLoopSleepStoreReadStatus.Found, wake?.Status);
            var prepared = Assert.IsType<GovernedLoopWakeEvidence>(wake?.PreparedEvidence);
            var committed = Assert.IsType<GovernedLoopWakeEvidence>(wake?.Evidence);
            Assert.Equal(GovernedLoopWakeDisposition.Prepared, prepared.Disposition);
            Assert.Equal(GovernedLoopWakeDisposition.Committed, committed.Disposition);
            Assert.Equal(1, prepared.EvidenceVersion);
            // https://github.com/Jacob-J-Thomas/agenthome-poc/issues/472: restart reconciliation permits a committed successor at prepared +1 or +2.
            Assert.True(
                committed.EvidenceVersion == prepared.EvidenceVersion + 1
                || committed.EvidenceVersion == prepared.EvidenceVersion + 2,
                $"Committed wake evidence version {committed.EvidenceVersion} must be prepared version {prepared.EvidenceVersion} plus one or two.");
            Assert.Equal(continuation.PreparedWakeEvidence.ContentHash, prepared.ContentHash);
            Assert.Equal(prepared.ContinuationOperationId, committed.ContinuationOperationId);
            Assert.Equal(continuation.ContentHash, committed.ContinuationEvidenceHash);
            Assert.True(prepared.RecordedAtUtc >= deadline);
            Assert.True(committed.RecordedAtUtc >= prepared.RecordedAtUtc);
            Assert.True(GovernedLoopWaitContractValidator.ValidateComposition(park, continuation, committed).IsValid);
            Assert.Equal(GovernedLoopFrontierStatus.Completed, completed.Frontier?.Payload.Status);
        }

        Assert.Equal(1, fixture.ProviderAttempts);
    }

    internal static async Task Explicit_background_request_activates_once_after_late_workspace_host_reacquisition()
    {
        if (string.Equals(Environment.GetEnvironmentVariable(WaitHostHolderChildMode), "1", StringComparison.Ordinal))
        {
            await RunWaitHostHolderChildAsync();
            return;
        }

        using var fixture = await GovernedRuntimeFixture.CreateAsync(
            waitDelay: TimeSpan.FromMinutes(5));
        var readyPath = Path.Combine(fixture.Paths.RootPath, "wait-host-holder.ready");
        var releasePath = Path.Combine(fixture.Paths.RootPath, "wait-host-holder.release");
        using var child = StartWaitHostHolderChild(fixture, readyPath, releasePath);
        try
        {
            await WaitForFileAsync(readyPath, TimeSpan.FromSeconds(60));
            await using var runtime = await fixture.CreateRuntimeAsync();

            var unavailable = await runtime.StartGovernedWaitBackgroundAsync();
            var beforeReacquisition = await new GovernedLoopCoordinatorEvidenceStore(fixture.Paths)
                .ReadAsync("local-background");

            Assert.False(unavailable.Available);
            Assert.Equal("WorkspaceHostUnavailable", unavailable.Status);
            Assert.Equal(GovernedLoopCoordinatorReadStatus.NotFound, beforeReacquisition?.Status);

            await File.WriteAllTextAsync(releasePath, "release");
            await WaitForChildAsync(child, TimeSpan.FromSeconds(60), "The external custom-loop host holder did not exit.");

            var waiting = await runtime.InvokeGovernedLoopAsync(
                fixture.Input("invoke-late-wait-host", "activate the explicitly requested Wait host after reacquisition"));
            var activated = await new GovernedLoopCoordinatorEvidenceStore(fixture.Paths)
                .ReadAsync("local-background");
            var lifecycleVersion = activated?.Snapshot?.LatestLifecycle.LifecycleVersion;
            var repeated = await runtime.StartGovernedWaitBackgroundAsync();
            var afterRepeatedStart = await new GovernedLoopCoordinatorEvidenceStore(fixture.Paths)
                .ReadAsync("local-background");

            Assert.True(string.Equals("Executed", waiting.Status, StringComparison.Ordinal), waiting.Detail);
            Assert.Equal("Waiting", waiting.ExecutionStatus);
            Assert.Equal(GovernedLoopCoordinatorReadStatus.Found, activated?.Status);
            Assert.Equal(GovernedLoopCoordinatorStatus.Running, activated?.Snapshot?.LatestLifecycle.Status);
            Assert.Equal(1, activated?.Snapshot?.Ownership.OwnershipEpoch);
            Assert.True(repeated.Available, repeated.Detail);
            Assert.Equal(lifecycleVersion, afterRepeatedStart?.Snapshot?.LatestLifecycle.LifecycleVersion);
            Assert.Equal(1, fixture.ProviderAttempts);
        }
        finally
        {
            if (!child.HasExited)
            {
                await File.WriteAllTextAsync(releasePath, "release");
                await WaitForChildAsync(child, TimeSpan.FromSeconds(60), "The external custom-loop host holder did not stop during cleanup.");
            }
        }
    }

    private static async Task RunWaitRestartChildAsync()
    {
        var workspace = Environment.GetEnvironmentVariable(WaitRestartWorkspace);
        var trustRoot = Environment.GetEnvironmentVariable(WaitRestartTrustRoot);
        var codexPath = Environment.GetEnvironmentVariable(WaitRestartCodexPath);
        var runId = Environment.GetEnvironmentVariable(WaitRestartRunId);
        Assert.False(string.IsNullOrWhiteSpace(workspace));
        Assert.False(string.IsNullOrWhiteSpace(trustRoot));
        Assert.False(string.IsNullOrWhiteSpace(codexPath));
        Assert.False(string.IsNullOrWhiteSpace(runId));

        await using var runtime = await AgentRuntimeFactory.ForFileCapabilityTrustRoot(
                new RejectingApprovalPrompt(),
                trustRoot!)
            .CreateAsync(
                "test-model",
                workspace!,
                codexPath,
                "read-only",
                AgentRuntimeSurface.Cli,
                preserveCurrentConversation: true);
        var activation = await runtime.StartGovernedWaitBackgroundAsync();
        Assert.True(activation.Available, activation.Detail);
        var paths = new WorkspacePaths(workspace!);
        var coordinator = await new GovernedLoopCoordinatorEvidenceStore(paths)
            .ReadAsync("local-background");
        Assert.True(
            coordinator?.Snapshot?.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Running,
            $"Restart coordinator was not active: read={coordinator?.Status}, lifecycle={coordinator?.Snapshot?.LatestLifecycle.Status}, lease={coordinator?.Snapshot?.LatestHeartbeat.LeaseExpiresAtUtc:O}.");
        using var store = new CustomLoopRunStore(paths);
        CustomLoopRunRecord completed;
        try
        {
            completed = await WaitForRunAsync(store, runId!, CustomLoopRunStatus.Completed, TimeSpan.FromSeconds(60));
        }
        catch (Exception exception)
        {
            var failedCoordinator = await new GovernedLoopCoordinatorEvidenceStore(paths)
                .ReadAsync("local-background");
            var current = await store.GetAsync(runId!);
            var checkpoint = current?.WaitEvidence.SingleOrDefault()?.ParkEvidence?.Checkpoint;
            GovernedLoopWakeEvidenceReadResult? wake = null;
            if (checkpoint is not null)
            {
                var identity = GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeIdentity(
                    GovernedLoopWakeIdentity.CurrentSchemaVersion,
                    string.Empty,
                    checkpoint.CheckpointId,
                    checkpoint.ContentHash,
                    checkpoint.WakeMode,
                    null,
                    null,
                    string.Empty));
                wake = await new GovernedLoopSleepStore(paths).ReadWakeAsync(identity.WakeId);
            }

            var candidates = await new GovernedLoopBackgroundWorkSource(
                    new ScheduleStore(paths),
                    new GovernedLoopSleepStore(paths))
                .ReadAsync(
                    GovernedLoopBackgroundWorkFamily.Wake,
                    DateTimeOffset.UtcNow,
                    16);
            var frontier = current?.Frontier?.Payload;
            var waitNode = frontier?.Nodes.SingleOrDefault(node => node.NodeId == "wait");

            throw new Xunit.Sdk.XunitException(
                $"{exception.Message} Coordinator read={failedCoordinator?.Status}, lifecycle={failedCoordinator?.Snapshot?.LatestLifecycle.Status}, heartbeat={failedCoordinator?.Snapshot?.LatestHeartbeat.HeartbeatSequence}, failure={failedCoordinator?.Snapshot?.LatestFailureSequence}/{failedCoordinator?.Snapshot?.LatestFailureHash}; wake={wake?.Status}/{wake?.Evidence?.Disposition}/{wake?.Evidence?.DispositionEvidenceReference}; candidates={candidates?.WakeStatus}/{candidates?.WakeCandidates.Count}; run/frontier/node={current?.Status}/{frontier?.Status}/{waitNode?.Status}; now/deadline={DateTimeOffset.UtcNow:O}/{checkpoint?.WakeDeadlineUtc:O}.");
        }

        var continuation = Assert.IsType<GovernedLoopWaitContinuationEvidence>(Assert.Single(completed.WaitEvidence).ContinuationEvidence);
        await WaitForCommittedWakeAsync(
            new GovernedLoopSleepStore(paths),
            continuation.PreparedWakeEvidence.Identity.WakeId,
            TimeSpan.FromSeconds(30));
    }

    private static Process StartWaitRestartChild(GovernedRuntimeFixture fixture, string runId)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Verification.CoverageChildProcessAssembly.AddVstestArguments(
            startInfo,
            typeof(GovernedLoopRuntimeTests).Assembly.Location,
            $"{typeof(GovernedLoopRuntimeTestsWait).FullName}.{nameof(GovernedLoopRuntimeTestsWait.Production_runtime_parks_and_wakes_a_canonical_wait_after_restart)}");
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment[WaitRestartChildMode] = "1";
        startInfo.Environment[WaitRestartWorkspace] = fixture.Paths.RootPath;
        startInfo.Environment[WaitRestartTrustRoot] = fixture.TrustRootPath;
        startInfo.Environment[WaitRestartCodexPath] = fixture.CodexPath;
        startInfo.Environment[WaitRestartRunId] = runId;
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The external governed Wait restart host did not start.");
    }

    private static async Task RunWaitHostHolderChildAsync()
    {
        var workspace = Environment.GetEnvironmentVariable(WaitHostHolderWorkspace);
        var readyPath = Environment.GetEnvironmentVariable(WaitHostHolderReadyPath);
        var releasePath = Environment.GetEnvironmentVariable(WaitHostHolderReleasePath);
        Assert.False(string.IsNullOrWhiteSpace(workspace));
        Assert.False(string.IsNullOrWhiteSpace(readyPath));
        Assert.False(string.IsNullOrWhiteSpace(releasePath));

        await using var gate = new CustomLoopWorkspaceExecutionGate(new WorkspacePaths(workspace!));
        Assert.True(gate.IsWorkspaceHostAvailable);
        await File.WriteAllTextAsync(readyPath!, "ready");
        await WaitForFileAsync(releasePath!, TimeSpan.FromSeconds(60));
    }

    private static Process StartWaitHostHolderChild(
        GovernedRuntimeFixture fixture,
        string readyPath,
        string releasePath)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Verification.CoverageChildProcessAssembly.AddCoordinationOnlyVstestArguments(
            startInfo,
            typeof(GovernedLoopRuntimeTests).Assembly.Location,
            $"{typeof(GovernedLoopRuntimeTestsWait).FullName}.{nameof(GovernedLoopRuntimeTestsWait.Explicit_background_request_activates_once_after_late_workspace_host_reacquisition)}");
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment[WaitHostHolderChildMode] = "1";
        startInfo.Environment[WaitHostHolderWorkspace] = fixture.Paths.RootPath;
        startInfo.Environment[WaitHostHolderReadyPath] = readyPath;
        startInfo.Environment[WaitHostHolderReleasePath] = releasePath;
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The external custom-loop host holder did not start.");
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellation.Token);
        }
    }

    private static async Task WaitForChildAsync(Process child, TimeSpan timeout, string timeoutMessage)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await child.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            child.Kill(entireProcessTree: true);
            throw new Xunit.Sdk.XunitException(timeoutMessage);
        }

        if (child.ExitCode != 0)
        {
            throw new Xunit.Sdk.XunitException(
                await child.StandardError.ReadToEndAsync()
                + Environment.NewLine
                + await child.StandardOutput.ReadToEndAsync());
        }
    }

    internal static async Task Public_schedule_queues_and_executes_the_exact_canonical_graph_once_across_restart()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(scheduleTrigger: true);
        var scheduledAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2).ToUniversalTime();
        const string Prompt = "Run the exact scheduled governed reflection.";
        var scenario = ScheduleScenario.Create(fixture, scheduledAtUtc, Prompt);
        var workerNow = scheduledAtUtc.AddMinutes(2);
        using (var schedule = ScheduleRuntimeFactory.Create(
                   fixture.Paths,
                   scenario,
                   scenario,
                   scenario,
                   new FixedTriggerTimeProvider(workerNow)))
        {
            Assert.Equal(ScheduleRuntimeCreateStatus.Created, (await schedule.CreateAsync(scenario.Definition)).Status);
            var evaluated = await schedule.EvaluateOnceAsync(scenario.Definition.ScheduleId);
            Assert.True(evaluated.Status == ScheduleEvaluationStatus.Queued, $"Status={evaluated.Status}; Reason={evaluated.ReasonCode}");
        }

        var store = new TriggerQueueStore(fixture.Paths, TriggerQueueQuota.Runtime, timeProvider: new FixedTriggerTimeProvider(workerNow));
        var queued = Assert.Single((await store.GetSnapshotAsync(workerNow)).Entries);
        var generation = (await store.GetSnapshotAsync(workerNow)).Generation;
        var authorizer = new ExactTriggerAuthorizer();
        string runId;
        await using (var runtime = await fixture.CreateRuntimeAsync())
        {
            var worker = runtime.CreateTriggerWorkerRuntime(authorizer, new FixedTriggerTimeProvider(workerNow));
            var result = await worker.RunOnceAsync(new TriggerWorkerSelectionInput("worker-1", generation, workerNow, TimeSpan.FromSeconds(30), [], 2));
            var entry = Assert.IsType<TriggerWorkerEntrySnapshot>(result.Entry);
            Assert.True(entry.GovernedRunId is not null, $"State={entry.State}; Outcome={entry.DispatchOutcome}; Detail={entry.DispatchDetail}");
            runId = Assert.IsType<string>(entry.GovernedRunId);
            var run = Assert.IsType<LoopRunSnapshot>(await runtime.GetCustomLoopRunAsync(runId));

            Assert.Equal("Acquired", result.SelectionStatus);
            Assert.Equal("Dispatched", entry.State);
            Assert.Equal("Terminal", entry.DispatchOutcome);
            Assert.Equal(CustomLoopRunStatus.Completed.ToString(), run.Status);
            Assert.Equal("scheduled-owner", run.AdmissionActor);
            Assert.Equal("schedule", run.Surface);
            Assert.Equal("governed-helper", run.AdmittedDefinition.RoleId);
            Assert.Null(run.InvokingConversation);
            Assert.Equal("schedule-trigger", run.Frontier!.Nodes[0].TypeId);
            Assert.Equal(run.GovernedAdmissionRequestHash, entry.GovernedAdmissionRequestHash);
            Assert.Equal(1, fixture.ProviderAttempts);
            Assert.Equal(1, authorizer.Reads);
        }

        var durable = Assert.IsType<CustomLoopRunRecord>(await new CustomLoopRunStore(fixture.Paths).GetAsync(runId));
        var origin = Assert.IsType<GovernedLoopSequentialTriggerOrigin>(durable.SequentialInvocationSnapshot?.TriggerOrigin);
        Assert.True(TriggerDeliveryJson.TryDeserialize(origin.CanonicalEnvelope, out var envelope, out _));
        Assert.Equal(TriggerKind.Time, envelope!.Kind);
        Assert.Equal(queued.DeliveryId.Value, envelope.DeliveryId.Value);
        Assert.Equal(Prompt, Encoding.UTF8.GetString(envelope.Payload.GetInlinePayload()!));
        Assert.Equal(fixture.Publication, envelope.Loop.GovernedPublication);
        Assert.Equal(fixture.Grant, envelope.Loop.AuthorityGrant);
        Assert.Equal(scenario.Definition.ScheduleId.Value, origin.ScheduleId);
        Assert.Equal(scenario.Definition.Revision, origin.DefinitionRevision);
        Assert.True(ScheduleContractHash.TryComputeDefinition(scenario.Definition, out var definitionHash, out _));
        Assert.Equal(definitionHash, origin.DefinitionHash);
        Assert.Equal(scheduledAtUtc, origin.Occurrence.ScheduledAtUtc);

        using (var restartedSchedule = ScheduleRuntimeFactory.Create(
                   fixture.Paths,
                   scenario,
                   scenario,
                   scenario,
                   new FixedTriggerTimeProvider(workerNow.AddMinutes(1))))
        {
            Assert.Equal(ScheduleEvaluationStatus.Exhausted, (await restartedSchedule.EvaluateOnceAsync(scenario.Definition.ScheduleId)).Status);
        }

        await using (var restartedRuntime = await fixture.CreateRuntimeAsync())
        {
            var replay = Assert.IsType<LoopRunSnapshot>(await restartedRuntime.GetCustomLoopRunAsync(runId));
            var emptyGeneration = (await store.GetSnapshotAsync(workerNow.AddMinutes(1))).Generation;
            var emptyWorker = restartedRuntime.CreateTriggerWorkerRuntime(authorizer, new FixedTriggerTimeProvider(workerNow.AddMinutes(1)));
            var empty = await emptyWorker.RunOnceAsync(new TriggerWorkerSelectionInput("worker-2", emptyGeneration, workerNow.AddMinutes(1), TimeSpan.FromSeconds(30), [], 2));

            Assert.Equal(CustomLoopRunStatus.Completed.ToString(), replay.Status);
            Assert.Equal("Empty", empty.SelectionStatus);
            Assert.Null(empty.Entry);
            Assert.Equal(1, fixture.ProviderAttempts);
        }
    }

    internal static async Task Atomic_schedule_run_admission_closes_the_post_observation_race_for_every_overlap_policy(
        ScheduleOverlapPolicy overlap,
        ScheduleRunAdmissionDisposition secondDisposition,
        ScheduleRunAdmissionDisposition thirdDisposition)
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(scheduleTrigger: true, pauseProvider: true);
        var scheduledAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2).ToUniversalTime();
        var workerNow = scheduledAtUtc.AddMinutes(2);
        var token = overlap.ToString().ToLowerInvariant();
        var first = ScheduleScenario.Create(fixture, scheduledAtUtc, $"hold the {token} blocker", $"governed-overlap-{token}-a", overlap);
        var second = ScheduleScenario.Create(fixture, scheduledAtUtc, $"never dispatch the {token} contender", $"governed-overlap-{token}-b", overlap);
        var third = ScheduleScenario.Create(fixture, scheduledAtUtc, $"retain bounded {token} evidence", $"governed-overlap-{token}-c", overlap);

        await QueueScheduleAsync(fixture.Paths, first, workerNow);
        await QueueScheduleAsync(fixture.Paths, second, workerNow.AddTicks(1));
        await QueueScheduleAsync(fixture.Paths, third, workerNow.AddTicks(2));

        var dispatchNow = workerNow.AddSeconds(1);
        var queue = new TriggerQueueStore(fixture.Paths, TriggerQueueQuota.Runtime, timeProvider: new FixedTriggerTimeProvider(dispatchNow));
        Assert.Equal(3, (await queue.GetSnapshotAsync(dispatchNow)).QueuedEntries);
        await using var runtime = await fixture.CreateRuntimeAsync();
        var generation = (await queue.GetSnapshotAsync(dispatchNow)).Generation;
        var blockerTask = runtime
            .CreateTriggerWorkerRuntime(new ExactTriggerAuthorizer(), new FixedTriggerTimeProvider(dispatchNow))
            .RunOnceAsync(new TriggerWorkerSelectionInput("overlap-worker-a", generation, dispatchNow, TimeSpan.FromSeconds(30), [], 3));
        await fixture.WaitForProviderAsync();
        TriggerWorkerRunResponse blockedSecond;
        TriggerWorkerRunResponse blockedThird;
        try
        {
            generation = (await queue.GetSnapshotAsync(dispatchNow)).Generation;
            blockedSecond = await runtime
                .CreateTriggerWorkerRuntime(new ExactTriggerAuthorizer(), new FixedTriggerTimeProvider(dispatchNow))
                .RunOnceAsync(new TriggerWorkerSelectionInput("overlap-worker-b", generation, dispatchNow, TimeSpan.FromSeconds(30), [], 3));
            generation = (await queue.GetSnapshotAsync(dispatchNow)).Generation;
            blockedThird = await runtime
                .CreateTriggerWorkerRuntime(new ExactTriggerAuthorizer(), new FixedTriggerTimeProvider(dispatchNow))
                .RunOnceAsync(new TriggerWorkerSelectionInput("overlap-worker-c", generation, dispatchNow, TimeSpan.FromSeconds(30), [], 3));
            Assert.Equal(1, fixture.ProviderAttempts);
        }
        finally
        {
            fixture.ReleaseProvider();
        }

        var blocker = await blockerTask;
        Assert.Equal("Dispatched", blocker.Entry!.State);
        Assert.Equal("Terminal", blocker.Entry.DispatchOutcome);

        Assert.True(
            string.Equals("DispatchRejected", blockedSecond.Entry!.State, StringComparison.Ordinal),
            $"State={blockedSecond.Entry.State}; Outcome={blockedSecond.Entry.DispatchOutcome}; Detail={blockedSecond.Entry.DispatchDetail}");
        Assert.Equal("Rejected", blockedSecond.Entry.DispatchOutcome);
        Assert.Null(blockedSecond.Entry.GovernedRunId);
        Assert.Contains(secondDisposition.ToString(), blockedSecond.Entry.DispatchDetail, StringComparison.Ordinal);
        Assert.Equal("DispatchRejected", blockedThird.Entry!.State);
        Assert.Equal("Rejected", blockedThird.Entry.DispatchOutcome);
        Assert.Null(blockedThird.Entry.GovernedRunId);
        Assert.Contains(thirdDisposition.ToString(), blockedThird.Entry.DispatchDetail, StringComparison.Ordinal);
        Assert.Equal(1, fixture.ProviderAttempts);

        using var runs = new CustomLoopRunStore(fixture.Paths);
        var secondEvidence = Assert.IsType<ScheduleRunAdmissionEvidence>(
            await runs.GetScheduleAdmissionAsync(second.CreateEnvelope().DeliveryId));
        var thirdEvidence = Assert.IsType<ScheduleRunAdmissionEvidence>(
            await runs.GetScheduleAdmissionAsync(third.CreateEnvelope().DeliveryId));
        Assert.True(ScheduleRunAdmissionEvidenceValidator.IsValid(secondEvidence));
        Assert.True(ScheduleRunAdmissionEvidenceValidator.IsValid(thirdEvidence));
        Assert.Equal(secondDisposition, secondEvidence.Attempts[^1].Disposition);
        Assert.Equal(thirdDisposition, thirdEvidence.Attempts[^1].Disposition);
        Assert.Equal(blocker.Entry.GovernedRunId, secondEvidence.Attempts[^1].BlockingRunId);
        Assert.Equal(blocker.Entry.GovernedRunId, thirdEvidence.Attempts[^1].BlockingRunId);
        Assert.Single(await runs.ListRecentAsync(10));

        await using var background = runtime.CreateGovernedLoopLocalBackgroundRuntime(
            first,
            first,
            first,
            new ExactTriggerAuthorizer(),
            new UnusedSleepPosture(),
            new UnusedWakeContinuation(),
            new UnusedWakeVerification(),
            new GovernedLoopLocalWorkRunnerOptions(
                "overlap-retry-worker",
                TimeSpan.FromSeconds(30),
                2,
                3),
            new GovernedLoopLocalCoordinatorOptions(
                "overlap-retry-coordinator",
                "overlap-retry-owner",
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                1),
            new FixedTriggerTimeProvider(dispatchNow.AddMinutes(1)));
        var expectedRetries = overlap switch
        {
            ScheduleOverlapPolicy.Skip => 0,
            ScheduleOverlapPolicy.DeferOne => 1,
            ScheduleOverlapPolicy.Allow => 2,
            _ => throw new InvalidOperationException("The overlap theory supplied an unsupported policy."),
        };
        for (var index = 0; index < expectedRetries; index++)
        {
            var retried = Assert.IsType<GovernedLoopLocalWorkResult>(
                await background.RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger));
            Assert.Equal(GovernedLoopLocalWorkResultStatus.Completed, retried.Status);
            Assert.Equal("schedule-retry-materialized", retried.ReasonCode);
        }

        var exhausted = Assert.IsType<GovernedLoopLocalWorkResult>(
            await background.RunOnceAsync(GovernedLoopLocalWorkFamily.Trigger));
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, exhausted.Status);
        Assert.Equal(1 + expectedRetries, fixture.ProviderAttempts);
        Assert.Equal(1 + expectedRetries, (await runs.ListRecentAsync(10)).Count);

        var refreshedSecond = Assert.IsType<ScheduleRunAdmissionEvidence>(
            await runs.GetScheduleAdmissionAsync(second.CreateEnvelope().DeliveryId));
        var refreshedThird = Assert.IsType<ScheduleRunAdmissionEvidence>(
            await runs.GetScheduleAdmissionAsync(third.CreateEnvelope().DeliveryId));
        Assert.Equal(
            overlap == ScheduleOverlapPolicy.Skip
                ? ScheduleRunAdmissionDisposition.OverlapSkipped
                : ScheduleRunAdmissionDisposition.RunCreated,
            refreshedSecond.Attempts[^1].Disposition);
        Assert.Equal(
            overlap == ScheduleOverlapPolicy.Allow
                ? ScheduleRunAdmissionDisposition.RunCreated
                : thirdDisposition,
            refreshedThird.Attempts[^1].Disposition);
        Assert.All(
            refreshedSecond.Attempts,
            attempt => Assert.Equal(secondEvidence.Attempts[0].AdmissionOperationId, attempt.AdmissionOperationId));
        Assert.All(
            refreshedThird.Attempts,
            attempt => Assert.Equal(thirdEvidence.Attempts[0].AdmissionOperationId, attempt.AdmissionOperationId));

        if (overlap == ScheduleOverlapPolicy.Skip)
        {
            var evidencePath = Path.Combine(
                fixture.Paths.CustomLoopScheduleAdmissionsPath,
                second.CreateEnvelope().DeliveryId.Value + ".json");
            var canonical = await File.ReadAllTextAsync(evidencePath);
            var corrupted = canonical.Replace(secondEvidence.ContentHash, Hash64('0'), StringComparison.Ordinal);
            Assert.NotEqual(canonical, corrupted);
            await File.WriteAllTextAsync(evidencePath, corrupted);
            await Assert.ThrowsAsync<FormatException>(() =>
                new CustomLoopRunStore(fixture.Paths).GetScheduleAdmissionAsync(second.CreateEnvelope().DeliveryId));
        }
    }

    internal static async Task Concurrent_cross_schedule_defer_one_observations_retain_one_atomic_deferral_across_restart()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(scheduleTrigger: true, pauseProvider: true);
        var dispatchNow = DateTimeOffset.UtcNow.ToUniversalTime();
        var scheduledAtUtc = dispatchNow.AddMinutes(-2);
        var workerNow = scheduledAtUtc.AddMinutes(2);
        var blocker = ScheduleScenario.Create(fixture, scheduledAtUtc, "hold the observed-active blocker", "observed-defer-a");
        var second = ScheduleScenario.Create(fixture, scheduledAtUtc, "retain one observed-active occurrence", "observed-defer-b");
        var third = ScheduleScenario.Create(fixture, scheduledAtUtc, "suppress the additional observed-active occurrence", "observed-defer-c");

        await QueueScheduleAsync(fixture.Paths, blocker, workerNow);
        var workerClock = new MonotonicTriggerTimeProvider(dispatchNow);
        var queue = new TriggerQueueStore(fixture.Paths, TriggerQueueQuota.Runtime, timeProvider: workerClock);
        await using (var runtime = await fixture.CreateRuntimeAsync())
        {
            var generation = (await queue.GetSnapshotAsync(dispatchNow)).Generation;
            var blockerTask = runtime
                .CreateTriggerWorkerRuntime(new ExactTriggerAuthorizer(), workerClock)
                .RunOnceAsync(new TriggerWorkerSelectionInput("observed-overlap-worker-a", generation, dispatchNow, TriggerWorkerLimits.MaxLeaseDuration, [], 3));
            await fixture.WaitForProviderAsync();
            TriggerWorkerRunResponse blockedSecond;
            TriggerWorkerRunResponse blockedThird;
            try
            {
                using var activeRuns = new CustomLoopRunStore(fixture.Paths);
                var active = Assert.IsType<CustomLoopRunRecord>(
                    await activeRuns.GetNonterminalByLoopAsync(blocker.Definition.Target.LoopId));
                workerClock.AdvanceTo(active.UpdatedAtUtc.AddTicks(1));
                var contenderObservedNow = workerClock.GetUtcNow();
                var evaluations = await Task.WhenAll(
                    QueueScheduleThroughDurableOverlapAsync(fixture.Paths, second, workerClock),
                    QueueScheduleThroughDurableOverlapAsync(fixture.Paths, third, workerClock));
                Assert.All(evaluations, result =>
                {
                    Assert.Equal(ScheduleEvaluationStatus.Queued, result.Status);
                    Assert.Null(result.State!.DeferredOccurrence);
                    Assert.Empty(result.State.DispositionEvidence);
                });

                generation = (await queue.GetSnapshotAsync(contenderObservedNow)).Generation;
                blockedSecond = await runtime
                    .CreateTriggerWorkerRuntime(new ExactTriggerAuthorizer(), workerClock)
                    .RunOnceAsync(new TriggerWorkerSelectionInput("observed-overlap-worker-b", generation, contenderObservedNow, TriggerWorkerLimits.MaxLeaseDuration, [], 3));
                generation = (await queue.GetSnapshotAsync(contenderObservedNow)).Generation;
                blockedThird = await runtime
                    .CreateTriggerWorkerRuntime(new ExactTriggerAuthorizer(), workerClock)
                    .RunOnceAsync(new TriggerWorkerSelectionInput("observed-overlap-worker-c", generation, contenderObservedNow, TriggerWorkerLimits.MaxLeaseDuration, [], 3));
                Assert.Equal(1, fixture.ProviderAttempts);
            }
            finally
            {
                fixture.ReleaseProvider();
            }

            var blockerResult = await blockerTask;
            Assert.True(blockerResult.Entry is not null, $"Blocker selection={blockerResult.SelectionStatus}; mutation={blockerResult.MutationStatus}");
            Assert.True(
                string.Equals("Dispatched", blockerResult.Entry.State, StringComparison.Ordinal),
                $"State={blockerResult.Entry.State}; Outcome={blockerResult.Entry.DispatchOutcome}; Detail={blockerResult.Entry.DispatchDetail}; Mutation={blockerResult.MutationStatus}");
            Assert.True(blockedSecond.Entry is not null, $"Second selection={blockedSecond.SelectionStatus}; mutation={blockedSecond.MutationStatus}");
            Assert.True(
                string.Equals("DispatchRejected", blockedSecond.Entry.State, StringComparison.Ordinal),
                $"State={blockedSecond.Entry.State}; Outcome={blockedSecond.Entry.DispatchOutcome}; Detail={blockedSecond.Entry.DispatchDetail}; Mutation={blockedSecond.MutationStatus}");
            Assert.True(blockedThird.Entry is not null, $"Third selection={blockedThird.SelectionStatus}; mutation={blockedThird.MutationStatus}");
            Assert.True(
                string.Equals("DispatchRejected", blockedThird.Entry.State, StringComparison.Ordinal),
                $"State={blockedThird.Entry.State}; Outcome={blockedThird.Entry.DispatchOutcome}; Detail={blockedThird.Entry.DispatchDetail}; Mutation={blockedThird.MutationStatus}");
            Assert.Equal(1, fixture.ProviderAttempts);
        }

        using (var restartedRuns = new CustomLoopRunStore(fixture.Paths))
        {
            var dispositions = new[]
            {
                Assert.IsType<ScheduleRunAdmissionEvidence>(
                    await restartedRuns.GetScheduleAdmissionAsync(second.CreateEnvelope().DeliveryId)),
                Assert.IsType<ScheduleRunAdmissionEvidence>(
                    await restartedRuns.GetScheduleAdmissionAsync(third.CreateEnvelope().DeliveryId)),
            }.Select(evidence => evidence.Attempts[^1].Disposition).ToArray();
            Assert.Single(dispositions, disposition => disposition == ScheduleRunAdmissionDisposition.OverlapDeferred);
            Assert.Single(dispositions, disposition => disposition == ScheduleRunAdmissionDisposition.DeferredOneSuppressed);
            Assert.Single(await restartedRuns.ListRecentAsync(10));
        }

        await using (var restartedRuntime = await fixture.CreateRuntimeAsync())
        {
            workerClock.AdvanceTo(workerClock.GetUtcNow().AddMinutes(1));
            var restartNow = workerClock.GetUtcNow();
            var generation = (await queue.GetSnapshotAsync(restartNow)).Generation;
            var empty = await restartedRuntime
                .CreateTriggerWorkerRuntime(new ExactTriggerAuthorizer(), workerClock)
                .RunOnceAsync(new TriggerWorkerSelectionInput("observed-overlap-worker-restart", generation, restartNow, TimeSpan.FromSeconds(30), [], 3));
            Assert.Equal("Empty", empty.SelectionStatus);
            Assert.Equal(1, fixture.ProviderAttempts);
        }
    }

    internal static async Task Worker_defers_pending_schedule_finalization_then_restart_dispatches_and_replays_exactly_once()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(scheduleTrigger: true);
        var scheduledAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2).ToUniversalTime();
        var workerNow = scheduledAtUtc.AddMinutes(2);
        var scenario = ScheduleScenario.Create(fixture, scheduledAtUtc, "recover the exact pending scheduled delivery");
        var durableStore = new ScheduleStore(fixture.Paths);
        var conflictOnce = new FinalizationConflictOnceScheduleStore(durableStore);
        using (var schedule = ScheduleRuntimeFactory.Create(
                   fixture.Paths,
                   conflictOnce,
                   scenario,
                   scenario,
                   scenario,
                   new FixedTriggerTimeProvider(workerNow)))
        {
            Assert.Equal(ScheduleRuntimeCreateStatus.Created, (await schedule.CreateAsync(scenario.Definition)).Status);
            var interrupted = await schedule.EvaluateOnceAsync(scenario.Definition.ScheduleId);
            Assert.Equal(ScheduleEvaluationStatus.Conflict, interrupted.Status);
            Assert.Equal(SchedulePendingDeliveryPhase.ResultObserved, interrupted.State!.PendingDelivery!.Phase);
            Assert.Contains(
                interrupted.State.PendingDelivery.Result!.Kind,
                new[] { ScheduleDeliveryResultKind.Queued, ScheduleDeliveryResultKind.Replayed });
        }

        var queue = new TriggerQueueStore(fixture.Paths, TriggerQueueQuota.Runtime, timeProvider: new FixedTriggerTimeProvider(workerNow.AddSeconds(1)));
        var queued = Assert.Single((await queue.GetSnapshotAsync(workerNow.AddSeconds(1))).Entries);
        Assert.Equal(TriggerQueueEntryState.Queued, queued.State);
        var firstGeneration = (await queue.GetSnapshotAsync(workerNow.AddSeconds(1))).Generation;
        var firstAuthorizer = new ExactTriggerAuthorizer();
        await using (var firstRuntime = await fixture.CreateRuntimeAsync())
        {
            var firstWorker = firstRuntime.CreateTriggerWorkerRuntime(firstAuthorizer, new FixedTriggerTimeProvider(workerNow.AddSeconds(1)));
            var deferred = await firstWorker.RunOnceAsync(new TriggerWorkerSelectionInput(
                "worker-before-finalization",
                firstGeneration,
                workerNow.AddSeconds(1),
                TimeSpan.FromSeconds(30),
                [],
                2));

            Assert.Equal("Acquired", deferred.SelectionStatus);
            Assert.Equal("Committed", deferred.MutationStatus);
            Assert.Equal("Queued", deferred.Entry!.State);
            Assert.Null(deferred.Entry.DispatchOperationId);
            Assert.Null(deferred.Entry.GovernedRunId);
            Assert.Equal(0, firstAuthorizer.Reads);
            Assert.Equal(0, fixture.ProviderAttempts);
        }

        using (var recovery = ScheduleRuntimeFactory.Create(
                   fixture.Paths,
                   scenario,
                   scenario,
                   scenario,
                   new FixedTriggerTimeProvider(workerNow.AddSeconds(2))))
        {
            var finalized = await recovery.EvaluateOnceAsync(scenario.Definition.ScheduleId);
            Assert.Equal(ScheduleEvaluationStatus.Queued, finalized.Status);
            Assert.Null(finalized.State!.PendingDelivery);
            Assert.Equal(ScheduleDeliveryResultKind.Queued, Assert.Single(finalized.State.TerminalDeliveryEvidence).Result.Kind);
        }

        string runId;
        var secondAuthorizer = new ExactTriggerAuthorizer();
        await using (var restartedRuntime = await fixture.CreateRuntimeAsync())
        {
            var generation = (await queue.GetSnapshotAsync(workerNow.AddSeconds(3))).Generation;
            var worker = restartedRuntime.CreateTriggerWorkerRuntime(secondAuthorizer, new FixedTriggerTimeProvider(workerNow.AddSeconds(3)));
            var dispatched = await worker.RunOnceAsync(new TriggerWorkerSelectionInput(
                "worker-after-finalization",
                generation,
                workerNow.AddSeconds(3),
                TimeSpan.FromSeconds(30),
                [],
                2));

            Assert.Equal("Acquired", dispatched.SelectionStatus);
            Assert.Equal("Dispatched", dispatched.Entry!.State);
            Assert.Equal("Terminal", dispatched.Entry.DispatchOutcome);
            runId = Assert.IsType<string>(dispatched.Entry.GovernedRunId);
            Assert.Equal(1, secondAuthorizer.Reads);
            Assert.Equal(1, fixture.ProviderAttempts);
        }

        await using (var replayRuntime = await fixture.CreateRuntimeAsync())
        {
            var replay = Assert.IsType<LoopRunSnapshot>(await replayRuntime.GetCustomLoopRunAsync(runId));
            var generation = (await queue.GetSnapshotAsync(workerNow.AddSeconds(4))).Generation;
            var empty = await replayRuntime
                .CreateTriggerWorkerRuntime(secondAuthorizer, new FixedTriggerTimeProvider(workerNow.AddSeconds(4)))
                .RunOnceAsync(new TriggerWorkerSelectionInput(
                    "worker-exact-replay",
                    generation,
                    workerNow.AddSeconds(4),
                    TimeSpan.FromSeconds(30),
                    [],
                    2));

            Assert.Equal(CustomLoopRunStatus.Completed.ToString(), replay.Status);
            Assert.Equal("Empty", empty.SelectionStatus);
            Assert.Equal(1, fixture.ProviderAttempts);
        }
    }

    internal static async Task Queue_commit_before_result_persistence_cannot_be_lost_by_terminal_worker_replay_after_restart()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(scheduleTrigger: true);
        var scheduledAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2).ToUniversalTime();
        var workerNow = scheduledAtUtc.AddMinutes(2);
        var scenario = ScheduleScenario.Create(fixture, scheduledAtUtc, "preserve the prepared queue-commit crash window");
        var durableStore = new ScheduleStore(fixture.Paths);
        var conflictOnce = new ResultObservationConflictOnceScheduleStore(durableStore);
        using (var schedule = ScheduleRuntimeFactory.Create(
                   fixture.Paths,
                   conflictOnce,
                   scenario,
                   scenario,
                   scenario,
                   new FixedTriggerTimeProvider(workerNow)))
        {
            Assert.Equal(ScheduleRuntimeCreateStatus.Created, (await schedule.CreateAsync(scenario.Definition)).Status);
            var interrupted = await schedule.EvaluateOnceAsync(scenario.Definition.ScheduleId);
            Assert.Equal(ScheduleEvaluationStatus.Conflict, interrupted.Status);
            Assert.Equal(SchedulePendingDeliveryPhase.Prepared, interrupted.State!.PendingDelivery!.Phase);
        }

        var queue = new TriggerQueueStore(fixture.Paths, TriggerQueueQuota.Runtime, timeProvider: new FixedTriggerTimeProvider(workerNow.AddSeconds(1)));
        var queued = Assert.Single((await queue.GetSnapshotAsync(workerNow.AddSeconds(1))).Entries);
        Assert.Equal(TriggerQueueEntryState.Queued, queued.State);
        await using (var runtime = await fixture.CreateRuntimeAsync())
        {
            var generation = (await queue.GetSnapshotAsync(workerNow.AddSeconds(1))).Generation;
            var terminal = await runtime
                .CreateTriggerWorkerRuntime(new ExactTriggerAuthorizer(), new FixedTriggerTimeProvider(workerNow.AddSeconds(1)))
                .RunOnceAsync(new TriggerWorkerSelectionInput(
                    "prepared-crash-worker",
                    generation,
                    workerNow.AddSeconds(1),
                    TimeSpan.FromSeconds(30),
                    [],
                    2));

            Assert.Equal("NeedsReview", terminal.Entry!.State);
            Assert.Equal("NeedsReview", terminal.Entry.DispatchOutcome);
            Assert.Null(terminal.Entry.GovernedRunId);
            Assert.Equal(0, fixture.ProviderAttempts);
        }

        using (var recovery = ScheduleRuntimeFactory.Create(
                   fixture.Paths,
                   scenario,
                   scenario,
                   scenario,
                   new FixedTriggerTimeProvider(workerNow.AddSeconds(2))))
        {
            var reconciled = await recovery.EvaluateOnceAsync(scenario.Definition.ScheduleId);
            Assert.True(
                reconciled.Status == ScheduleEvaluationStatus.NeedsReview,
                $"Status={reconciled.Status}; Reason={reconciled.ReasonCode}");
            Assert.Equal("queue-evidence-conflict", reconciled.ReasonCode);
            Assert.Equal(SchedulePendingDeliveryPhase.ResultObserved, reconciled.State!.PendingDelivery!.Phase);
            Assert.Equal(ScheduleDeliveryResultKind.Ambiguous, reconciled.State.PendingDelivery.Result!.Kind);
            Assert.Empty(reconciled.State.TerminalDeliveryEvidence);

            var replay = await recovery.EvaluateOnceAsync(scenario.Definition.ScheduleId);
            Assert.Equal(ScheduleEvaluationStatus.NeedsReview, replay.Status);
            Assert.Equal("queue-outcome-ambiguous", replay.ReasonCode);
            Assert.Empty(replay.State!.TerminalDeliveryEvidence);
        }

        var restartedEntry = Assert.Single((await new TriggerQueueStore(fixture.Paths, TriggerQueueQuota.Runtime).GetSnapshotAsync(workerNow.AddSeconds(3))).Entries);
        Assert.Equal(TriggerQueueEntryState.NeedsReview, restartedEntry.State);
        Assert.Null(await new CustomLoopRunStore(fixture.Paths).GetScheduleAdmissionAsync(scenario.CreateEnvelope().DeliveryId));
        Assert.Empty(await new CustomLoopRunStore(fixture.Paths).ListRecentAsync(10));
        Assert.Equal(0, fixture.ProviderAttempts);
    }

    internal static async Task Substituted_trigger_context_fails_before_canonical_provider_dispatch(string mismatch)
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(scheduleTrigger: true);
        await using var runtime = await fixture.CreateRuntimeAsync();
        Assert.True(TriggerDeliveryFactory.TryCreateGovernedLoopReference(fixture.Publication, fixture.Grant, out var target, out _));
        Assert.True(AuthorityActorId.TryParse("scheduled-owner", out var actor, out _));
        var workspace = mismatch == "workspace" ? new string('f', 64) : TriggerWorkspaceId(fixture.Paths);
        var role = mismatch == "role" ? "other-role" : "governed-helper";
        Assert.True(TriggerDeliveryFactory.TryCreateActorContext(actor, "schedule", workspace, role, out var actorContext, out _));
        var envelope = TriggerWorkerTestData.ScheduleEnvelope(target!, actorContext!);
        var store = new TriggerQueueStore(fixture.Paths, TriggerQueueQuota.Runtime);
        Assert.True(TriggerDeliveryAdmissionRequestFactory.TryCreate(envelope, envelope.Loop, envelope.Adapter, true, envelope.ActorContext, envelope.Authority, TriggerWorkerTestData.CreatedAtUtc.AddSeconds(3), out var delivery, out _));
        await new TriggerQueueAdmissionService(new TriggerDeliveryAdmissionService(store), store).AdmitAsync(TriggerQueueAdmissionRequestFactory.Create(delivery!, TriggerQueueAdmissionMode.Queued, TriggerQueuePriority.Normal));
        var generation = (await store.GetSnapshotAsync(TriggerWorkerTestData.CreatedAtUtc.AddSeconds(4))).Generation;
        var worker = runtime.CreateTriggerWorkerRuntime(new ExactTriggerAuthorizer(), new FixedTriggerTimeProvider(TriggerWorkerTestData.CreatedAtUtc.AddSeconds(4)));

        var result = await worker.RunOnceAsync(new TriggerWorkerSelectionInput("worker-1", generation, TriggerWorkerTestData.CreatedAtUtc.AddSeconds(4), TimeSpan.FromSeconds(30), [], 2));

        Assert.Equal("NeedsReview", result.Entry!.State);
        Assert.Equal("NeedsReview", result.Entry.DispatchOutcome);
        Assert.Null(result.Entry.GovernedRunId);
        Assert.NotNull(result.Entry.DispatchOperationId);
        Assert.Null(await new CustomLoopInvocationOperationStore(fixture.Paths).GetAsync(result.Entry.DispatchOperationId!));
        Assert.Equal(0, fixture.ProviderAttempts);
    }

    internal static async Task Forged_or_swapped_schedule_provenance_fails_before_admission_across_replay_and_restart(string mismatch)
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(includeRestrictedGrant: true, scheduleTrigger: true);
        var scheduledAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2).ToUniversalTime();
        var workerNow = scheduledAtUtc.AddMinutes(2);
        var scenario = ScheduleScenario.Create(fixture, scheduledAtUtc, "authenticated schedule payload");
        var canonical = scenario.CreateEnvelope();
        await SeedAcceptedScheduleEvidenceAsync(fixture.Paths, scenario.Definition, canonical, scheduledAtUtc);
        var substituted = SubstitutedScheduleEnvelope(fixture, scenario, canonical, mismatch);
        var queue = new TriggerQueueStore(fixture.Paths, TriggerQueueQuota.Runtime, timeProvider: new FixedTriggerTimeProvider(workerNow));
        Assert.True(TriggerDeliveryAdmissionRequestFactory.TryCreate(
            substituted,
            substituted.Loop,
            substituted.Adapter,
            true,
            substituted.ActorContext,
            substituted.Authority,
            workerNow.AddSeconds(1),
            out var delivery,
            out _));
        var admission = new TriggerQueueAdmissionService(new TriggerDeliveryAdmissionService(queue), queue);
        var request = TriggerQueueAdmissionRequestFactory.Create(delivery!, TriggerQueueAdmissionMode.Queued, TriggerQueuePriority.Normal);
        Assert.Equal(TriggerQueueAdmissionStatus.Queued, (await admission.AdmitAsync(request)).Status);
        Assert.Equal(TriggerQueueAdmissionStatus.Replayed, (await admission.AdmitAsync(request)).Status);
        var generation = (await queue.GetSnapshotAsync(workerNow.AddSeconds(2))).Generation;

        await using var restartedRuntime = await fixture.CreateRuntimeAsync();
        var worker = restartedRuntime.CreateTriggerWorkerRuntime(new ExactTriggerAuthorizer(), new FixedTriggerTimeProvider(workerNow.AddSeconds(2)));
        var result = await worker.RunOnceAsync(new TriggerWorkerSelectionInput(
            $"worker-{mismatch}",
            generation,
            workerNow.AddSeconds(2),
            TimeSpan.FromSeconds(30),
            [],
            2));

        var entry = Assert.IsType<TriggerWorkerEntrySnapshot>(result.Entry);
        Assert.Equal("NeedsReview", entry.State);
        Assert.Equal("NeedsReview", entry.DispatchOutcome);
        Assert.Null(entry.GovernedRunId);
        Assert.Null(entry.GovernedAdmissionRequestHash);
        Assert.NotNull(entry.DispatchOperationId);
        Assert.Null(await new CustomLoopInvocationOperationStore(fixture.Paths).GetAsync(entry.DispatchOperationId!));
        Assert.Equal(0, fixture.ProviderAttempts);
    }

    internal static async Task Manual_governed_invocation_rejects_the_reserved_trigger_namespace_before_admission()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync();
        await using var runtime = await fixture.CreateRuntimeAsync();
        Assert.True(TriggerDeliveryId.TryParse("delivery-manual-governed-trigger-namespace", out var deliveryId));
        var operationId = TriggerWorkerRequestHash.ComputeOperationId(deliveryId!, 1);

        var response = await runtime.InvokeGovernedLoopAsync(fixture.Input(operationId, "must not admit manually"));

        Assert.Equal("Invalid", response.Status);
        Assert.Null(response.AdmissionOutcome);
        Assert.False(response.WasDispatched);
        Assert.Null(response.Run);
        Assert.Contains("reserved", response.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.ProviderAttempts);
    }

    internal static async Task Manual_invocation_of_a_schedule_trigger_graph_fails_before_admission()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(scheduleTrigger: true);
        await using var runtime = await fixture.CreateRuntimeAsync();

        var response = await runtime.InvokeGovernedLoopAsync(fixture.Input("invoke-schedule-manually", "must not admit"));

        Assert.Equal("Invalid", response.Status);
        Assert.Null(response.AdmissionOutcome);
        Assert.False(response.WasDispatched);
        Assert.Null(response.Run);
        Assert.Contains("schedule-derived", response.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.ProviderAttempts);
    }

    internal static async Task Schedule_delivery_to_a_manual_trigger_graph_fails_before_admission()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync();
        var scheduledAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2).ToUniversalTime();
        var workerNow = scheduledAtUtc.AddMinutes(2);
        var scenario = ScheduleScenario.Create(fixture, scheduledAtUtc, "must not admit to a manual graph");
        using (var schedule = ScheduleRuntimeFactory.Create(
                   fixture.Paths,
                   scenario,
                   scenario,
                   scenario,
                   new FixedTriggerTimeProvider(workerNow)))
        {
            Assert.Equal(ScheduleRuntimeCreateStatus.Created, (await schedule.CreateAsync(scenario.Definition)).Status);
            Assert.Equal(ScheduleEvaluationStatus.Queued, (await schedule.EvaluateOnceAsync(scenario.Definition.ScheduleId)).Status);
        }

        await using var runtime = await fixture.CreateRuntimeAsync();
        var store = new TriggerQueueStore(fixture.Paths, TriggerQueueQuota.Runtime, timeProvider: new FixedTriggerTimeProvider(workerNow));
        var generation = (await store.GetSnapshotAsync(workerNow)).Generation;
        var worker = runtime.CreateTriggerWorkerRuntime(new ExactTriggerAuthorizer(), new FixedTriggerTimeProvider(workerNow));

        var result = await worker.RunOnceAsync(new TriggerWorkerSelectionInput(
            "worker-manual-confusion",
            generation,
            workerNow,
            TimeSpan.FromSeconds(30),
            [],
            2));

        Assert.Equal("NeedsReview", result.Entry!.State);
        Assert.Equal("NeedsReview", result.Entry.DispatchOutcome);
        Assert.Contains("manual-trigger", result.Entry.DispatchDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Entry.GovernedRunId);
        Assert.NotNull(result.Entry.DispatchOperationId);
        Assert.Null(await new CustomLoopInvocationOperationStore(fixture.Paths).GetAsync(result.Entry.DispatchOperationId!));
        Assert.Equal(0, fixture.ProviderAttempts);
    }

    internal static async Task Public_runtime_executes_exact_canonical_inputs_and_terminal_replay_precedes_workspace_busy_and_restart()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync();
        var input = fixture.Input("invoke-canonical-success", "answer through the governed graph");

        await using (var runtime = await fixture.CreateRuntimeAsync())
        {
            var executed = await runtime.InvokeGovernedLoopAsync(input);

            Assert.True(string.Equals("Executed", executed.Status, StringComparison.Ordinal), executed.Detail);
            Assert.Equal("Admitted", executed.AdmissionStatus);
            Assert.Equal("Ready", executed.MaterializationStatus);
            Assert.True(
                string.Equals("Completed", executed.ExecutionStatus, StringComparison.Ordinal),
                $"{executed.Detail} Run failure: {executed.Run?.FailureCode}/{executed.Run?.FailureDetail}");
            Assert.True(executed.WasDispatched);
            Assert.Equal(CustomLoopRunStatus.Completed.ToString(), executed.Run?.Status);
            Assert.Equal(1, fixture.ProviderAttempts);

            using var runStore = new CustomLoopRunStore(fixture.Paths);
            var durableRun = Assert.IsType<CustomLoopRunRecord>(
                await runStore.GetAsync(executed.Run!.Id));
            Assert.NotNull(durableRun.SequentialAdapterBinding);
            Assert.NotNull(durableRun.SequentialInvocationSnapshot);
            Assert.Equal(input.OperationId, durableRun.SequentialAdapterBinding!.AdmissionOperationId);
            AssertFrontierProjection(
                Assert.IsType<LoopRunFrontierSnapshot>(executed.Run.Frontier),
                Assert.IsType<GovernedLoopFrontierPosture>(durableRun.Frontier));

            await using var competingGate = new CustomLoopWorkspaceExecutionGate(fixture.Paths);
            using var competingLease = Assert.IsAssignableFrom<IDisposable>(
                competingGate.TryAcquire("competing-terminal-replay", Hash64('8')).Lease);
            var terminalReplay = await runtime.InvokeGovernedLoopAsync(input);

            Assert.Equal("Terminal", terminalReplay.Status);
            Assert.Equal("Replayed", terminalReplay.AdmissionStatus);
            Assert.False(terminalReplay.WasDispatched);
            Assert.Equal(executed.Run.Id, terminalReplay.Run?.Id);
            Assert.Equal(1, fixture.ProviderAttempts);
        }

        await using (var restarted = await fixture.CreateRuntimeAsync(preserveCurrentConversation: true))
        {
            var replay = await restarted.InvokeGovernedLoopAsync(input);

            Assert.Equal("Terminal", replay.Status);
            Assert.Equal("Replayed", replay.AdmissionStatus);
            Assert.False(replay.WasDispatched);
            Assert.Equal(1, fixture.ProviderAttempts);

            var defaultTurn = await restarted.RunTurnAsync("legacy default path remains selected");
            Assert.True(defaultTurn.IsMessageTurn);
            Assert.Contains("legacy default path remains selected", defaultTurn.Output, StringComparison.Ordinal);
        }
    }

    internal static async Task First_bound_completion_replays_after_restart_and_rejects_a_second_run_without_provider_dispatch()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(
            completionConstraint: AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion);
        var firstInput = fixture.Input("invoke-first-bound-success", "complete the first bound run");

        await using (var runtime = await fixture.CreateRuntimeAsync())
        {
            var completed = await runtime.InvokeGovernedLoopAsync(firstInput);
            var replayed = await runtime.InvokeGovernedLoopAsync(firstInput);

            Assert.True(string.Equals("Executed", completed.Status, StringComparison.Ordinal), completed.Detail);
            Assert.Equal("Completed", completed.ExecutionStatus);
            Assert.Equal(CustomLoopRunStatus.Completed.ToString(), completed.Run?.Status);
            Assert.True(completed.WasDispatched);
            Assert.Equal("Terminal", replayed.Status);
            Assert.Equal("Replayed", replayed.AdmissionStatus);
            Assert.Equal("Completed", replayed.ExecutionStatus);
            Assert.False(replayed.WasDispatched);
            Assert.Equal(completed.Run?.Id, replayed.Run?.Id);
            Assert.Equal(1, fixture.ProviderAttempts);
        }

        await using var restarted = await fixture.CreateRuntimeAsync(preserveCurrentConversation: true);
        var restartReplay = await restarted.InvokeGovernedLoopAsync(firstInput);
        var rejected = await restarted.InvokeGovernedLoopAsync(
            fixture.Input("invoke-first-bound-second", "a second run must not dispatch"));

        Assert.Equal("Terminal", restartReplay.Status);
        Assert.Equal("Replayed", restartReplay.AdmissionStatus);
        Assert.Equal("Completed", restartReplay.ExecutionStatus);
        Assert.False(restartReplay.WasDispatched);
        Assert.True(string.Equals("Executed", rejected.Status, StringComparison.Ordinal), rejected.Detail);
        Assert.Equal("Failed", rejected.ExecutionStatus);
        Assert.Equal(CustomLoopRunStatus.Failed.ToString(), rejected.Run?.Status);
        Assert.Equal("effect_authority_denied", rejected.Run?.FailureCode);
        Assert.False(rejected.WasDispatched);
        Assert.Equal(1, fixture.ProviderAttempts);
    }

    internal static async Task Failed_provider_attempt_does_not_consume_first_bound_completion()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(
            completionConstraint: AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion,
            failFirstAttempts: 1);
        await using var runtime = await fixture.CreateRuntimeAsync();

        var failed = await runtime.InvokeGovernedLoopAsync(
            fixture.Input("invoke-first-bound-provider-failure", "fail before completion"));
        var completed = await runtime.InvokeGovernedLoopAsync(
            fixture.Input("invoke-first-bound-after-provider-failure", "complete after the failed attempt"));

        Assert.Equal("Failed", failed.ExecutionStatus);
        Assert.Equal(CustomLoopRunStatus.Failed.ToString(), failed.Run?.Status);
        Assert.True(failed.WasDispatched);
        Assert.True(string.Equals("Executed", completed.Status, StringComparison.Ordinal), completed.Detail);
        Assert.Equal("Completed", completed.ExecutionStatus);
        Assert.Equal(CustomLoopRunStatus.Completed.ToString(), completed.Run?.Status);
        Assert.True(completed.WasDispatched);
        Assert.Equal(2, fixture.ProviderAttempts);
    }

    internal static async Task Paused_then_cancelled_run_does_not_consume_first_bound_completion()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(
            pauseProvider: true,
            completionConstraint: AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion);
        await using var runtime = await fixture.CreateRuntimeAsync();
        var invocation = runtime.InvokeGovernedLoopAsync(
            fixture.Input("invoke-first-bound-pause-cancel", "pause and cancel before completion"));
        await fixture.WaitForProviderAsync();
        using var runStore = new CustomLoopRunStore(fixture.Paths);
        var running = await WaitForRunAsync(runStore, CustomLoopRunStatus.Running);

        var pause = await runtime.PauseCustomLoopAsync(
            new LoopRunControlInput(running.Id, running.LifecycleVersion, "pause-first-bound-before-cancel"));
        Assert.Equal("PauseRequested", pause.Status);
        fixture.ReleaseProvider();

        var paused = await invocation;
        Assert.Equal("Paused", paused.ExecutionStatus);
        Assert.Equal(CustomLoopRunStatus.Paused.ToString(), paused.Run?.Status);
        var cancelled = await runtime.CancelCustomLoopAsync(
            new LoopRunControlInput(paused.Run!.Id, paused.Run.LifecycleVersion, "cancel-first-bound-paused-run"));
        var completed = await runtime.InvokeGovernedLoopAsync(
            fixture.Input("invoke-first-bound-after-cancel", "complete after cancellation"));

        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled.ToString(), cancelled.Run?.Status);
        Assert.True(string.Equals("Executed", completed.Status, StringComparison.Ordinal), completed.Detail);
        Assert.Equal("Completed", completed.ExecutionStatus);
        Assert.Equal(CustomLoopRunStatus.Completed.ToString(), completed.Run?.Status);
        Assert.Equal(2, fixture.ProviderAttempts);
    }

    internal static async Task Publication_conflict_does_not_consume_first_bound_completion()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(
            pauseProvider: true,
            completionConstraint: AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion);

        await using (var runtime = await fixture.CreateRuntimeAsync())
        {
            var invocation = runtime.InvokeGovernedLoopAsync(
                fixture.Input("invoke-first-bound-publication-conflict", "conflict before publication"));
            await fixture.WaitForProviderAsync();
            await new ConversationMemoryStore(fixture.Paths).AppendMessageAsync(
                LlmMessage.User("interleaving durable conversation message"));
            fixture.ReleaseProvider();

            var review = await invocation;
            Assert.Equal("NeedsReview", review.ExecutionStatus);
            Assert.Equal(CustomLoopRunStatus.NeedsReview.ToString(), review.Run?.Status);
            Assert.Equal("conversation_publication_failed", review.Run?.FailureCode);
            Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked.ToString(), review.Run?.Frontier?.Status);
            Assert.NotNull(review.Run?.Frontier?.Nodes[^1].OutcomeEvidenceId);
            Assert.Equal(1, fixture.ProviderAttempts);
        }

        await using var restarted = await fixture.CreateRuntimeAsync(preserveCurrentConversation: true);
        var completed = await restarted.InvokeGovernedLoopAsync(
            fixture.Input("invoke-first-bound-after-publication-conflict", "complete after publication conflict"));

        Assert.True(string.Equals("Executed", completed.Status, StringComparison.Ordinal), completed.Detail);
        Assert.Equal("Completed", completed.ExecutionStatus);
        Assert.Equal(CustomLoopRunStatus.Completed.ToString(), completed.Run?.Status);
        Assert.Equal(2, fixture.ProviderAttempts);
    }

    internal static async Task Definitive_authority_rejection_replays_after_restart_without_materialization_or_provider_work()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(includeRestrictedGrant: true);
        var input = fixture.Input("invoke-canonical-rejected", "must be rejected", fixture.RestrictedGrant!);

        await using (var runtime = await fixture.CreateRuntimeAsync())
        {
            var rejected = await runtime.InvokeGovernedLoopAsync(input);
            var replayed = await runtime.InvokeGovernedLoopAsync(input);

            Assert.True(string.Equals("Rejected", rejected.Status, StringComparison.Ordinal), rejected.Detail);
            Assert.Equal("Rejected", rejected.AdmissionStatus);
            Assert.NotNull(rejected.AdmissionFailureCode);
            Assert.Null(rejected.MaterializationStatus);
            Assert.Null(rejected.Run);
            Assert.False(rejected.WasDispatched);
            Assert.Equal("Rejected", replayed.Status);
            Assert.Equal("Replayed", replayed.AdmissionStatus);
            Assert.False(replayed.WasDispatched);
            Assert.Equal(0, fixture.ProviderAttempts);
        }

        await using var restarted = await fixture.CreateRuntimeAsync(preserveCurrentConversation: true);
        var restartReplay = await restarted.InvokeGovernedLoopAsync(input);

        Assert.Equal("Rejected", restartReplay.Status);
        Assert.Equal("Replayed", restartReplay.AdmissionStatus);
        Assert.False(restartReplay.WasDispatched);
        Assert.Equal(0, fixture.ProviderAttempts);
    }

    internal static async Task Begin_before_snapshot_bind_recovers_only_when_the_exact_snapshot_can_be_reproduced()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync();
        var input = fixture.Input("invoke-crash-window-exact", "recover the exact prepared snapshot");
        await using var runtime = await fixture.CreateRuntimeAsync();
        await using var competingGate = new CustomLoopWorkspaceExecutionGate(fixture.Paths);
        var competing = competingGate.TryAcquire("competing-before-bind", Hash64('7'));
        Assert.Equal(CustomLoopExecutionLeaseStatus.Acquired, competing.Status);

        using (competing.Lease)
        {
            var interrupted = await runtime.InvokeGovernedLoopAsync(input);

            Assert.Equal("WorkspaceBusy", interrupted.Status);
            Assert.False(interrupted.WasDispatched);
            Assert.Equal(0, fixture.ProviderAttempts);
            var unbound = Assert.IsType<EmbodySense.Core.Application.Loops.Models.CustomLoopInvocationOperation>(
                await new CustomLoopInvocationOperationStore(fixture.Paths).GetAsync(input.OperationId));
            Assert.Equal(CustomLoopInvocationBindingState.Unbound, unbound.BindingState);
            Assert.Null(unbound.SequentialInvocationSnapshot);
        }

        var recovered = await runtime.InvokeGovernedLoopAsync(input);
        var durable = Assert.IsType<EmbodySense.Core.Application.Loops.Models.CustomLoopInvocationOperation>(
            await new CustomLoopInvocationOperationStore(fixture.Paths).GetAsync(input.OperationId));

        Assert.True(string.Equals("Executed", recovered.Status, StringComparison.Ordinal), recovered.Detail);
        Assert.True(recovered.WasDispatched);
        Assert.Equal(1, fixture.ProviderAttempts);
        Assert.Equal(CustomLoopInvocationBindingState.CapturedContext, durable.BindingState);
        Assert.NotNull(durable.SequentialInvocationSnapshot);
        Assert.Equal(durable.CreatedAtUtc, durable.SequentialInvocationSnapshot!.ContextCapturedAtUtc);
    }

    internal static async Task Begin_before_snapshot_bind_conflicts_when_context_changes_and_does_zero_provider_work()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync();
        var input = fixture.Input("invoke-crash-window-changed", "do not substitute changed context");
        await using var runtime = await fixture.CreateRuntimeAsync();
        await using var competingGate = new CustomLoopWorkspaceExecutionGate(fixture.Paths);
        var competing = competingGate.TryAcquire("competing-before-context-change", Hash64('6'));

        using (Assert.IsAssignableFrom<IDisposable>(competing.Lease))
        {
            var interrupted = await runtime.InvokeGovernedLoopAsync(input);
            Assert.Equal("WorkspaceBusy", interrupted.Status);
        }

        await File.AppendAllTextAsync(
            fixture.Paths.AgentFile("CONTEXT.md"),
            $"{Environment.NewLine}changed after durable Begin{Environment.NewLine}");
        var conflicted = await runtime.InvokeGovernedLoopAsync(input);
        var durable = Assert.IsType<EmbodySense.Core.Application.Loops.Models.CustomLoopInvocationOperation>(
            await new CustomLoopInvocationOperationStore(fixture.Paths).GetAsync(input.OperationId));

        Assert.Equal("Conflict", conflicted.Status);
        Assert.False(conflicted.WasDispatched);
        Assert.Equal(0, fixture.ProviderAttempts);
        Assert.Equal(CustomLoopInvocationBindingState.Unbound, durable.BindingState);
        Assert.Null(durable.SequentialInvocationSnapshot);
    }

    internal static async Task Public_pause_and_resume_reconstructs_the_canonical_plan_from_durable_evidence()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(pauseProvider: true, inferenceSteps: 2);
        var input = fixture.Input("invoke-canonical-pause", "pause-at-boundary");
        await using var runtime = await fixture.CreateRuntimeAsync();

        var invocation = runtime.InvokeGovernedLoopAsync(input);
        await fixture.WaitForProviderAsync();
        using var runStore = new CustomLoopRunStore(fixture.Paths);
        var running = await WaitForRunAsync(runStore, CustomLoopRunStatus.Running);
        Assert.NotNull(running.SequentialAdapterBinding);
        Assert.NotNull(running.SequentialInvocationSnapshot);

        var pause = await runtime.PauseCustomLoopAsync(
            new LoopRunControlInput(running.Id, running.LifecycleVersion, "pause-canonical-run"));
        Assert.Equal("PauseRequested", pause.Status);
        fixture.ReleaseProvider();

        var pausedInvocation = await invocation;
        Assert.Equal("Paused", pausedInvocation.ExecutionStatus);
        Assert.Equal(CustomLoopRunStatus.Paused.ToString(), pausedInvocation.Run?.Status);
        var paused = Assert.IsType<CustomLoopRunRecord>(
            await runStore.GetAsync(running.Id));
        var resume = await runtime.ResumeCustomLoopAsync(
            new LoopRunControlInput(paused.Id, paused.LifecycleVersion, "resume-canonical-run"));

        Assert.Equal("Completed", resume.Status);
        Assert.Equal(CustomLoopRunStatus.Completed.ToString(), resume.Run?.Status);
        Assert.Equal(2, fixture.ProviderAttempts);
        Assert.Equal(paused.SequentialAdapterBinding?.ContentHash, (await runStore.GetAsync(paused.Id))?.SequentialAdapterBinding?.ContentHash);
    }

    internal static async Task Public_resume_revalidates_the_exact_grant_before_the_next_provider_effect(
        AuthorityGrantOperationKind transition)
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(
            pauseProvider: true,
            inferenceSteps: 2,
            grantLifetime: transition == AuthorityGrantOperationKind.Expire ? TimeSpan.FromSeconds(8) : null);
        var token = transition.ToString().ToLowerInvariant();
        var input = fixture.Input($"invoke-before-{token}", $"stop after exact {token}");
        await using var runtime = await fixture.CreateRuntimeAsync();

        var invocation = runtime.InvokeGovernedLoopAsync(input);
        await fixture.WaitForProviderAsync();
        using var runStore = new CustomLoopRunStore(fixture.Paths);
        var running = await WaitForRunAsync(runStore, CustomLoopRunStatus.Running);
        var pause = await runtime.PauseCustomLoopAsync(
            new LoopRunControlInput(running.Id, running.LifecycleVersion, $"pause-before-{token}"));
        Assert.Equal("PauseRequested", pause.Status);
        fixture.ReleaseProvider();

        var pausedInvocation = await invocation;
        Assert.Equal("Paused", pausedInvocation.ExecutionStatus);
        Assert.Equal(1, fixture.ProviderAttempts);
        var paused = Assert.IsType<CustomLoopRunRecord>(await runStore.GetAsync(running.Id));
        await fixture.TransitionGrantAsync(transition);

        var resume = await runtime.ResumeCustomLoopAsync(
            new LoopRunControlInput(paused.Id, paused.LifecycleVersion, $"resume-after-{token}"));

        Assert.Equal("Failed", resume.Status);
        Assert.Equal(CustomLoopRunStatus.Failed.ToString(), resume.Run?.Status);
        Assert.True(
            resume.Detail.Contains("governed effect", StringComparison.OrdinalIgnoreCase)
                || resume.Detail.Contains("before dispatch", StringComparison.OrdinalIgnoreCase),
            resume.Detail);
        Assert.Equal(1, fixture.ProviderAttempts);
        await AssertNoToolExecutionAsync(fixture.Paths);
    }

    internal static async Task Public_absent_exact_grant_is_rejected_without_provider_or_tool_effects()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync();
        var missing = fixture.MissingGrantReference();
        await using var runtime = await fixture.CreateRuntimeAsync();

        var rejected = await runtime.InvokeGovernedLoopAsync(
            fixture.Input("invoke-absent-grant", "the absent grant must not dispatch", missing));

        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal("Rejected", rejected.AdmissionStatus);
        Assert.False(rejected.WasDispatched);
        Assert.Equal(0, fixture.ProviderAttempts);
        await AssertNoToolExecutionAsync(fixture.Paths);
    }

    internal static void Public_frontier_node_contract_preserves_legacy_json_and_round_trips_topology_evidence()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        const string LegacyJson = """
            {
              "schemaVersion": 1,
              "planOrdinal": 3,
              "nodeId": "condition",
              "kind": "Condition",
              "typeId": "exact-text-condition",
              "descriptorVersion": 1,
              "incomingControlEdgeIds": ["infer-to-condition"],
              "outgoingControlEdgeIds": ["condition-false", "condition-true"],
              "status": "Completed",
              "attempt": 1,
              "attemptOperationId": "attempt-condition-2",
              "outcomeEvidenceId": "event-condition-2",
              "outcomeEvidenceHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            }
            """;

        var legacy = Assert.IsType<LoopRunFrontierNodeSnapshot>(
            JsonSerializer.Deserialize<LoopRunFrontierNodeSnapshot>(LegacyJson, options));

        Assert.Equal(0, legacy.ActivationOrdinal);
        Assert.Equal(0, legacy.VisitOrdinal);
        Assert.Null(legacy.CycleId);
        Assert.Null(legacy.CycleIteration);
        Assert.Null(legacy.ControlOutcome);
        Assert.Empty(legacy.SelectedControlEdgeIds);
        Assert.Empty(legacy.SkippedControlEdgeIds);
        Assert.Empty(legacy.JoinArrivals);

        var topology = legacy with
        {
            ActivationOrdinal = 6,
            VisitOrdinal = 2,
            CycleId = "cycle-condition",
            CycleIteration = 2,
            ControlOutcome = GovernedLoopControlCondition.True.ToString(),
            SelectedControlEdgeIds = Array.AsReadOnly(new[] { "condition-true" }),
            SkippedControlEdgeIds = Array.AsReadOnly(new[] { "condition-false" }),
        };
        var json = JsonSerializer.Serialize(topology, options);
        var roundTrip = Assert.IsType<LoopRunFrontierNodeSnapshot>(
            JsonSerializer.Deserialize<LoopRunFrontierNodeSnapshot>(json, options));

        Assert.Equal(topology.ActivationOrdinal, roundTrip.ActivationOrdinal);
        Assert.Equal(topology.VisitOrdinal, roundTrip.VisitOrdinal);
        Assert.Equal(topology.CycleId, roundTrip.CycleId);
        Assert.Equal(topology.CycleIteration, roundTrip.CycleIteration);
        Assert.Equal(topology.ControlOutcome, roundTrip.ControlOutcome);
        Assert.Equal(topology.SelectedControlEdgeIds, roundTrip.SelectedControlEdgeIds);
        Assert.Equal(topology.SkippedControlEdgeIds, roundTrip.SkippedControlEdgeIds);
        Assert.Empty(roundTrip.JoinArrivals);
        var (schemaVersion, planOrdinal, nodeId, kind, typeId, descriptorVersion, incomingEdges, outgoingEdges, status, attempt, attemptOperationId, outcomeEvidenceId, outcomeEvidenceHash) = topology;
        Assert.Equal(1, schemaVersion);
        Assert.Equal(3, planOrdinal);
        Assert.Equal("condition", nodeId);
        Assert.Equal("Condition", kind);
        Assert.Equal("exact-text-condition", typeId);
        Assert.Equal(1, descriptorVersion);
        Assert.Equal(topology.IncomingControlEdgeIds, incomingEdges);
        Assert.Equal(topology.OutgoingControlEdgeIds, outgoingEdges);
        Assert.Equal("Completed", status);
        Assert.Equal(1, attempt);
        Assert.Equal("attempt-condition-2", attemptOperationId);
        Assert.Equal("event-condition-2", outcomeEvidenceId);
        Assert.Equal(new string('a', 64), outcomeEvidenceHash);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)topology.SelectedControlEdgeIds).Add("replacement"));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)topology.SkippedControlEdgeIds).Add("replacement"));

        var join = new LoopRunFrontierNodeSnapshot(
            1,
            4,
            "join",
            "Join",
            "selected-join",
            1,
            Array.AsReadOnly(new[] { "branch-a-to-join", "branch-b-to-join" }),
            Array.AsReadOnly(new[] { "join-to-exit" }),
            "Ready",
            null,
            null,
            null,
            null)
        {
            ActivationOrdinal = 7,
            VisitOrdinal = 1,
            JoinArrivals = Array.AsReadOnly(new[]
            {
                new LoopRunFrontierJoinArrivalSnapshot(1, "branch-a-to-join", 5),
                new LoopRunFrontierJoinArrivalSnapshot(1, "branch-b-to-join", 6),
            }),
        };
        var joinRoundTrip = Assert.IsType<LoopRunFrontierNodeSnapshot>(
            JsonSerializer.Deserialize<LoopRunFrontierNodeSnapshot>(JsonSerializer.Serialize(join, options), options));
        Assert.Equal(join.JoinArrivals, joinRoundTrip.JoinArrivals);
        Assert.Throws<NotSupportedException>(() => ((IList<LoopRunFrontierJoinArrivalSnapshot>)join.JoinArrivals).Add(
            new LoopRunFrontierJoinArrivalSnapshot(1, "replacement", 0)));
    }

    private static async Task<CustomLoopRunRecord> WaitForRunAsync(
        CustomLoopRunStore store,
        CustomLoopRunStatus status)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var run = (await store.ListNonterminalAsync()).SingleOrDefault(item => item.Status == status);
            if (run is not null)
            {
                return run;
            }

            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException($"A canonical run did not reach {status} within the test deadline.");
    }

    private static async Task<CustomLoopRunRecord> WaitForRunAsync(
        CustomLoopRunStore store,
        string runId,
        CustomLoopRunStatus status,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var run = await store.GetAsync(runId);
            if (run?.Status == status)
            {
                return run;
            }

            await Task.Delay(20);
        }

        var current = await store.GetAsync(runId);
        throw new Xunit.Sdk.XunitException(
            $"Canonical run {runId} did not reach {status} within the test deadline; current={current?.Status}, failure={current?.FailureCode}/{current?.FailureDetail}.");
    }

    private static async Task<GovernedLoopWakeEvidence> WaitForCommittedWakeAsync(
        GovernedLoopSleepStore store,
        string wakeId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        GovernedLoopWakeEvidenceReadResult? read = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            read = await store.ReadWakeAsync(wakeId);
            if (read?.Evidence?.Disposition == GovernedLoopWakeDisposition.Committed)
            {
                return read.Evidence;
            }

            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException(
            $"Wake {wakeId} did not reach Committed within the test deadline; read={read?.Status}, disposition={read?.Evidence?.Disposition}.");
    }

    private static async Task AssertNoToolExecutionAsync(WorkspacePaths paths)
    {
        var events = await new AuditLog(paths).ReadTailAsync(1_000);
        Assert.DoesNotContain(events, item => item.Action == AuditSchema.Actions.ToolExecute);
    }

    private static void AssertFrontierProjection(
        LoopRunFrontierSnapshot snapshot,
        GovernedLoopFrontierPosture frontier)
    {
        Assert.Equal(frontier.SchemaVersion, snapshot.SchemaVersion);
        Assert.Equal(frontier.WorkspaceId, snapshot.WorkspaceId);
        Assert.Equal(frontier.Binding.SchemaVersion, snapshot.Binding.SchemaVersion);
        Assert.Equal(frontier.Binding.RunId, snapshot.Binding.RunId);
        Assert.Equal(frontier.Binding.Revision.GraphId, snapshot.Binding.GraphId);
        Assert.Equal(frontier.Binding.Revision.RevisionId, snapshot.Binding.RevisionId);
        Assert.Equal(frontier.Binding.Revision.ExecutableHash, snapshot.Binding.ExecutableHash);
        Assert.Equal(frontier.Binding.ExecutionGeneration, snapshot.Binding.ExecutionGeneration);
        Assert.Equal(frontier.GraphArtifactHash, snapshot.GraphArtifactHash);
        Assert.Equal(frontier.GraphLayoutHash, snapshot.GraphLayoutHash);
        Assert.Equal(frontier.AdmissionReceiptHash, snapshot.AdmissionReceiptHash);
        Assert.Equal(frontier.Payload.FrontierVersion, snapshot.FrontierVersion);
        Assert.Equal(frontier.Payload.ConcurrencyCeiling, snapshot.ConcurrencyCeiling);
        Assert.Equal(frontier.Payload.Status.ToString(), snapshot.Status);
        Assert.Equal(frontier.Payload.UpdatedAtUtc, snapshot.UpdatedAtUtc);
        Assert.Equal(frontier.Payload.ContentHash, snapshot.ContentHash);
        Assert.Equal(frontier.Payload.Nodes.Count, snapshot.Nodes.Count);
        foreach (var (expected, actual) in frontier.Payload.Nodes.Zip(snapshot.Nodes))
        {
            Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
            Assert.Equal(expected.ActivationOrdinal, actual.ActivationOrdinal);
            Assert.Equal(expected.PlanOrdinal, actual.PlanOrdinal);
            Assert.Equal(expected.VisitOrdinal, actual.VisitOrdinal);
            Assert.Equal(expected.NodeId, actual.NodeId);
            Assert.Equal(expected.Descriptor.Kind.ToString(), actual.Kind);
            Assert.Equal(expected.Descriptor.TypeId, actual.TypeId);
            Assert.Equal(expected.Descriptor.Version, actual.DescriptorVersion);
            Assert.Equal(expected.IncomingControlEdgeIds, actual.IncomingControlEdgeIds);
            Assert.Equal(expected.OutgoingControlEdgeIds, actual.OutgoingControlEdgeIds);
            Assert.Equal(expected.CycleId, actual.CycleId);
            Assert.Equal(expected.CycleIteration, actual.CycleIteration);
            Assert.Equal(expected.ControlOutcome?.ToString(), actual.ControlOutcome);
            Assert.Equal(expected.SelectedControlEdgeIds, actual.SelectedControlEdgeIds);
            Assert.Equal(expected.SkippedControlEdgeIds, actual.SkippedControlEdgeIds);
            Assert.Equal(expected.JoinArrivals.Count, actual.JoinArrivals.Count);
            foreach (var (expectedArrival, actualArrival) in expected.JoinArrivals.Zip(actual.JoinArrivals))
            {
                Assert.Equal(expectedArrival.SchemaVersion, actualArrival.SchemaVersion);
                Assert.Equal(expectedArrival.ControlEdgeId, actualArrival.ControlEdgeId);
                Assert.Equal(expectedArrival.SourceActivationOrdinal, actualArrival.SourceActivationOrdinal);
            }

            Assert.Equal(expected.Status.ToString(), actual.Status);
            Assert.Equal(expected.Attempt, actual.Attempt);
            Assert.Equal(expected.AttemptOperationId, actual.AttemptOperationId);
            Assert.Equal(expected.OutcomeEvidenceId, actual.OutcomeEvidenceId);
            Assert.Equal(expected.OutcomeEvidenceHash, actual.OutcomeEvidenceHash);

            Assert.Throws<NotSupportedException>(() => ((IList<string>)actual.IncomingControlEdgeIds).Add("substituted-incoming"));
            Assert.Throws<NotSupportedException>(() => ((IList<string>)actual.OutgoingControlEdgeIds).Add("substituted-outgoing"));
            Assert.Throws<NotSupportedException>(() => ((IList<string>)actual.SelectedControlEdgeIds).Add("substituted-selected"));
            Assert.Throws<NotSupportedException>(() => ((IList<string>)actual.SkippedControlEdgeIds).Add("substituted-skipped"));
            Assert.Throws<NotSupportedException>(() => ((IList<LoopRunFrontierJoinArrivalSnapshot>)actual.JoinArrivals).Add(
                new LoopRunFrontierJoinArrivalSnapshot(1, "substituted-arrival", 0)));
        }

        Assert.Throws<NotSupportedException>(() => ((IList<LoopRunFrontierNodeSnapshot>)snapshot.Nodes).Add(snapshot.Nodes[0]));
    }

    private static async Task SeedAcceptedScheduleEvidenceAsync(
        WorkspacePaths paths,
        ScheduleDefinition definition,
        TriggerDeliveryEnvelope envelope,
        DateTimeOffset scheduledAtUtc)
    {
        Assert.True(ScheduleContractHash.TryComputeDefinition(definition, out var definitionHash, out _));
        var occurrence = new ScheduleOccurrence(
            ScheduleOccurrence.CurrentSchemaVersion,
            1,
            definition.Recurrence.FirstLocalOccurrence,
            scheduledAtUtc,
            definition.TimeZone);
        Assert.True(ScheduleIdentityDerivation.TryDerive(
            definition.ScheduleId,
            definition.Revision,
            definitionHash!,
            occurrence,
            out var identity,
            out _));
        Assert.Equal(identity!.DeliveryId, envelope.DeliveryId);
        Assert.Equal(identity.DeduplicationId, envelope.DeduplicationId);
        Assert.True(TriggerDeliveryHash.TryCompute(envelope, out var envelopeHash, out _));
        var result = new ScheduleDeliveryResultEvidence(
            ScheduleDeliveryResultEvidence.CurrentSchemaVersion,
            ScheduleDeliveryResultKind.Queued,
            "queue-enqueued",
            envelopeHash!,
            envelope.Temporal.ReceivedAtUtc.AddSeconds(1));
        var terminal = new ScheduleTerminalDeliveryEvidence(
            ScheduleTerminalDeliveryEvidence.CurrentSchemaVersion,
            occurrence,
            identity,
            Hash64('9'),
            Hash64('8'),
            Hash64('7'),
            result,
            result.RecordedAtUtc.AddSeconds(1));
        var state = new ScheduleState(
            ScheduleState.CurrentSchemaVersion,
            definition.ScheduleId,
            definition.Revision,
            definitionHash!,
            1,
            true,
            null,
            null,
            null,
            terminal.FinalizedAtUtc,
            null,
            [],
            [terminal]);
        var composition = ScheduleContractValidator.ValidateDefinitionStateComposition(definition, state);
        Assert.True(composition.IsValid, string.Join(',', composition.Errors.Select(error => $"{error.Path}:{error.Code}")));
        var created = await new ScheduleStore(paths).CreateAsync(new ScheduleStoreCreateRequest(definition, state, definitionHash!));
        Assert.Equal(ScheduleStoreMutationStatus.Applied, created.Status);
    }

    private static async Task QueueScheduleAsync(
        WorkspacePaths paths,
        ScheduleScenario scenario,
        DateTimeOffset workerNow)
    {
        using var schedule = ScheduleRuntimeFactory.Create(
            paths,
            scenario,
            scenario,
            scenario,
            new FixedTriggerTimeProvider(workerNow));
        Assert.Equal(ScheduleRuntimeCreateStatus.Created, (await schedule.CreateAsync(scenario.Definition)).Status);
        var result = await schedule.EvaluateOnceAsync(scenario.Definition.ScheduleId);
        Assert.True(result.Status == ScheduleEvaluationStatus.Queued, $"Status={result.Status}; Reason={result.ReasonCode}");
    }

    private static async Task<ScheduleEvaluationResult> QueueScheduleThroughDurableOverlapAsync(
        WorkspacePaths paths,
        ScheduleScenario scenario,
        TimeProvider clock)
    {
        using var runStore = new CustomLoopRunStore(paths);
        using var schedule = ScheduleRuntimeFactory.Create(
            paths,
            scenario,
            new ScheduleRunOverlapAdapter(runStore),
            scenario,
            clock);
        Assert.Equal(ScheduleRuntimeCreateStatus.Created, (await schedule.CreateAsync(scenario.Definition)).Status);
        return await schedule.EvaluateOnceAsync(scenario.Definition.ScheduleId);
    }

    private static TriggerDeliveryEnvelope SubstitutedScheduleEnvelope(
        GovernedRuntimeFixture fixture,
        ScheduleScenario scenario,
        TriggerDeliveryEnvelope canonical,
        string mismatch)
    {
        TriggerDeliveryId deliveryId = canonical.DeliveryId;
        TriggerDeduplicationId deduplicationId = canonical.DeduplicationId;
        TriggerTemporalEvidence temporal = canonical.Temporal;
        TriggerPayloadEvidence payload = canonical.Payload;
        TriggerLoopReference target = canonical.Loop;
        var definition = scenario.Definition;
        var occurrence = scenario.Occurrence;
        if (mismatch is "schedule" or "occurrence")
        {
            if (mismatch == "schedule")
            {
                Assert.True(ScheduleId.TryParse("swapped-schedule", out var swappedScheduleId));
                definition = definition with { ScheduleId = swappedScheduleId! };
            }
            else
            {
                occurrence = occurrence with
                {
                    Ordinal = occurrence.Ordinal + 1,
                    ScheduledLocal = occurrence.ScheduledLocal.AddDays(-1),
                    ScheduledAtUtc = occurrence.ScheduledAtUtc.AddDays(-1),
                };
                Assert.True(TriggerDeliveryFactory.TryCreateTemporalEvidence(
                    canonical.Temporal.ObservedAtUtc,
                    canonical.Temporal.ReceivedAtUtc,
                    occurrence.ScheduledAtUtc,
                    null,
                    null,
                    null,
                    null,
                    out var swappedTemporal,
                    out _));
                temporal = swappedTemporal!;
            }

            Assert.True(ScheduleContractHash.TryComputeDefinition(definition, out var definitionHash, out _));
            Assert.True(ScheduleIdentityDerivation.TryDerive(
                definition.ScheduleId,
                definition.Revision,
                definitionHash!,
                occurrence,
                out var identity,
                out _));
            deliveryId = identity!.DeliveryId;
            deduplicationId = identity.DeduplicationId;
        }
        else if (mismatch == "payload")
        {
            payload = TriggerWorkerTestData.InlinePayload("swapped payload"u8.ToArray());
        }
        else if (mismatch == "target")
        {
            Assert.True(TriggerDeliveryFactory.TryCreateGovernedLoopReference(
                fixture.Publication,
                Assert.IsType<AuthorityGrantReference>(fixture.RestrictedGrant),
                out var swappedTarget,
                out _));
            target = swappedTarget!;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(mismatch));
        }

        Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(1, 1, deliveryId, out var redelivery, out _));
        var canonicalDirective = Assert.IsType<ScheduleExecutionDirective>(canonical.ScheduleExecutionDirective);
        string directiveDefinitionHash = canonicalDirective.DefinitionHash;
        if (mismatch is "schedule" or "occurrence")
        {
            Assert.True(ScheduleContractHash.TryComputeDefinition(definition, out var substitutedDefinitionHash, out _));
            directiveDefinitionHash = substitutedDefinitionHash!;
        }

        Assert.True(ScheduleIdentityDerivation.TryDerive(
            definition.ScheduleId,
            definition.Revision,
            directiveDefinitionHash,
            occurrence,
            out var directiveIdentity,
            out _));
        Assert.Equal(deliveryId, directiveIdentity!.DeliveryId);
        Assert.Equal(deduplicationId, directiveIdentity.DeduplicationId);
        var directive = new ScheduleExecutionDirective(
            ScheduleExecutionDirective.CurrentSchemaVersion,
            definition.ScheduleId,
            definition.Revision,
            directiveDefinitionHash,
            occurrence,
            directiveIdentity,
            target,
            canonicalDirective.Overlap,
            canonicalDirective.PreQueueOverlapEvidenceHash);
        Assert.True(TriggerDeliveryFactory.TryCreateScheduledEnvelope(
            TriggerDeliveryEnvelope.CurrentSchemaVersion,
            deliveryId,
            deduplicationId,
            canonical.Adapter,
            target,
            canonical.ActorContext,
            canonical.Authority,
            temporal,
            payload,
            redelivery,
            directive,
            false,
            null,
            TriggerAdmissionStatus.Unknown,
            TriggerAdmissionReason.Unknown,
            out var substituted,
            out _));
        return substituted!;
    }

    private static string Hash64(char value) => new(value, 64);

    private static string TriggerWorkspaceId(WorkspacePaths paths) => CapabilityWorkspaceScopeId.Create(paths.RootPath)["workspace-sha256:".Length..];

    private sealed class ExactTriggerAuthorizer : ITriggerWorkerCurrentEvidenceAuthorizer
    {
        internal TriggerWorkerCurrentEvidenceInput? LastInput { get; private set; }

        internal int Reads { get; private set; }

        public Task<TriggerWorkerAuthorizationResponse> AuthorizeAsync(TriggerWorkerCurrentEvidenceInput input, DateTimeOffset evaluatedAtUtc, CancellationToken cancellationToken = default)
        {
            Reads++;
            LastInput = input;
            return Task.FromResult(new TriggerWorkerAuthorizationResponse("Authorized", Hash64('a'), "exact current trigger evidence"));
        }
    }

    private sealed class FinalizationConflictOnceScheduleStore(IScheduleStorePort inner) : IScheduleStorePort
    {
        private int _conflicted;

        public Task<ScheduleStoreReadResult> ReadAsync(
            ScheduleId scheduleId,
            CancellationToken cancellationToken = default)
            => inner.ReadAsync(scheduleId, cancellationToken);

        public Task<ScheduleStoreMutationResult> CreateAsync(
            ScheduleStoreCreateRequest request,
            CancellationToken cancellationToken = default)
            => inner.CreateAsync(request, cancellationToken);

        public Task<ScheduleStoreMutationResult> CompareExchangeAsync(
            ScheduleStateCompareExchange request,
            CancellationToken cancellationToken = default)
        {
            if (request.Expected.PendingDelivery?.Phase == SchedulePendingDeliveryPhase.ResultObserved
                && request.Replacement.PendingDelivery is null
                && Interlocked.CompareExchange(ref _conflicted, 1, 0) == 0)
            {
                return Task.FromResult(new ScheduleStoreMutationResult(
                    ScheduleStoreMutationStatus.Conflict,
                    ScheduleContractCopy.Copy(request.Expected)));
            }

            return inner.CompareExchangeAsync(request, cancellationToken);
        }
    }

    private sealed class ResultObservationConflictOnceScheduleStore(IScheduleStorePort inner) : IScheduleStorePort
    {
        private int _conflicted;

        public Task<ScheduleStoreReadResult> ReadAsync(ScheduleId scheduleId, CancellationToken cancellationToken = default)
            => inner.ReadAsync(scheduleId, cancellationToken);

        public Task<ScheduleStoreMutationResult> CreateAsync(ScheduleStoreCreateRequest request, CancellationToken cancellationToken = default)
            => inner.CreateAsync(request, cancellationToken);

        public Task<ScheduleStoreMutationResult> CompareExchangeAsync(
            ScheduleStateCompareExchange request,
            CancellationToken cancellationToken = default)
        {
            if (request.Expected.PendingDelivery?.Phase == SchedulePendingDeliveryPhase.Prepared
                && request.Replacement.PendingDelivery?.Phase == SchedulePendingDeliveryPhase.ResultObserved
                && Interlocked.CompareExchange(ref _conflicted, 1, 0) == 0)
            {
                return Task.FromResult(new ScheduleStoreMutationResult(
                    ScheduleStoreMutationStatus.Conflict,
                    ScheduleContractCopy.Copy(request.Expected)));
            }

            return inner.CompareExchangeAsync(request, cancellationToken);
        }
    }

    private sealed class FixedTriggerTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MonotonicTriggerTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private long _utcTicks = now.UtcDateTime.Ticks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        internal void AdvanceTo(DateTimeOffset candidate)
        {
            var candidateTicks = candidate.UtcDateTime.Ticks;
            while (true)
            {
                var current = Interlocked.Read(ref _utcTicks);
                if (candidateTicks <= current || Interlocked.CompareExchange(ref _utcTicks, candidateTicks, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class ScheduleScenario : IScheduleCurrentEvidencePort, IScheduleOverlapPort, IScheduleTimeZonePort
    {
        private ScheduleScenario(
            ScheduleDefinition definition,
            ScheduleCurrentEvidence evidence,
            DateTime scheduledLocal,
            DateTimeOffset scheduledAtUtc)
        {
            Definition = definition;
            Evidence = evidence;
            ScheduledLocal = scheduledLocal;
            Occurrence = new ScheduleOccurrence(
                ScheduleOccurrence.CurrentSchemaVersion,
                1,
                scheduledLocal,
                scheduledAtUtc,
                definition.TimeZone);
        }

        internal ScheduleDefinition Definition { get; }

        internal ScheduleOccurrence Occurrence { get; }

        private ScheduleCurrentEvidence Evidence { get; }

        private DateTime ScheduledLocal { get; }

        internal static ScheduleScenario Create(
            GovernedRuntimeFixture fixture,
            DateTimeOffset scheduledAtUtc,
            string prompt,
            string scheduleIdValue = "governed-runtime-once",
            ScheduleOverlapPolicy overlap = ScheduleOverlapPolicy.DeferOne)
        {
            Assert.True(ScheduleId.TryParse(scheduleIdValue, out var scheduleId));
            Assert.True(AuthorityActorId.TryParse("scheduled-owner", out var actor, out _));
            Assert.True(AuthorityProfileId.TryParse("governed-loop-profile", out var profileId, out _));
            Assert.True(AuthorityProfileRevision.TryParse("1", out var profileRevision, out _));
            Assert.True(TriggerDeliveryFactory.TryCreateGovernedLoopReference(fixture.Publication, fixture.Grant, out var target, out _));
            Assert.True(TriggerDeliveryFactory.TryCreateActorContext(actor, "schedule", TriggerWorkspaceId(fixture.Paths), "governed-helper", out var actorContext, out _));
            var descriptor = Assert.Single(BuiltInCapabilityCatalog.Descriptors, item => item.Id.Value == ScheduleTriggerCapabilityId);
            Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var descriptorIdentity, out _));
            var adapter = new TriggerAdapterReference(descriptorIdentity!, descriptor.Implementation);
            Assert.True(AuthorityBoundaryReceiptFactory.TryCreate(
                1,
                AuthorityBoundaryDecision.Direct,
                [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)],
                [new AuthorityProfileReference(profileId!, profileRevision!)],
                scheduledAtUtc.AddSeconds(1),
                out var boundaryReceipt,
                out _));
            var authority = new TriggerAuthorityEvidence(new AuthorityProfileReference(profileId!, profileRevision!), boundaryReceipt!);
            var payload = Encoding.UTF8.GetBytes(prompt);
            var payloadHash = CapabilityIntegrityDigest.Compute(payload);
            var scheduledLocal = DateTime.SpecifyKind(scheduledAtUtc.UtcDateTime, DateTimeKind.Unspecified);
            var timeZone = new ScheduleTimeZoneReference("Etc/UTC", Hash64('f'));
            var definition = new ScheduleDefinition(
                ScheduleDefinition.CurrentSchemaVersion,
                scheduleId!,
                1,
                target!,
                adapter,
                actor!,
                "schedule",
                TriggerWorkspaceId(fixture.Paths),
                "governed-helper",
                new AuthorityProfileReference(profileId!, profileRevision!),
                new SchedulePayloadReference($"payload/{scheduleIdValue}", payloadHash),
                SchedulePriority.Normal,
                new ScheduleRecurrenceRule(ScheduleRecurrenceKind.Once, scheduledLocal, null),
                timeZone,
                new ScheduleDaylightSavingPolicy(ScheduleInvalidLocalTimePolicy.ShiftForward, ScheduleAmbiguousLocalTimePolicy.EarlierUtc),
                new ScheduleMisfirePolicy(ScheduleMisfirePolicyKind.FireLatestOnce, 0),
                overlap,
                true);
            var evidence = new ScheduleCurrentEvidence(
                Hash64('9'),
                scheduledAtUtc.AddMinutes(2),
                target!,
                adapter,
                actorContext!,
                authority,
                recurrencePermitted: true,
                payload);
            return new ScheduleScenario(definition, evidence, scheduledLocal, scheduledAtUtc);
        }

        internal TriggerDeliveryEnvelope CreateEnvelope()
        {
            Assert.True(ScheduleContractHash.TryComputeDefinition(Definition, out var definitionHash, out _));
            Assert.True(ScheduleIdentityDerivation.TryDerive(
                Definition.ScheduleId,
                Definition.Revision,
                definitionHash!,
                Occurrence,
                out var identity,
                out _));
            Assert.True(TriggerDeliveryFactory.TryCreateTemporalEvidence(
                Evidence.ObservedAtUtc,
                Evidence.ObservedAtUtc,
                Occurrence.ScheduledAtUtc,
                null,
                null,
                null,
                null,
                out var temporal,
                out _));
            Assert.True(TriggerDeliveryFactory.TryCreateInlinePayload(Evidence.GetResolvedPayload(), out var payload, out _));
            Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(1, 1, identity!.DeliveryId, out var redelivery, out _));
            var directive = new ScheduleExecutionDirective(
                ScheduleExecutionDirective.CurrentSchemaVersion,
                Definition.ScheduleId,
                Definition.Revision,
                definitionHash!,
                Occurrence,
                identity,
                Definition.Target,
                Definition.Overlap,
                Hash64('8'));
            Assert.True(TriggerDeliveryFactory.TryCreateScheduledEnvelope(
                TriggerDeliveryEnvelope.CurrentSchemaVersion,
                identity.DeliveryId,
                identity.DeduplicationId,
                Evidence.Adapter,
                Evidence.Target,
                Evidence.ActorContext,
                Evidence.Authority,
                temporal,
                payload,
                redelivery,
                directive,
                false,
                null,
                TriggerAdmissionStatus.Unknown,
                TriggerAdmissionReason.Unknown,
                out var envelope,
                out _));
            return envelope!;
        }

        public Task<ScheduleCurrentEvidenceResult> ResolveAsync(
            ScheduleDefinition definition,
            ScheduleOccurrence occurrence,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Evidence.ObservedAtUtc >= observedAtUtc
                ? Evidence
                : new ScheduleCurrentEvidence(
                    Evidence.EvidenceHash,
                    observedAtUtc,
                    Evidence.Target,
                    Evidence.Adapter,
                    Evidence.ActorContext,
                    Evidence.Authority,
                    Evidence.RecurrencePermitted,
                    Evidence.GetResolvedPayload());
            return Task.FromResult(new ScheduleCurrentEvidenceResult(ScheduleCurrentEvidenceStatus.Available, current));
        }

        public Task<ScheduleOverlapResult> GetStatusAsync(
            TriggerLoopReference target,
            ScheduleOccurrenceIdentity occurrenceIdentity,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ScheduleOverlapResult(ScheduleOverlapStatus.Clear, Hash64('8')));
        }

        public Task<ScheduleTimeZoneResolution> ResolveLocalAsync(
            ScheduleTimeZoneReference timeZone,
            DateTime scheduledLocal,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.Unique,
                timeZone.RulesFingerprint,
                scheduledLocal,
                new DateTimeOffset(scheduledLocal, TimeSpan.Zero),
                null));
        }

        public Task<ScheduleInstantResolution> ResolveInstantAsync(
            ScheduleTimeZoneReference timeZone,
            DateTimeOffset scheduledAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ScheduleInstantResolution(
                ScheduleInstantResolutionStatus.Resolved,
                timeZone.RulesFingerprint,
                ScheduledLocal));
        }
    }

    private sealed class GovernedRuntimeFixture : IDisposable
    {
        private const string OwnerActorId = "governed-test-owner";
        private static readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
        private readonly TestWorkspace _workspace;
        private readonly string _providerCounterPath;
        private readonly string _providerStartedPath;
        private readonly string _providerReleasePath;
        private readonly string _codexPath;

        private GovernedRuntimeFixture(
            TestWorkspace workspace,
            GovernedLoopRevisionPublicationPin publication,
            AuthorityGrantReference grant,
            AuthorityGrantReference? restrictedGrant,
            string codexPath,
            DateTimeOffset? waitDeadlineUtc)
        {
            _workspace = workspace;
            Publication = publication;
            Grant = grant;
            RestrictedGrant = restrictedGrant;
            _codexPath = codexPath;
            WaitDeadlineUtc = waitDeadlineUtc;
            Paths = new WorkspacePaths(workspace.RootPath);
            _providerCounterPath = workspace.File("governed-provider-attempts.txt");
            _providerStartedPath = workspace.File("governed-provider-started.marker");
            _providerReleasePath = workspace.File("governed-provider-release.marker");
        }

        public WorkspacePaths Paths { get; }

        public GovernedLoopRevisionPublicationPin Publication { get; }

        public AuthorityGrantReference Grant { get; }

        public AuthorityGrantReference? RestrictedGrant { get; }

        public string CodexPath => _codexPath;

        public string TrustRootPath => _workspace.ServerStatePath;

        public DateTimeOffset? WaitDeadlineUtc { get; }

        public int ProviderAttempts
            => File.Exists(_providerCounterPath)
                ? int.Parse(File.ReadAllText(_providerCounterPath), System.Globalization.CultureInfo.InvariantCulture)
                : 0;

        public static async Task<GovernedRuntimeFixture> CreateAsync(
            bool includeRestrictedGrant = false,
            bool pauseProvider = false,
            int inferenceSteps = 1,
            TimeSpan? grantLifetime = null,
            AuthorityGrantCompletionConstraintKind completionConstraint = AuthorityGrantCompletionConstraintKind.None,
            int failFirstAttempts = 0,
            bool scheduleTrigger = false,
            TimeSpan? waitDelay = null)
        {
            Assert.InRange(inferenceSteps, 1, 2);
            Assert.InRange(failFirstAttempts, 0, 2);
            if (waitDelay is { } delay)
            {
                Assert.InRange(delay, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10));
            }

            var workspace = new TestWorkspace();
            try
            {
                await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
                var paths = new WorkspacePaths(workspace.RootPath);
                var role = await CreateRoleAsync(paths, scheduleTrigger);
                var codexPath = await CreateCodexExecutableAsync(workspace, pauseProvider, failFirstAttempts);
                var waitDeadlineUtc = waitDelay is { } exactDelay
                    ? DateTimeOffset.UtcNow.Add(exactDelay)
                    : (DateTimeOffset?)null;
                var publication = await CreatePublishedGraphAsync(
                    workspace,
                    paths,
                    role,
                    inferenceSteps,
                    scheduleTrigger,
                    waitDeadlineUtc);
                var grant = await CreateGrantAsync(
                    workspace,
                    paths,
                    role,
                    publication,
                    "governed-full-grant",
                    FullCeiling(scheduleTrigger),
                    scheduleTrigger,
                    grantLifetime,
                    completionConstraint);
                var restricted = includeRestrictedGrant
                    ? await CreateGrantAsync(
                        workspace,
                        paths,
                        role,
                        publication,
                        "governed-empty-grant",
                        EmptyCeiling(),
                        scheduleTrigger,
                        null,
                        AuthorityGrantCompletionConstraintKind.None)
                    : null;
                return new GovernedRuntimeFixture(workspace, publication, grant, restricted, codexPath, waitDeadlineUtc);
            }
            catch
            {
                workspace.Dispose();
                throw;
            }
        }

        public GovernedLoopRunInvocationInput Input(
            string operationId,
            string prompt,
            AuthorityGrantReference? grant = null)
            => new(operationId, Publication, grant ?? Grant, prompt);

        public Task<AgentRuntime> CreateRuntimeAsync(bool preserveCurrentConversation = false)
            => AgentRuntimeFactory.ForFileCapabilityTrustRoot(
                    new RejectingApprovalPrompt(),
                    _workspace.ServerStatePath,
                    CompatibleRuntimeStatus())
                .CreateAsync(
                    "test-model",
                    _workspace.RootPath,
                    _codexPath,
                    "read-only",
                    AgentRuntimeSurface.Cli,
                    preserveCurrentConversation);

        public async Task WaitForProviderAsync()
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (!File.Exists(_providerStartedPath) && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.True(File.Exists(_providerStartedPath), "The governed provider attempt did not start within the test deadline.");
        }

        public void ReleaseProvider() => File.WriteAllText(_providerReleasePath, "release");

        public AuthorityGrantReference MissingGrantReference()
            => new(GrantId("governed-absent-grant"), GrantRevision(1), "sha256:" + Hash64('f'));

        public async Task TransitionGrantAsync(AuthorityGrantOperationKind kind)
        {
            Assert.Contains(
                kind,
                new[]
                {
                    AuthorityGrantOperationKind.Narrow,
                    AuthorityGrantOperationKind.Suspend,
                    AuthorityGrantOperationKind.Replace,
                    AuthorityGrantOperationKind.Revoke,
                    AuthorityGrantOperationKind.Expire,
                });
            var trust = new FileCapabilityCatalogTrustProvider(_workspace.ServerStatePath);
            var store = new AuthorityProfileStore(Paths, trust);
            var read = await store.ReadAsync(Grant.GrantId);
            Assert.Equal(AuthorityGrantStoreReadStatus.Ready, read.Status);
            var current = Assert.IsType<AuthorityGrant>(read.Snapshot?.CurrentGrant);
            var status = kind switch
            {
                AuthorityGrantOperationKind.Suspend => AuthorityGrantLifecycleStatus.Suspended,
                AuthorityGrantOperationKind.Revoke => AuthorityGrantLifecycleStatus.Revoked,
                AuthorityGrantOperationKind.Expire => AuthorityGrantLifecycleStatus.Expired,
                _ => AuthorityGrantLifecycleStatus.Active,
            };
            var ceiling = kind == AuthorityGrantOperationKind.Narrow
                ? new AuthorityCeiling(
                    current.RequestedCeiling.Capabilities
                        .Where(item => item.Id.Value != ModelInferenceCapabilityId)
                        .ToArray(),
                    current.RequestedCeiling.DataClasses,
                    current.RequestedCeiling.MaxTargetCount,
                    current.RequestedCeiling.MaxSideEffectClass,
                    current.RequestedCeiling.AllowsRecurrence,
                    current.RequestedCeiling.AllowsExternalPublication,
                    current.RequestedCeiling.AllowsIrreversibleAction)
                : current.RequestedCeiling;
            var recordedAtUtc = kind == AuthorityGrantOperationKind.Expire
                ? current.Boundary.ExpiresAtUtc!.Value
                : DateTimeOffset.UtcNow;
            var successor = AuthorityGrantHash.Apply(current with
            {
                Revision = GrantRevision(current.Revision.Value + 1),
                PredecessorRevision = current.Revision,
                PredecessorContentHash = current.ContentHash,
                Status = status,
                RequestedCeiling = ceiling,
                RecordedAtUtc = recordedAtUtc,
                ContentHash = string.Empty,
            });
            var operationId = $"transition-{kind.ToString().ToLowerInvariant()}";
            var requestHash = Hash64('b');
            var observed = await store.ReadForMutationAsync(current.GrantId, operationId, requestHash);
            var evidence = new AuthorityGrantOperationEvidence(
                AuthorityGrantContractLimits.CurrentSchemaVersion,
                operationId,
                requestHash,
                kind,
                AuthorityGrantOperationOutcome.Committed,
                AuthorityGrantOperationFailureCode.None,
                successor.GrantId,
                current.Revision.Value,
                new AuthorityGrantReference(successor.GrantId, successor.Revision, successor.ContentHash),
                successor.ChangedByActorId,
                successor.Reason,
                Hash64('c'),
                kind is AuthorityGrantOperationKind.Narrow or AuthorityGrantOperationKind.Replace ? Hash64('d') : null,
                successor.RecordedAtUtc);
            var committed = await store.CommitAsync(new AuthorityGrantStoreMutation(observed.StoreGeneration, successor, evidence));
            Assert.Equal(AuthorityGrantStoreCommitStatus.Committed, committed.Status);
        }

        public void Dispose() => _workspace.Dispose();

        private CodexRuntimeStatus CompatibleRuntimeStatus()
            => new(
                CodexRuntimeCompatibility.Compatible,
                _codexPath,
                _codexPath,
                "codex-cli compatible-governed-test",
                "test-model",
                "controlled test",
                "The exact fake app-server and provider path remains exercised; redundant compatibility probes are covered separately.");

        private static async Task<ContextualRoleRevision> CreateRoleAsync(WorkspacePaths paths, bool scheduleTrigger)
        {
            var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
            var revision = ContextualRoleRevisionContentHash.Apply(new ContextualRoleRevision(
                ContextualRoleLimits.SchemaVersion,
                new ContextualRoleRevisionIdentity("governed-helper", 1),
                string.Empty,
                "Governed helper",
                "Execute one bounded canonical inference graph.",
                ContextualRoleStatus.Published,
                new ContextualRoleProvenance(OwnerActorId, _now.AddMinutes(-2), _now.AddMinutes(-1)),
                new ContextualRoleWorkspaceApplicability(ImmutableArray.Create(workspaceId)),
                new ContextualRoleInstructionSourceReference(
                    ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown,
                    "role",
                    ContextualRoleInstructionClassification.RoleInstruction),
                new ContextualRolePolicyMaxima(
                    scheduleTrigger
                        ? ImmutableArray.Create(ConversationTurnCapabilityId, ModelInferenceCapabilityId, ScheduleTriggerCapabilityId)
                        : ImmutableArray.Create(ConversationTurnCapabilityId, ModelInferenceCapabilityId))));
            var request = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest(
                "create-governed-helper-role",
                string.Empty,
                ContextualRoleRevisionMutationKind.Create,
                revision.Identity.RoleId,
                OwnerActorId,
                revision,
                null,
                _now));
            using var store = new ContextualRoleRevisionStore(paths, workspaceId);
            var result = await store.MutateAsync(request);
            Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, result.Status);
            return Assert.IsType<ContextualRoleRevision>(result.Revision);
        }

        private static async Task<GovernedLoopRevisionPublicationPin> CreatePublishedGraphAsync(
            TestWorkspace workspace,
            WorkspacePaths paths,
            ContextualRoleRevision role,
            int inferenceSteps,
            bool scheduleTrigger,
            DateTimeOffset? waitDeadlineUtc)
        {
            var candidate = Candidate(
                new ContextualRoleRevisionPin(role.Identity, role.ContentHash),
                inferenceSteps,
                scheduleTrigger,
                waitDeadlineUtc);
            var normalized = GovernedLoopGraphNormalizer.Normalize(candidate);
            Assert.True(normalized.IsValid);
            var revision = normalized.Graph!.RevisionReference;
            var lifecycle = new ContextualRoleLifecycleSnapshot(
                1,
                role.Identity.RoleId,
                role.Identity,
                ContextualRoleLifecycleState.Active,
                "create-governed-helper-role",
                ContextualRoleRevisionMutationKind.Create,
                _now);
            var authority = new StaticAuthorityProvider(new GovernedLoopAuthoritySnapshot(
                true,
                Hash64('a'),
                new ContextualRoleRevisionPin(role.Identity, role.ContentHash),
                role,
                lifecycle,
                CapabilityWorkspaceScopeId.Create(paths.RootPath),
                ContextualRoleInstructionSourceProbeStatus.Ready,
                role.PolicyMaxima.CapabilityIds,
                CustomLoopLimits.MaxGraphNodeAttempts,
                100_000,
                CustomLoopLimits.MaxGraphNodeEvidenceItems,
                100));
            var service = GovernedLoopGraphAuthoringFactory.Create(
                paths,
                new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath),
                new StaticNodeCatalog(Catalog(candidate)),
                authority,
                new AllowingActorAuthorizer());
            var created = await service.MutateAsync(new GovernedLoopGraphAuthoringRequest(
                1,
                new GovernedLoopRevisionLifecycleRequest(
                    1,
                    "create-governed-sequential-graph",
                    GovernedLoopRevisionOperationKind.CreateDraft,
                    revision.GraphId,
                    Actor(),
                    GovernedLoopRevisionLifecycleStatus.Unknown,
                    0,
                    null,
                    null,
                    revision,
                    null,
                    null),
                candidate));
            Assert.True(
                created.Status == GovernedLoopGraphAuthoringStatus.Committed,
                string.Join(Environment.NewLine, created.GraphValidationErrors.Select(error => $"{error.Code}: {error.Message}")));
            var head = Assert.IsType<GovernedLoopRevisionLifecycleHead>(created.LifecycleResult?.Head);
            var published = await service.MutateAsync(new GovernedLoopGraphAuthoringRequest(
                1,
                new GovernedLoopRevisionLifecycleRequest(
                    1,
                    "publish-governed-sequential-graph",
                    GovernedLoopRevisionOperationKind.Publish,
                    head.GraphId,
                    Actor(),
                    head.Status,
                    head.LifecycleVersion,
                    head.DraftRevision,
                    head.PublishedRevision,
                    null,
                    head.DraftRevision,
                    null),
                null));
            Assert.Equal(GovernedLoopGraphAuthoringStatus.Committed, published.Status);
            return Assert.IsType<GovernedLoopRevisionPublicationPin>(published.LifecycleResult?.Head?.PublishedRevision);
        }

        private static async Task<AuthorityGrantReference> CreateGrantAsync(
            TestWorkspace workspace,
            WorkspacePaths paths,
            ContextualRoleRevision role,
            GovernedLoopRevisionPublicationPin publication,
            string grantId,
            AuthorityCeiling requestedCeiling,
            bool scheduleTrigger,
            TimeSpan? grantLifetime,
            AuthorityGrantCompletionConstraintKind completionConstraint)
        {
            var trust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
            var store = new AuthorityProfileStore(paths, trust);
            var profileId = ProfileId("governed-loop-profile");
            var profileRead = await store.ReadAsync(profileId.Value);
            AuthorityProfileRecord profile;
            if (profileRead.Status == AuthorityProfileReadStatus.NotFound)
            {
                var declaration = new AuthorityProfile(
                    AuthorityProfile.CurrentSchemaVersion,
                    profileId,
                    ProfileRevision(1),
                    AuthorityProfileStatus.Active,
                    Purpose("Bound canonical sequential test execution."),
                    new AuthorityProvenance(Actor(), AuthorityProvenanceKind.UserDeclaration),
                    _now.AddMinutes(-5),
                    _now.AddDays(1),
                    FullCeiling(scheduleTrigger),
                    []);
                var created = await store.MutateAsync(new AuthorityProfileMutation(
                    AuthorityProfileMutationKind.Create,
                    "create-governed-loop-profile",
                    0,
                    declaration,
                    null,
                    null,
                    Actor(),
                    Purpose("Create the bounded governed-loop authority profile.")));
                Assert.Equal(AuthorityProfileMutationStatus.Applied, created.Status);
                profile = Assert.IsType<AuthorityProfileRecord>(created.Record);
            }
            else
            {
                Assert.Equal(AuthorityProfileReadStatus.Available, profileRead.Status);
                profile = Assert.IsType<AuthorityProfileRecord>(profileRead.Record);
            }

            var binding = new AuthorityGrantBinding(
                new AuthorityGrantProfilePin(
                    new AuthorityProfileReference(profile.ProfileId, profile.CurrentProfile.Revision),
                    profile.CurrentHash),
                new ContextualRoleRevisionPin(role.Identity, role.ContentHash),
                publication);
            var recordedAtUtc = DateTimeOffset.UtcNow;
            var grant = AuthorityGrantHash.Apply(new AuthorityGrant(
                AuthorityGrantContractLimits.CurrentSchemaVersion,
                GrantId(grantId),
                GrantRevision(1),
                null,
                null,
                AuthorityGrantLifecycleStatus.Active,
                binding,
                requestedCeiling,
                new AuthorityGrantBoundary(
                    recordedAtUtc.AddMinutes(-1),
                    recordedAtUtc.Add(grantLifetime ?? TimeSpan.FromHours(12)),
                    completionConstraint),
                Actor(),
                Purpose("Delegate one exact published governed loop."),
                recordedAtUtc,
                string.Empty));
            var operationId = "create-" + grantId;
            var requestHash = grantId.EndsWith("empty-grant", StringComparison.Ordinal) ? Hash64('3') : Hash64('2');
            var observed = await store.ReadForMutationAsync(grant.GrantId, operationId, requestHash);
            var evidence = new AuthorityGrantOperationEvidence(
                AuthorityGrantContractLimits.CurrentSchemaVersion,
                operationId,
                requestHash,
                AuthorityGrantOperationKind.Create,
                AuthorityGrantOperationOutcome.Committed,
                AuthorityGrantOperationFailureCode.None,
                grant.GrantId,
                0,
                new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash),
                grant.ChangedByActorId,
                grant.Reason,
                Hash64('4'),
                Hash64('5'),
                grant.RecordedAtUtc);
            var committed = await store.CommitAsync(new AuthorityGrantStoreMutation(observed.StoreGeneration, grant, evidence));
            Assert.Equal(AuthorityGrantStoreCommitStatus.Committed, committed.Status);
            var reference = new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash);
            if (requestedCeiling.Capabilities.Count != 0)
            {
                await AssertDependenciesActiveAsync(workspace, paths, binding, reference);
            }

            return reference;
        }

        private static async Task AssertDependenciesActiveAsync(
            TestWorkspace workspace,
            WorkspacePaths paths,
            AuthorityGrantBinding binding,
            AuthorityGrantReference reference)
        {
            var trust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
            var transaction = new CapabilityAuthorityTransaction(paths);
            var lifecycleStore = new GovernedLoopRevisionLifecycleStore(paths, trust, authorityTransaction: transaction);
            var graphStore = new GovernedLoopGraphRevisionStore(paths, lifecycleStore, trust, authorityTransaction: transaction);
            var publicationSource = new GovernedLoopPublishedRevisionSource(lifecycleStore, transaction);
            var bindingSource = new GovernedLoopGrantBindingSource(publicationSource, graphStore, transaction);
            using var roleStore = new ContextualRoleRevisionStore(
                paths,
                CapabilityWorkspaceScopeId.Create(paths.RootPath),
                authorityTransaction: transaction);
            var roleSource = new AuthorityGrantRoleSource(
                CapabilityWorkspaceScopeId.Create(paths.RootPath),
                roleStore,
                roleStore,
                new WorkspaceContextualRoleInstructionSourceProbe(paths),
                transaction);
            var authorityStore = new AuthorityProfileStore(paths, trust, authorityTransaction: transaction);
            var profileSource = new AuthorityGrantProfileSource(authorityStore);
            var resolver = new AuthorityGrantResolver(
                authorityStore,
                profileSource,
                roleSource,
                publicationSource,
                bindingSource,
                transaction);
            var bindingResult = await bindingSource.ResolveAsync(binding.Loop);
            var roleResult = await roleSource.ResolveAsync(binding.Role);
            var profileRead = await authorityStore.ReadAsync(binding.Profile.Reference.ProfileId.Value);
            var profileEvaluatedAtUtc = DateTimeOffset.UtcNow;
            var profileResult = await profileSource.ResolveAsync(binding.Profile, profileEvaluatedAtUtc);
            var grantResult = await resolver.ResolveAsync(reference);

            Assert.Equal(AuthorityGrantDependencyStatus.Active, bindingResult.Status);
            Assert.Equal(AuthorityGrantDependencyStatus.Active, roleResult.Status);
            Assert.Equal(AuthorityProfileReadStatus.Available, profileRead.Status);
            Assert.Equal(AuthorityGrantDependencyStatus.Active, profileResult.Status);
            Assert.Equal(AuthorityGrantResolutionStatus.Active, grantResult.Status);
        }

        private static GovernedLoopGraphCandidate Candidate(
            ContextualRoleRevisionPin role,
            int inferenceSteps,
            bool scheduleTrigger,
            DateTimeOffset? waitDeadlineUtc)
        {
            var nodes = new List<GovernedLoopNodeDefinition>
            {
                new(
                    "trigger",
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Trigger, scheduleTrigger ? "schedule-trigger" : "manual-trigger", 1),
                    [Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context)],
                    GovernedLoopAuthorityCeiling.Create(scheduleTrigger ? [ScheduleTriggerCapabilityId] : []),
                    new Dictionary<string, string>()),
            };
            var controlEdges = new List<GovernedLoopControlEdgeDefinition>();
            var bindings = new List<GovernedLoopBindingDefinition>();
            var display = new List<GovernedLoopNodeDisplayMetadata>
            {
                new("trigger", "Trigger", "Start.", 0, 0),
            };
            var dataSourceNodeId = "trigger";
            var dataSourcePortId = "request";
            for (var index = 1; index <= inferenceSteps; index++)
            {
                var nodeId = index == 1 ? "inference" : $"inference-{index}";
                nodes.Add(new GovernedLoopNodeDefinition(
                    nodeId,
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Inference, "provider-inference", 1),
                    [Port("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context), Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                    GovernedLoopAuthorityCeiling.Create([ModelInferenceCapabilityId]),
                    new Dictionary<string, string> { ["instruction"] = $"Answer bounded inference step {index}." }));
                controlEdges.Add(new GovernedLoopControlEdgeDefinition(
                    index == 1 ? "trigger-to-inference" : $"inference-{index - 1}-to-inference-{index}",
                    dataSourceNodeId,
                    nodeId,
                    index == 1 ? GovernedLoopControlCondition.Always : GovernedLoopControlCondition.Success));
                bindings.Add(new GovernedLoopBindingDefinition($"request-binding-{index}", GovernedLoopBindingKind.Data, dataSourceNodeId, dataSourcePortId, nodeId, "request"));
                bindings.Add(new GovernedLoopBindingDefinition($"context-binding-{index}", GovernedLoopBindingKind.Context, "trigger", "invocation-context", nodeId, "invocation-context"));
                display.Add(new GovernedLoopNodeDisplayMetadata(nodeId, $"Inference {index}", "Infer.", index * 100, 0));
                dataSourceNodeId = nodeId;
                dataSourcePortId = "result";
            }

            var controlSourceNodeId = dataSourceNodeId;
            if (waitDeadlineUtc is { } waitDeadline)
            {
                nodes.Add(new GovernedLoopNodeDefinition(
                    "wait",
                    GovernedLoopSequentialNodeDescriptors.TimestampWait,
                    [],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [GovernedLoopWaitVocabulary.DeadlineUtcParameter] = waitDeadline.ToUniversalTime().ToString(
                            GovernedLoopWaitVocabulary.CanonicalUtcTimestampFormat,
                            System.Globalization.CultureInfo.InvariantCulture),
                    }));
                controlEdges.Add(new GovernedLoopControlEdgeDefinition(
                    "inference-to-wait",
                    dataSourceNodeId,
                    "wait",
                    GovernedLoopControlCondition.Success));
                display.Add(new GovernedLoopNodeDisplayMetadata("wait", "Wait", "Sleep durably.", (inferenceSteps + 1) * 100, 0));
                controlSourceNodeId = "wait";
            }

            nodes.Add(new GovernedLoopNodeDefinition(
                "exit",
                new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Exit, "success-exit", 1),
                [Port("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]),
                new Dictionary<string, string>()));
            controlEdges.Add(new GovernedLoopControlEdgeDefinition("inference-to-exit", controlSourceNodeId, "exit", GovernedLoopControlCondition.Success));
            bindings.Add(new GovernedLoopBindingDefinition("result-binding", GovernedLoopBindingKind.Data, dataSourceNodeId, dataSourcePortId, "exit", "result"));
            display.Add(new GovernedLoopNodeDisplayMetadata("exit", "Exit", "Finish.", (inferenceSteps + (waitDeadlineUtc is null ? 1 : 2)) * 100, 0));

            return new GovernedLoopGraphCandidate(
                1,
                "governed-sequential-loop",
                "revision-1",
                "Execute one canonical sequential inference chain.",
                role,
                "trigger",
                ["exit"],
                GovernedLoopAuthorityCeiling.Create(
                    scheduleTrigger
                        ? [ConversationTurnCapabilityId, ModelInferenceCapabilityId, ScheduleTriggerCapabilityId]
                        : [ConversationTurnCapabilityId, ModelInferenceCapabilityId]),
                [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
                nodes,
                controlEdges,
                bindings,
                new GovernedLoopOutputContract(
                    "Return the bounded result.",
                    [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
                new GovernedLoopDisplayMetadata("Governed sequential loop", "Public Startup composition test.", display));
        }

        private static GovernedLoopPortDefinition Port(
            string id,
            GovernedLoopPortDirection direction,
            GovernedLoopBindingKind kind)
            => new(id, direction, kind, "text", true);

        private static GovernedLoopNodeCatalogSnapshot Catalog(GovernedLoopGraphCandidate candidate)
        {
            var schemas = candidate.ValueSchemas!.ToDictionary(schema => schema!.Id, schema => schema!.Kind, StringComparer.Ordinal);
            var terminal = candidate.TerminalNodeIds!.ToHashSet(StringComparer.Ordinal);
            var descriptors = candidate.Nodes!.DistinctBy(node => node!.Descriptor).Select(node =>
            {
                if (GovernedLoopWaitNodeCatalogContract.TryResolve(node!.Descriptor, out var waitContract))
                {
                    return waitContract!;
                }

                var outcomes = candidate.ControlEdges!
                    .Where(edge => string.Equals(edge!.FromNodeId, node!.Id, StringComparison.Ordinal))
                    .Select(edge => edge!.Condition)
                    .Distinct()
                    .Order()
                    .ToArray();
                return new GovernedLoopNodeCatalogDescriptor(
                    node!.Descriptor,
                    true,
                    true,
                    node.Descriptor.Kind == GovernedLoopNodeKind.Trigger,
                    terminal.Contains(node.Id),
                    outcomes,
                    outcomes,
                    GovernedLoopJoinPolicy.None,
                    0,
                    false,
                    null,
                    null,
                    node.Ports.Select(port => new GovernedLoopCatalogPortContract(
                        port.Id,
                        port.Direction,
                        port.BindingKind,
                        GovernedLoopValueKindSet.Create([schemas[port.ValueSchemaId]]),
                        port.Required)).ToArray(),
                    node.Parameters.Select(parameter => new GovernedLoopCatalogParameterContract(
                        parameter.Key,
                        GovernedLoopParameterValueKind.Text,
                        true,
                        1,
                        CustomLoopLimits.MaxGraphParameterValueCharacters,
                        null,
                        null,
                        [])).ToArray(),
                    node.AuthorityCeiling.CapabilityIds,
                    new GovernedLoopNodeResourceBudget(0, 0, 0, 0));
            }).ToArray();
            return new GovernedLoopNodeCatalogSnapshot(true, "governed-runtime-catalog", descriptors);
        }

        private static AuthorityCeiling FullCeiling(bool scheduleTrigger = false)
            => new(
                BuiltInCapabilityCatalog.Descriptors
                    .Where(item => item.Id.Value is ConversationTurnCapabilityId or ModelInferenceCapabilityId
                        || scheduleTrigger && item.Id.Value == ScheduleTriggerCapabilityId)
                    .Select(CreateCapabilityIdentity)
                    .OrderBy(item => item.Id.Value, StringComparer.Ordinal)
                    .ToArray(),
                [],
                1,
                CapabilitySideEffectClass.None,
                scheduleTrigger,
                true,
                false);

        private static AuthorityCeiling EmptyCeiling()
            => new([], [], 0, CapabilitySideEffectClass.None, false, false, false);

        private static CapabilityDescriptorIdentity CreateCapabilityIdentity(CapabilityDescriptor descriptor)
        {
            Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out var validation));
            Assert.True(validation.IsValid);
            return identity!;
        }

        private static AuthorityActorId Actor()
        {
            Assert.True(AuthorityActorId.TryParse(OwnerActorId, out var actor, out _));
            return actor!;
        }

        private static AuthorityPurpose Purpose(string value)
        {
            Assert.True(AuthorityPurpose.TryParse(value, out var purpose, out _));
            return purpose!;
        }

        private static AuthorityProfileId ProfileId(string value)
        {
            Assert.True(AuthorityProfileId.TryParse(value, out var id, out _));
            return id!;
        }

        private static AuthorityProfileRevision ProfileRevision(int value)
        {
            Assert.True(AuthorityProfileRevision.TryParse(value.ToString(System.Globalization.CultureInfo.InvariantCulture), out var revision, out _));
            return revision!;
        }

        private static AuthorityGrantId GrantId(string value)
        {
            Assert.True(AuthorityGrantId.TryParse(value, out var id, out _));
            return id!;
        }

        private static AuthorityGrantRevision GrantRevision(int value)
        {
            Assert.True(AuthorityGrantRevision.TryParse(value.ToString(System.Globalization.CultureInfo.InvariantCulture), out var revision, out _));
            return revision!;
        }

        private static async Task<string> CreateCodexExecutableAsync(
            TestWorkspace workspace,
            bool pauseProvider,
            int failFirstAttempts)
        {
            var scriptPath = workspace.File("fake-governed-codex.js");
            var commandPath = workspace.File(OperatingSystem.IsWindows() ? "fake-governed-codex.cmd" : "fake-governed-codex");
            var counterPath = System.Text.Json.JsonSerializer.Serialize(workspace.File("governed-provider-attempts.txt"));
            var startedPath = System.Text.Json.JsonSerializer.Serialize(workspace.File("governed-provider-started.marker"));
            var releasePath = System.Text.Json.JsonSerializer.Serialize(workspace.File("governed-provider-release.marker"));
            var pauseEveryTurn = pauseProvider ? "true" : "false";
            await File.WriteAllTextAsync(scriptPath, $$"""
                const fs = require("node:fs");
                const readline = require("node:readline");
                const counterPath = {{counterPath}};
                const startedPath = {{startedPath}};
                const releasePath = {{releasePath}};
                const pauseEveryTurn = {{pauseEveryTurn}};
                const failFirstAttempts = {{failFirstAttempts}};

                if (process.argv.slice(2).includes("--version")) {
                  process.stdout.write("codex-cli compatible-governed-test\n");
                  process.exit(0);
                }

                const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
                let threadNumber = 0;
                let turnNumber = 0;

                function write(value) {
                  process.stdout.write(`${JSON.stringify(value)}\n`);
                }

                function complete(threadId, turnId, text) {
                  write({ method: "item/agentMessage/delta", params: { threadId, turnId, delta: text } });
                  write({
                    method: "turn/completed",
                    params: { threadId, turnId, turn: { id: turnId, status: "completed", items: [{ type: "agentMessage", phase: "final_answer", text }] } }
                  });
                }

                function prompt(message) {
                  const text = (message.params?.input ?? []).map(item => String(item?.text ?? "")).join("\n");
                  const marker = "Current user message:";
                  const index = text.indexOf(marker);
                  return index < 0 ? text : text.slice(index + marker.length).trim();
                }

                function waitForRelease(callback) {
                  if (fs.existsSync(releasePath)) {
                    callback();
                    return;
                  }

                  setTimeout(() => waitForRelease(callback), 20);
                }

                input.on("line", line => {
                  const message = JSON.parse(line);
                  switch (message.method) {
                    case "initialize":
                      write({ id: message.id, result: {} });
                      break;
                    case "model/list":
                      write({ id: message.id, result: { data: [{ id: "test-model", model: "test-model" }], nextCursor: null } });
                      break;
                    case "thread/start": {
                      const threadId = `thread-governed-${++threadNumber}`;
                      write({ id: message.id, result: { thread: { id: threadId } } });
                      break;
                    }
                    case "turn/start": {
                      const threadId = String(message.params?.threadId ?? `thread-governed-${threadNumber}`);
                      const turnId = `turn-governed-${++turnNumber}`;
                      const userPrompt = prompt(message);
                      const attempts = fs.existsSync(counterPath) ? Number(fs.readFileSync(counterPath, "utf8")) : 0;
                      const attemptNumber = attempts + 1;
                      fs.writeFileSync(counterPath, String(attemptNumber));
                      write({ id: message.id, result: { turn: { id: turnId } } });
                      if (attemptNumber <= failFirstAttempts) {
                        write({
                          method: "turn/completed",
                          params: { threadId, turnId, turn: { id: turnId, status: "failed", error: { message: "planned governed provider failure" }, items: [] } }
                        });
                        break;
                      }
                      const finish = () => complete(threadId, turnId, `governed response: ${userPrompt}`);
                      if (pauseEveryTurn && !fs.existsSync(releasePath)) {
                        fs.writeFileSync(startedPath, "started");
                        waitForRelease(finish);
                      } else {
                        finish();
                      }
                      break;
                    }
                    default:
                      break;
                  }
                });
                """);
            if (OperatingSystem.IsWindows())
            {
                await File.WriteAllTextAsync(commandPath, """
                    @echo off
                    node "%~dp0fake-governed-codex.js" %*
                    """);
            }
            else
            {
                var escaped = scriptPath
                    .Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal)
                    .Replace("$", "\\$", StringComparison.Ordinal)
                    .Replace("`", "\\`", StringComparison.Ordinal);
                await File.WriteAllTextAsync(commandPath, $"#!/bin/sh\nexec node \"{escaped}\" \"$@\"\n");
                File.SetUnixFileMode(
                    commandPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return commandPath;
        }
    }

    private sealed class UnusedSleepPosture : IGovernedLoopSleepCurrentPosturePort
    {
        public Task<GovernedLoopSleepCurrentPostureReadResult?> ReadAsync(
            GovernedLoopExecutionBinding binding,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Trigger-only overlap retry must not read sleep posture.");
    }

    private sealed class UnusedWakeContinuation : IGovernedLoopWakeContinuationPort
    {
        public Task<GovernedLoopWakeContinuationResult?> ContinueAsync(
            GovernedLoopWakeContinuationRequest request,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Trigger-only overlap retry must not continue a wake.");

        public Task<GovernedLoopWakeContinuationResult?> ReconcileAsync(
            GovernedLoopWakeContinuationRequest request,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Trigger-only overlap retry must not reconcile a wake.");
    }

    private sealed class UnusedWakeVerification : IGovernedLoopAuthenticatedWakeVerificationPort
    {
        public Task<GovernedLoopAuthenticatedWakeVerificationResult?> VerifyAsync(
            GovernedLoopAuthenticatedWakeVerificationRequest request,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Trigger-only overlap retry must not verify wake evidence.");
    }

    private sealed class StaticNodeCatalog(GovernedLoopNodeCatalogSnapshot snapshot) : IGovernedLoopNodeCatalog
    {
        public Task<GovernedLoopNodeCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);
    }

    private sealed class StaticAuthorityProvider(GovernedLoopAuthoritySnapshot snapshot) : IGovernedLoopAuthoritySnapshotProvider
    {
        public Task<GovernedLoopAuthoritySnapshot> GetSnapshotAsync(
            ContextualRoleRevisionPin? owningRole,
            CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);
    }

    private sealed class AllowingActorAuthorizer : IGovernedLoopRevisionActorAuthorizer
    {
        public Task<GovernedLoopRevisionActorAuthorization> AuthorizeAsync(
            GovernedLoopRevisionActorAuthorizationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GovernedLoopRevisionActorAuthorization(
                GovernedLoopRevisionActorAuthorizationStatus.Authorized,
                request.Request.OperationId,
                request.RequestHash,
                request.Request.ActorId,
                Hash64('9')));
    }

    private sealed class RejectingApprovalPrompt : IAgentToolApprovalPrompt
    {
        public Task<(bool Approved, string DecisionBy, string Detail)> RequestApprovalAsync(
            AgentToolApprovalRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult((false, "test", "No governed workspace tool is used in this graph."));
    }
}
