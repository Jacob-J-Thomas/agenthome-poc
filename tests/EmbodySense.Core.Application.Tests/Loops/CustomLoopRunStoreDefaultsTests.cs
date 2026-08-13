using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.TraceRetention.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.TraceRetention;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Application.Tests.Loops;

public sealed class CustomLoopRunStoreDefaultsTests
{
    private static readonly DateTimeOffset _timestamp = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Optional_defaults_are_conservative_and_cancellation_aware()
    {
        ICustomLoopRunStore store = new DefaultRunStore();
        var run = CreateRun();
        var request = new CustomLoopTraceDeletionRequest(run.Id, new string('a', CustomLoopLimits.Sha256HexCharacters), "delete-default", "test-user", "web");
        var mutation = new CustomLoopTraceDeletionMutation(request, CustomLoopTraceDeletionRequestHash.Compute(request), _timestamp);

        Assert.True(await store.HasSufficientTraceCapacityForDispatchAsync(run, run.LifecycleVersion));
        Assert.Equal(CustomLoopTraceQuota.Empty(), await store.GetTraceQuotaAsync());
        Assert.Null(await store.InspectTraceAsync(run.Id));
        Assert.Equal(CustomLoopTraceDeletionLookupStatus.NotFound, (await store.GetTraceDeletionOperationAsync(request.OperationId)).Status);
        Assert.Equal(CustomLoopTraceDeletionReservationStatus.DeletionOperationLimitExceeded, (await store.ReserveTraceDeletionOperationAsync(mutation)).Status);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.Unknown, (await store.CommitTraceDeletionAuditFailureAsync(mutation)).Status);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.NotFound, (await store.DeleteTerminalTraceAsync(mutation)).Status);
        Assert.Equal(CustomLoopTraceDeletionAuditMarkStatus.NotFound, await store.MarkTraceDeletionOutcomeAsync(request.OperationId, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted));
        Assert.Equal(CustomLoopRunStoreStatus.NotFound, (await store.AppendTerminalIntegrityWarningAsync(run.Id, run.LifecycleVersion, run.Events[0])).Status);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.HasSufficientTraceCapacityForDispatchAsync(run, run.LifecycleVersion, cancellation.Token));
    }

    [Fact]
    public async Task Default_monitor_projects_known_runs_without_inventing_deletion_or_artifact_evidence()
    {
        var run = CreateRun();
        ICustomLoopRunStore store = new DefaultRunStore { Run = run };

        var monitor = await store.GetMonitorAsync(run.Id);
        var missing = await store.GetMonitorAsync("missing-run");

        Assert.NotNull(monitor);
        Assert.Equal(run.Id, monitor.Summary.Id);
        Assert.Equal(run.LoopId, monitor.Summary.LoopId);
        Assert.Equal(run.LifecycleVersion, monitor.Summary.LifecycleVersion);
        Assert.Equal(run.Status, monitor.Summary.Status);
        Assert.False(monitor.Summary.IsDeleted);
        Assert.Equal(string.Empty, monitor.ArtifactHash);
        Assert.Null(missing);
    }

    [Fact]
    public async Task Default_page_supports_only_the_unfiltered_first_page()
    {
        var run = CreateRun();
        var expected = new CustomLoopRunSummary(
            run.Id,
            run.LoopId,
            run.AdmissionOperationId,
            run.AdmittedDefinition.DefinitionVersion,
            run.LifecycleVersion,
            run.Status,
            run.CreatedAtUtc,
            run.UpdatedAtUtc,
            run.CompletedAtUtc,
            run.Checkpoint.Iteration,
            run.Checkpoint.NextStepIndex,
            run.FailureCode,
            false);
        var implementation = new DefaultRunStore { Recent = [expected] };
        ICustomLoopRunStore store = implementation;

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.ListPageAsync(null!));
        await Assert.ThrowsAsync<NotSupportedException>(() => store.ListPageAsync(new CustomLoopRunPageRequest(5, LoopId: run.LoopId)));
        await Assert.ThrowsAsync<NotSupportedException>(() => store.ListPageAsync(new CustomLoopRunPageRequest(5, Cursor: "next")));
        var page = await store.ListPageAsync(new CustomLoopRunPageRequest(5));

        Assert.Equal(5, implementation.LastMaximumCount);
        Assert.Equal(expected, Assert.Single(page.Items));
        Assert.Null(page.ContinuationCursor);
    }

    private static CustomLoopRunRecord CreateRun()
    {
        var definition = CustomLoopDefinition.CreateSeed("loop-default", "default-role", "step-1", "create-default", _timestamp);
        var admitted = new CustomLoopRunEvent(1, "event-1", _timestamp, CustomLoopRunEventKind.Admitted, null, null, null, "Run admitted.", [], null, null, null, null, null, null, null, null, null, null);
        var run = new CustomLoopRunRecord(CustomLoopRunRecord.CurrentSchemaVersion, "run-default", definition.Id, 1, CustomLoopRunStatus.Admitted, _timestamp, _timestamp, null, "web", new CustomLoopModelSnapshot("openai", "gpt-5"), "invoke-default", "test-user", string.Empty, definition, "Initial prompt", null, CustomLoopContextSnapshot.CreateEmpty(_timestamp), CustomLoopExecutionClock.NotStarted(), CustomLoopRunCheckpoint.Start(), [admitted], null, null, null)
        {
            CapabilityAdmission = TestCapabilityAdmissionFactory.Create(definition.CapabilityRequirements, _timestamp)
        };
        return CustomLoopAdmissionRequestHash.Apply(run);
    }

    private sealed class DefaultRunStore : ICustomLoopRunStore
    {
        public CustomLoopRunRecord? Run { get; init; }

        public IReadOnlyList<CustomLoopRunSummary> Recent { get; init; } = [];

        public int? LastMaximumCount { get; private set; }

        public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(Run?.Id, runId, StringComparison.Ordinal) ? Run : null);

        public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default)
        {
            LastMaximumCount = maximumCount;
            return Task.FromResult(Recent);
        }

        public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
