using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.TraceRetention;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Tests.Loops;

public sealed class CustomLoopRunStoreDefaultsTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Optional_defaults_are_conservative_and_cancellation_aware()
    {
        ICustomLoopRunStore store = new DefaultRunStore();
        var run = CreateRun();
        var request = new CustomLoopTraceDeletionRequest(run.Id, new string('a', CustomLoopLimits.Sha256HexCharacters), "delete-default", "test-user", "web");
        var mutation = new CustomLoopTraceDeletionMutation(request, CustomLoopTraceDeletionRequestHash.Compute(request), Timestamp);

        Assert.True(await store.HasSufficientTraceCapacityForDispatchAsync(run, run.LifecycleVersion));
        Assert.Equal(CustomLoopTraceQuota.Empty(), await store.GetTraceQuotaAsync());
        Assert.Null(await store.InspectTraceAsync(run.Id));
        Assert.Equal(CustomLoopTraceDeletionLookupStatus.NotFound, (await store.GetTraceDeletionOperationAsync(request.OperationId)).Status);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.NotFound, (await store.DeleteTerminalTraceAsync(mutation)).Status);
        Assert.Equal(CustomLoopTraceDeletionAuditMarkStatus.NotFound, await store.MarkTraceDeletionOutcomeAsync(request.OperationId, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted));
        Assert.Equal(CustomLoopRunStoreStatus.NotFound, (await store.AppendTerminalIntegrityWarningAsync(run.Id, run.LifecycleVersion, run.Events[0])).Status);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.HasSufficientTraceCapacityForDispatchAsync(run, run.LifecycleVersion, cancellation.Token));
    }

    private static CustomLoopRunRecord CreateRun()
    {
        var definition = CustomLoopDefinition.CreateSeed("loop-default", "default-role", "step-1", "create-default", Timestamp);
        var admitted = new CustomLoopRunEvent(1, "event-1", Timestamp, CustomLoopRunEventKind.Admitted, null, null, null, "Run admitted.", [], null, null, null, null, null, null, null, null, null, null);
        var run = new CustomLoopRunRecord(CustomLoopRunRecord.CurrentSchemaVersion, "run-default", definition.Id, 1, CustomLoopRunStatus.Admitted, Timestamp, Timestamp, null, "web", new CustomLoopModelSnapshot("openai", "gpt-5"), "invoke-default", "test-user", string.Empty, definition, "Initial prompt", null, CustomLoopContextSnapshot.CreateEmpty(Timestamp), CustomLoopExecutionClock.NotStarted(), CustomLoopRunCheckpoint.Start(), [admitted], null, null, null);
        return CustomLoopAdmissionRequestHash.Apply(run);
    }

    private sealed class DefaultRunStore : ICustomLoopRunStore
    {
        public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
