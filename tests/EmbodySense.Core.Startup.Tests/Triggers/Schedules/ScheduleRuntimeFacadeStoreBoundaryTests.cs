using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Triggers.Schedules;
using EmbodySense.Core.Startup.Triggers.Schedules.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Triggers.Schedules;

public sealed class ScheduleRuntimeFacadeStoreBoundaryTests
{
    private static readonly DateTimeOffset _now = ScheduleCurrentEvidenceTestContext.GrantEvaluatedAtUtc.AddMilliseconds(25);

    [Fact]
    public async Task Public_read_rejects_null_invalid_and_malformed_results_and_classifies_store_failures()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();

        var nullResult = await ReadAsync(
            workspace,
            context,
            (_, _) => Task.FromResult<ScheduleStoreReadResult>(null!));
        var invalidStatus = await ReadAsync(
            workspace,
            context,
            (_, _) => Task.FromResult(new ScheduleStoreReadResult((ScheduleStoreReadStatus)int.MaxValue, null, null)));
        var malformedFound = await ReadAsync(
            workspace,
            context,
            (_, _) => Task.FromResult(new ScheduleStoreReadResult(ScheduleStoreReadStatus.Found, null, null)));
        var malformedNotFound = await ReadAsync(
            workspace,
            context,
            (_, _) => Task.FromResult(new ScheduleStoreReadResult(ScheduleStoreReadStatus.NotFound, context.Definition, null)));
        var corruptFailure = await ReadAsync(
            workspace,
            context,
            (_, _) => Task.FromException<ScheduleStoreReadResult>(new FormatException("corrupt store evidence")));
        var unavailableFailure = await ReadAsync(
            workspace,
            context,
            (_, _) => Task.FromException<ScheduleStoreReadResult>(new IOException("store unavailable")));

