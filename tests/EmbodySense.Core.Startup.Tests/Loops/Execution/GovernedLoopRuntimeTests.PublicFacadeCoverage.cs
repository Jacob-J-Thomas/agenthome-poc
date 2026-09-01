using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Triggers.Models;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Triggers;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

internal static partial class GovernedLoopRuntimeTests
{
    internal static async Task Public_governed_invocation_maps_invalid_and_missing_revision_reads_without_dispatch()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync();
        await using var runtime = await fixture.CreateRuntimeAsync();

        var invalid = await runtime.InvokeGovernedLoopAsync(null!);
        Assert.Equal("Invalid", invalid.Status);
        Assert.False(invalid.WasDispatched);
        Assert.Null(invalid.Run);

        var missingRevision = GovernedLoopRevisionReference.Create(
            GovernedLoopRevisionReference.CurrentSchemaVersion,
            "governed-missing-graph",
            fixture.Publication.Revision.RevisionId,
            fixture.Publication.Revision.ExecutableHash);
        var missingPublication = fixture.Publication with { Revision = missingRevision };
        var missing = await runtime.InvokeGovernedLoopAsync(fixture.Input("invoke-missing-revision", "missing revision must not dispatch") with { Publication = missingPublication });
        Assert.Equal("NotFound", missing.Status);
        Assert.False(missing.WasDispatched);
        Assert.Null(missing.Run);

        var unavailableReference = GovernedLoopRevisionReference.Create(
            GovernedLoopRevisionReference.CurrentSchemaVersion,
            BuiltInLoopIds.DefaultConversation,
            fixture.Publication.Revision.RevisionId,
            fixture.Publication.Revision.ExecutableHash);
        var unavailablePublication = fixture.Publication with { Revision = unavailableReference };
        var unavailable = await runtime.InvokeGovernedLoopAsync(fixture.Input("invoke-unavailable-revision", "unavailable revision must not dispatch") with { Publication = unavailablePublication });
        Assert.Equal("Unavailable", unavailable.Status);
        Assert.False(unavailable.WasDispatched);
        Assert.Null(unavailable.Run);
    }

    internal static async Task Public_governed_invocation_maps_corrupt_revision_artifacts_to_invalid_without_dispatch()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync();
        var artifactPath = Path.Combine(
            fixture.Paths.GovernedLoopRevisionsPath,
            "graph-authoring",
            "artifacts",
            fixture.Publication.Revision.GraphId,
            fixture.Publication.Revision.RevisionId + ".json");
        var original = await File.ReadAllBytesAsync(artifactPath);
        try
        {
            await File.WriteAllTextAsync(artifactPath, "{\"corrupt\":true}");
            await using var runtime = await fixture.CreateRuntimeAsync();
            var response = await runtime.InvokeGovernedLoopAsync(fixture.Input("invoke-corrupt-revision", "corrupt revision must not dispatch"));
            Assert.Equal("Invalid", response.Status);
            Assert.False(response.WasDispatched);
            Assert.Null(response.Run);
            Assert.Equal(0, fixture.ProviderAttempts);
        }
        finally
        {
            await File.WriteAllBytesAsync(artifactPath, original);
        }
    }

    internal static async Task Public_governed_invocation_honors_cancellation_before_graph_read()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync();
        await using var runtime = await fixture.CreateRuntimeAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => runtime.InvokeGovernedLoopAsync(
            fixture.Input("invoke-cancelled-graph-read", "cancel before graph read"),
            cancellation.Token));
        Assert.Equal(0, fixture.ProviderAttempts);
    }

    internal static async Task Public_governed_invocation_reports_host_unavailable_when_startup_cannot_acquire_execution_ownership()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync();
        await using var competingGate = new CustomLoopWorkspaceExecutionGate(fixture.Paths);
        var competing = competingGate.TryAcquire("competing-facade-host", new string('b', 64));
        Assert.Equal(CustomLoopExecutionLeaseStatus.Acquired, competing.Status);

        using (competing.Lease)
        {
            await using var runtime = await fixture.CreateRuntimeAsync();
            var response = await runtime.InvokeGovernedLoopAsync(
                fixture.Input("invoke-host-unavailable", "do not dispatch while another host owns the workspace"));

            Assert.Equal("WorkspaceHostUnavailable", response.Status);
            Assert.False(response.WasDispatched);
            Assert.Null(response.Run);
            Assert.Equal(0, fixture.ProviderAttempts);
        }
    }

    internal static async Task Public_scheduled_governed_invocation_maps_host_unavailable_after_preparing_the_exact_delivery()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(scheduleTrigger: true);
        var scheduledAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2).ToUniversalTime();
        var workerNow = scheduledAtUtc.AddMinutes(2);
        var scenario = ScheduleScenario.Create(fixture, scheduledAtUtc, "prepare the exact scheduled delivery while the host is unavailable");
        await QueueScheduleAsync(fixture.Paths, scenario, workerNow);

        var queue = new TriggerQueueStore(fixture.Paths, TriggerQueueQuota.Runtime, timeProvider: new FixedTriggerTimeProvider(workerNow));
        var snapshot = await queue.GetSnapshotAsync(workerNow);
        await using var competingGate = new CustomLoopWorkspaceExecutionGate(fixture.Paths);
        var competing = competingGate.TryAcquire("competing-scheduled-facade-host", new string('c', 64));
        Assert.Equal(CustomLoopExecutionLeaseStatus.Acquired, competing.Status);

        using (competing.Lease)
        {
            await using var runtime = await fixture.CreateRuntimeAsync();
            var worker = runtime.CreateTriggerWorkerRuntime(new ExactTriggerAuthorizer(), new FixedTriggerTimeProvider(workerNow));
            var result = await worker.RunOnceAsync(new TriggerWorkerSelectionInput(
                "scheduled-facade-host-unavailable",
                snapshot.Generation,
                workerNow,
                TimeSpan.FromSeconds(30),
                [],
                2));
            var entry = Assert.IsType<TriggerWorkerEntrySnapshot>(result.Entry);

            Assert.Equal("Acquired", result.SelectionStatus);
            Assert.Equal("NeedsReview", entry.State);
            Assert.Equal("NeedsReview", entry.DispatchOutcome);
            Assert.Contains("WorkspaceHostUnavailable", entry.DispatchDetail, StringComparison.Ordinal);
            Assert.False(entry.GovernedRunId is not null && fixture.ProviderAttempts > 0);
            Assert.Equal(0, fixture.ProviderAttempts);
        }
    }

    internal static async Task Public_scheduled_governed_invocation_rejects_a_trigger_role_that_does_not_own_the_graph()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(scheduleTrigger: true);
        var scheduledAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2).ToUniversalTime();
        var workerNow = scheduledAtUtc.AddMinutes(2);
        var scenario = ScheduleScenario.Create(fixture, scheduledAtUtc, "reject a trigger role that is not the graph owner")
            .WithActorRole("different-role");
        await QueueScheduleAsync(fixture.Paths, scenario, workerNow);

        var queue = new TriggerQueueStore(fixture.Paths, TriggerQueueQuota.Runtime, timeProvider: new FixedTriggerTimeProvider(workerNow));
        var snapshot = await queue.GetSnapshotAsync(workerNow);
        await using var runtime = await fixture.CreateRuntimeAsync();
        var worker = runtime.CreateTriggerWorkerRuntime(new ExactTriggerAuthorizer(), new FixedTriggerTimeProvider(workerNow));
        var result = await worker.RunOnceAsync(new TriggerWorkerSelectionInput(
            "scheduled-facade-wrong-role",
            snapshot.Generation,
            workerNow,
            TimeSpan.FromSeconds(30),
            [],
            2));
        var entry = Assert.IsType<TriggerWorkerEntrySnapshot>(result.Entry);

        Assert.Equal("Acquired", result.SelectionStatus);
        Assert.Equal("NeedsReview", entry.State);
        Assert.Equal("NeedsReview", entry.DispatchOutcome);
        Assert.Contains("graph owner role", entry.DispatchDetail, StringComparison.Ordinal);
        Assert.Equal(0, fixture.ProviderAttempts);
    }

    internal static async Task Public_governed_invocation_rejects_a_reused_operation_with_a_different_grant()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(includeRestrictedGrant: true);
        await using var runtime = await fixture.CreateRuntimeAsync();
        var input = fixture.Input("invoke-reused-grant", "bind the original exact grant");
        var first = await runtime.InvokeGovernedLoopAsync(input);
        Assert.Equal("Executed", first.Status);

        var changed = await runtime.InvokeGovernedLoopAsync(input with { AuthorityGrant = fixture.RestrictedGrant! });
        Assert.Equal("Conflict", changed.Status);
        Assert.False(changed.WasDispatched);
        Assert.Equal(1, fixture.ProviderAttempts);
    }

    internal static async Task Public_governed_invocation_fails_closed_when_the_receipt_artifact_is_corrupt()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync();
        await using var runtime = await fixture.CreateRuntimeAsync();
        var input = fixture.Input("invoke-corrupt-receipt", "retain the exact receipt boundary");
        var first = await runtime.InvokeGovernedLoopAsync(input);
        Assert.Equal("Executed", first.Status);

        var operationPath = Path.Combine(fixture.Paths.CustomLoopInvocationOperationsPath, input.OperationId + ".json");
        var original = await File.ReadAllBytesAsync(operationPath);
        try
        {
            await File.WriteAllTextAsync(operationPath, "{\"corrupt\":true}");
            var response = await runtime.InvokeGovernedLoopAsync(input);
            Assert.Equal("Unavailable", response.Status);
            Assert.False(response.WasDispatched);
            Assert.Null(response.Run);
            Assert.Equal(1, fixture.ProviderAttempts);
        }
        finally
        {
            await File.WriteAllBytesAsync(operationPath, original);
        }
    }

    internal static async Task Public_governed_invocation_maps_a_receipt_write_lock_failure_to_unavailable()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync();
        await using var runtime = await fixture.CreateRuntimeAsync();
        Directory.CreateDirectory(fixture.Paths.CustomLoopInvocationOperationsPath);
        var lockPath = Path.Combine(fixture.Paths.CustomLoopInvocationOperationsPath, ".custom-loop-mutations.lock");
        await using var mutationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.WriteThrough);

        var response = await runtime.InvokeGovernedLoopAsync(
            fixture.Input("invoke-receipt-lock-failure", "the receipt write lock must fail closed"));

        Assert.Equal("Unavailable", response.Status);
        Assert.False(response.WasDispatched);
        Assert.Null(response.Run);
        Assert.Equal(0, fixture.ProviderAttempts);
    }

    internal static async Task Public_governed_invocation_keeps_a_running_run_busy_when_its_durable_artifact_is_temporarily_unreadable()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(pauseProvider: true);
        await using var runtime = await fixture.CreateRuntimeAsync();
        var input = fixture.Input("invoke-corrupt-running-run", "preserve the running boundary");
        var invocation = runtime.InvokeGovernedLoopAsync(input);
        await fixture.WaitForProviderAsync();
        using var runStore = new CustomLoopRunStore(fixture.Paths);
        var running = await WaitForRunAsync(runStore, CustomLoopRunStatus.Running);
        var runPath = Path.Combine(fixture.Paths.CustomLoopRunsPath, running.LoopId, running.Id + ".json");
        var original = await File.ReadAllBytesAsync(runPath);
        try
        {
            await File.WriteAllTextAsync(runPath, "{\"corrupt\":true}");
            var response = await runtime.InvokeGovernedLoopAsync(input);
            Assert.Equal("OperationInProgress", response.Status);
            Assert.False(response.WasDispatched);
            Assert.Null(response.Run);
        }
        finally
        {
            await File.WriteAllBytesAsync(runPath, original);
            fixture.ReleaseProvider();
        }

        var completed = await invocation;
        Assert.Equal("Completed", completed.ExecutionStatus);
        Assert.Equal(1, fixture.ProviderAttempts);
    }

    internal static async Task Public_restarted_runtime_fails_closed_when_a_retained_running_run_is_unreadable()
    {
        using var fixture = await GovernedRuntimeFixture.CreateAsync(pauseProvider: true);
        await using var firstRuntime = await fixture.CreateRuntimeAsync();
        var input = fixture.Input("invoke-restarted-corrupt-running-run", "preserve the retained running boundary");
        var invocation = firstRuntime.InvokeGovernedLoopAsync(input);
        await fixture.WaitForProviderAsync();
        using var runStore = new CustomLoopRunStore(fixture.Paths);
        var running = await WaitForRunAsync(runStore, CustomLoopRunStatus.Running);
        var runPath = Path.Combine(fixture.Paths.CustomLoopRunsPath, running.LoopId, running.Id + ".json");
        var original = await File.ReadAllBytesAsync(runPath);
        try
        {
            await File.WriteAllTextAsync(runPath, "{\"corrupt\":true}");
            await using var restartedRuntime = await fixture.CreateRuntimeAsync();
            var response = await restartedRuntime.InvokeGovernedLoopAsync(input);

            Assert.Equal("WorkspaceHostUnavailable", response.Status);
            Assert.False(response.WasDispatched);
            Assert.Null(response.Run);
            Assert.Equal(1, fixture.ProviderAttempts);
        }
        finally
        {
            await File.WriteAllBytesAsync(runPath, original);
            fixture.ReleaseProvider();
        }

        var completed = await invocation;
        Assert.Equal("Completed", completed.ExecutionStatus);
        Assert.Equal(1, fixture.ProviderAttempts);
    }
}