        Assert.All([nullResult, invalidStatus, malformedFound, malformedNotFound, corruptFailure], result =>
        {
            Assert.Equal(ScheduleStoreReadStatus.Corrupt, result.Status);
            Assert.Null(result.Definition);
            Assert.Null(result.State);
        });
        Assert.Equal(ScheduleStoreReadStatus.Unavailable, unavailableFailure.Status);
        Assert.Null(unavailableFailure.Definition);
        Assert.Null(unavailableFailure.State);
    }

    [Fact]
    public async Task Public_read_rejects_a_valid_snapshot_for_another_requested_schedule()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var store = new ScriptedScheduleStore
        {
            CreateBehavior = (request, _) => Task.FromResult(new ScheduleStoreMutationResult(
                ScheduleStoreMutationStatus.Applied,
                request.InitialState)),
        };
        using var runtime = CreateRuntime(workspace, context, store);
        var created = await runtime.CreateAsync(context.Definition);
        store.ReadBehavior = (_, _) => Task.FromResult(new ScheduleStoreReadResult(
            ScheduleStoreReadStatus.Found,
            context.Definition,
            created.CurrentState));
        Assert.True(ScheduleId.TryParse("different-schedule", out var differentSchedule));

        var result = await runtime.ReadAsync(differentSchedule!);

        Assert.Equal(ScheduleStoreReadStatus.Corrupt, result.Status);
        Assert.Null(result.Definition);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task Create_requires_an_exact_nonnull_applied_state_and_classifies_store_failures()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();

        var nullResult = await CreateAsync(
            workspace,
            context,
            (_, _) => Task.FromResult<ScheduleStoreMutationResult>(null!));
        var invalidStatus = await CreateAsync(
            workspace,
            context,
            (_, _) => Task.FromResult(new ScheduleStoreMutationResult((ScheduleStoreMutationStatus)int.MaxValue, null)));
        var missingAppliedState = await CreateAsync(
            workspace,
            context,
            (_, _) => Task.FromResult(new ScheduleStoreMutationResult(ScheduleStoreMutationStatus.Applied, null)));
        var substitutedAppliedState = await CreateAsync(
            workspace,
            context,
            (request, _) => Task.FromResult(new ScheduleStoreMutationResult(
                ScheduleStoreMutationStatus.Applied,
                request.InitialState with { StateRevision = request.InitialState.StateRevision + 1 })));
        var corruptFailure = await CreateAsync(
            workspace,
            context,
            (_, _) => Task.FromException<ScheduleStoreMutationResult>(new InvalidDataException("corrupt store evidence")));
        var unavailableFailure = await CreateAsync(
            workspace,
            context,
            (_, _) => Task.FromException<ScheduleStoreMutationResult>(new IOException("store unavailable")));
        var exactApplied = await CreateAsync(
            workspace,
            context,
            (request, _) => Task.FromResult(new ScheduleStoreMutationResult(
                ScheduleStoreMutationStatus.Applied,
                request.InitialState)));
        var invalidCreateReplay = await CreateAsync(
            workspace,
            context,
            (request, _) => Task.FromResult(new ScheduleStoreMutationResult(
                ScheduleStoreMutationStatus.Applied,
                request.InitialState)
            { ExactReplay = true }));

        Assert.All([nullResult, invalidStatus, missingAppliedState, substitutedAppliedState, corruptFailure], result =>
        {
            Assert.Equal(ScheduleRuntimeCreateStatus.Corrupt, result.Status);
            Assert.Null(result.CurrentState);
        });
        Assert.Equal(ScheduleRuntimeCreateStatus.Unavailable, unavailableFailure.Status);
        Assert.Null(unavailableFailure.CurrentState);
        Assert.Equal(ScheduleRuntimeCreateStatus.Created, exactApplied.Status);
        Assert.NotNull(exactApplied.CurrentState);
        Assert.Equal(ScheduleRuntimeCreateStatus.Corrupt, invalidCreateReplay.Status);
        Assert.Null(invalidCreateReplay.CurrentState);
    }

    [Fact]
    public async Task Create_reconciles_a_conflicting_store_create_with_current_truth()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        ScheduleState? racedState = null;
        var readCall = 0;
        var reconciledStore = new ScriptedScheduleStore
        {
            ReadBehavior = (_, _) => Task.FromResult(++readCall == 1
                ? new ScheduleStoreReadResult(ScheduleStoreReadStatus.NotFound, null, null)
                : new ScheduleStoreReadResult(ScheduleStoreReadStatus.Found, context.Definition, racedState)),
            CreateBehavior = (request, _) =>
            {
                racedState = request.InitialState;
                return Task.FromResult(new ScheduleStoreMutationResult(ScheduleStoreMutationStatus.Conflict, null));
            },
        };
        using var reconciledRuntime = CreateRuntime(workspace, context, reconciledStore);

        var reconciled = await reconciledRuntime.CreateAsync(context.Definition);

        var missingStore = new ScriptedScheduleStore
        {
            CreateBehavior = (_, _) => Task.FromResult(new ScheduleStoreMutationResult(ScheduleStoreMutationStatus.Conflict, null)),
        };
        using var missingRuntime = CreateRuntime(workspace, context, missingStore);
        var missing = await missingRuntime.CreateAsync(context.Definition);

        Assert.Equal(ScheduleRuntimeCreateStatus.AlreadyExists, reconciled.Status);
        Assert.Equal(racedState, reconciled.CurrentState);
        Assert.Equal(2, reconciledStore.ReadCallCount);
        Assert.Equal(ScheduleRuntimeCreateStatus.Corrupt, missing.Status);
        Assert.Null(missing.CurrentState);
        Assert.Equal(2, missingStore.ReadCallCount);
    }

    [Theory]
    [InlineData(ScheduleStoreReadStatus.Unavailable, ScheduleRuntimeCreateStatus.Unavailable)]
    [InlineData(ScheduleStoreReadStatus.Backpressured, ScheduleRuntimeCreateStatus.Backpressured)]
    [InlineData((ScheduleStoreReadStatus)int.MaxValue, ScheduleRuntimeCreateStatus.Corrupt)]
    public async Task Create_maps_closed_initial_read_statuses(
        ScheduleStoreReadStatus readStatus,
        ScheduleRuntimeCreateStatus expectedStatus)
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var store = new ScriptedScheduleStore
        {
            ReadBehavior = (_, _) => Task.FromResult(new ScheduleStoreReadResult(readStatus, null, null)),
        };
        using var runtime = CreateRuntime(workspace, context, store);

        var result = await runtime.CreateAsync(context.Definition);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.CurrentState);
    }

    [Fact]
    public async Task Set_enabled_same_state_uses_current_store_truth_and_rejects_invalid_expected_state()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var store = new ScriptedScheduleStore
        {
            CreateBehavior = (request, _) => Task.FromResult(new ScheduleStoreMutationResult(
                ScheduleStoreMutationStatus.Applied,
                request.InitialState)),
        };
        using var runtime = CreateRuntime(workspace, context, store);
        var created = await runtime.CreateAsync(context.Definition);
        var expected = Assert.IsType<ScheduleState>(created.CurrentState);

        var invalidExpected = await runtime.SetEnabledAsync(expected with { StateRevision = 0 }, true);
        store.ReadBehavior = (_, _) => Task.FromResult(new ScheduleStoreReadResult(ScheduleStoreReadStatus.NotFound, null, null));
        var missing = await runtime.SetEnabledAsync(expected, true);
        store.ReadBehavior = (_, _) => Task.FromResult(new ScheduleStoreReadResult(ScheduleStoreReadStatus.Unavailable, null, null));
        var unavailable = await runtime.SetEnabledAsync(expected, true);
        store.ReadBehavior = (_, _) => Task.FromResult(new ScheduleStoreReadResult(ScheduleStoreReadStatus.Backpressured, null, null));
        var backpressured = await runtime.SetEnabledAsync(expected, true);
        store.ReadBehavior = (_, _) => Task.FromResult(new ScheduleStoreReadResult((ScheduleStoreReadStatus)int.MaxValue, null, null));
        var invalidStatus = await runtime.SetEnabledAsync(expected, true);
        store.ReadBehavior = (_, _) => Task.FromResult(new ScheduleStoreReadResult(
            ScheduleStoreReadStatus.Found,
            context.Definition,
            expected with { DefinitionHash = new string('f', 64) }));
        var malformed = await runtime.SetEnabledAsync(expected, true);
        store.ReadBehavior = (_, _) => Task.FromResult(new ScheduleStoreReadResult(
            ScheduleStoreReadStatus.Found,
            context.Definition,
            expected with { StateRevision = expected.StateRevision + 1 }));
        var advanced = await runtime.SetEnabledAsync(expected, true);
        store.ReadBehavior = (_, _) => Task.FromResult(new ScheduleStoreReadResult(
            ScheduleStoreReadStatus.Found,
            context.Definition,
            expected));
        var exact = await runtime.SetEnabledAsync(expected, true);

        Assert.Equal(ScheduleStoreMutationStatus.Corrupt, invalidExpected.Status);
        Assert.Equal(ScheduleStoreMutationStatus.Conflict, missing.Status);
        Assert.Equal(ScheduleStoreMutationStatus.Unavailable, unavailable.Status);
        Assert.Equal(ScheduleStoreMutationStatus.Backpressured, backpressured.Status);
        Assert.Equal(ScheduleStoreMutationStatus.Corrupt, invalidStatus.Status);
        Assert.Equal(ScheduleStoreMutationStatus.Corrupt, malformed.Status);
        Assert.Equal(ScheduleStoreMutationStatus.Conflict, advanced.Status);
        Assert.Equal(ScheduleStoreMutationStatus.AlreadyExists, exact.Status);
        Assert.Equal(expected, exact.CurrentState);
    }

    [Fact]
    public async Task Compare_exchange_requires_an_exact_applied_replacement_and_rejects_malformed_current_state()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var store = new ScriptedScheduleStore
        {
            CreateBehavior = (request, _) => Task.FromResult(new ScheduleStoreMutationResult(
                ScheduleStoreMutationStatus.Applied,
                request.InitialState)),
        };
        using var runtime = CreateRuntime(workspace, context, store);
        var created = await runtime.CreateAsync(context.Definition);
        Assert.Equal(ScheduleRuntimeCreateStatus.Created, created.Status);

        store.CompareExchangeBehavior = (_, _) => Task.FromResult<ScheduleStoreMutationResult>(null!);
        var nullResult = await runtime.SetEnabledAsync(created.CurrentState!, false);
        store.CompareExchangeBehavior = (_, _) => Task.FromResult(new ScheduleStoreMutationResult(
            (ScheduleStoreMutationStatus)int.MaxValue,
            null));
        var invalidStatus = await runtime.SetEnabledAsync(created.CurrentState!, false);
        store.CompareExchangeBehavior = (_, _) => Task.FromResult(new ScheduleStoreMutationResult(
            ScheduleStoreMutationStatus.Applied,
            null));
        var missingAppliedState = await runtime.SetEnabledAsync(created.CurrentState!, false);
        store.CompareExchangeBehavior = (request, _) => Task.FromResult(new ScheduleStoreMutationResult(
            ScheduleStoreMutationStatus.Applied,
            request.Expected));
        var substitutedAppliedState = await runtime.SetEnabledAsync(created.CurrentState!, false);
        store.CompareExchangeBehavior = (request, _) => Task.FromResult(new ScheduleStoreMutationResult(
            ScheduleStoreMutationStatus.Conflict,
            request.Expected with { DefinitionHash = new string('f', 64) }));
        var malformedConflict = await runtime.SetEnabledAsync(created.CurrentState!, false);
        store.CompareExchangeBehavior = (_, _) => Task.FromException<ScheduleStoreMutationResult>(
            new FormatException("corrupt store evidence"));
        var corruptFailure = await runtime.SetEnabledAsync(created.CurrentState!, false);
        store.CompareExchangeBehavior = (_, _) => Task.FromException<ScheduleStoreMutationResult>(
            new IOException("store unavailable"));
        var unavailableFailure = await runtime.SetEnabledAsync(created.CurrentState!, false);
        store.CompareExchangeBehavior = (request, _) => Task.FromResult(new ScheduleStoreMutationResult(
            ScheduleStoreMutationStatus.Applied,
            request.Replacement));
        var exactApplied = await runtime.SetEnabledAsync(created.CurrentState!, false);
        store.CompareExchangeBehavior = (request, _) => Task.FromResult(new ScheduleStoreMutationResult(
            ScheduleStoreMutationStatus.Applied,
            request.Replacement)
        { ExactReplay = true });
        var exactReplay = await runtime.SetEnabledAsync(created.CurrentState!, false);

        Assert.All(
            [nullResult, invalidStatus, missingAppliedState, substitutedAppliedState, malformedConflict, corruptFailure],
            result => Assert.Equal(ScheduleStoreMutationStatus.Corrupt, result.Status));
        Assert.Equal(ScheduleStoreMutationStatus.Unavailable, unavailableFailure.Status);
        Assert.Equal(ScheduleStoreMutationStatus.Applied, exactApplied.Status);
        Assert.Equal(ScheduleStoreMutationStatus.Applied, exactReplay.Status);
        Assert.True(exactReplay.ExactReplay);
        Assert.Equal(2, exactApplied.CurrentState!.StateRevision);
        Assert.False(exactApplied.CurrentState.Enabled);
    }

    [Fact]
    public async Task Evaluation_cannot_treat_an_applied_compare_exchange_without_persisted_state_as_success()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var store = new ScriptedScheduleStore
        {
            CreateBehavior = (request, _) => Task.FromResult(new ScheduleStoreMutationResult(
                ScheduleStoreMutationStatus.Applied,
                request.InitialState)),
        };
        using var runtime = CreateRuntime(workspace, context, store);
        var created = await runtime.CreateAsync(context.Definition);
        store.ReadBehavior = (_, _) => Task.FromResult(new ScheduleStoreReadResult(
            ScheduleStoreReadStatus.Found,
            context.Definition,
            created.CurrentState));
        store.CompareExchangeBehavior = (_, _) => Task.FromResult(new ScheduleStoreMutationResult(
            ScheduleStoreMutationStatus.Applied,
            null));

        var result = await runtime.EvaluateOnceAsync(context.Definition.ScheduleId);

        Assert.Equal(ScheduleEvaluationStatus.Corrupt, result.Status);
        Assert.Equal("schedule-store-corrupt", result.ReasonCode);
        Assert.Equal(created.CurrentState, result.State);
        Assert.Equal(0, context.PayloadReadCount);
    }

    [Fact]
    public async Task Store_boundary_propagates_cancellation_during_read_create_and_compare_exchange()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();

        using var readCancellation = new CancellationTokenSource();
        var readStore = new ScriptedScheduleStore
        {
            ReadBehavior = (_, token) =>
            {
                readCancellation.Cancel();
                return Task.FromCanceled<ScheduleStoreReadResult>(token);
            },
        };
        using var readRuntime = CreateRuntime(workspace, context, readStore);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readRuntime.ReadAsync(
            context.Definition.ScheduleId,
            readCancellation.Token));

        using var createCancellation = new CancellationTokenSource();
        var createStore = new ScriptedScheduleStore
        {
            CreateBehavior = (_, token) =>
            {
                createCancellation.Cancel();
                return Task.FromCanceled<ScheduleStoreMutationResult>(token);
            },
        };
        using var createRuntime = CreateRuntime(workspace, context, createStore);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => createRuntime.CreateAsync(
            context.Definition,
            createCancellation.Token));

        using var exchangeCancellation = new CancellationTokenSource();
        var exchangeStore = new ScriptedScheduleStore
        {
            CreateBehavior = (request, _) => Task.FromResult(new ScheduleStoreMutationResult(
                ScheduleStoreMutationStatus.Applied,
                request.InitialState)),
            CompareExchangeBehavior = (_, token) =>
            {
                exchangeCancellation.Cancel();
                return Task.FromCanceled<ScheduleStoreMutationResult>(token);
            },
        };
        using var exchangeRuntime = CreateRuntime(workspace, context, exchangeStore);
        var created = await exchangeRuntime.CreateAsync(context.Definition);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchangeRuntime.SetEnabledAsync(
            created.CurrentState!,
            false,
            exchangeCancellation.Token));
    }

    private static async Task<ScheduleStoreReadResult> ReadAsync(
        TestWorkspace workspace,
        ScheduleCurrentEvidenceTestContext context,
        Func<ScheduleId, CancellationToken, Task<ScheduleStoreReadResult>> behavior)
    {
        var store = new ScriptedScheduleStore { ReadBehavior = behavior };
        using var runtime = CreateRuntime(workspace, context, store);
        return await runtime.ReadAsync(context.Definition.ScheduleId);
    }

    private static async Task<ScheduleRuntimeCreateResult> CreateAsync(
        TestWorkspace workspace,
        ScheduleCurrentEvidenceTestContext context,
        Func<ScheduleStoreCreateRequest, CancellationToken, Task<ScheduleStoreMutationResult>> behavior)
    {
        var store = new ScriptedScheduleStore { CreateBehavior = behavior };
        using var runtime = CreateRuntime(workspace, context, store);
        return await runtime.CreateAsync(context.Definition);
    }

    private static ScheduleRuntimeFacade CreateRuntime(
        TestWorkspace workspace,
        ScheduleCurrentEvidenceTestContext context,
        ScriptedScheduleStore store)
        => ScheduleRuntimeFactory.Create(
            new WorkspacePaths(workspace.RootPath),
            store,
            context.AdapterUnderTest(),
            new UnusedScheduleOverlap(),
            new ScheduleBoundaryTimeZone(),
            new MutableScheduleTimeProvider(_now));
}
