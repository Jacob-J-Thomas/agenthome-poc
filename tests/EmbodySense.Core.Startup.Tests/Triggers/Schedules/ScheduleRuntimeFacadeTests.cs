using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Triggers;
using EmbodySense.Core.Startup.Triggers.Schedules;
using EmbodySense.Core.Startup.Triggers.Schedules.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Triggers.Schedules;

public sealed class ScheduleRuntimeFacadeTests
{
    private static readonly DateTimeOffset _now = ScheduleCurrentEvidenceTestContext.GrantEvaluatedAtUtc.AddMilliseconds(25);

    [Fact]
    public async Task Create_derives_revision_one_from_retained_time_zone_evidence_without_admitting_work()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var timeZone = new RuntimeTimeZone();
        using var runtime = CreateRuntime(workspace, context, timeZone);

        var created = await runtime.CreateAsync(context.Definition);
        var persisted = await runtime.ReadAsync(context.Definition.ScheduleId);
        var queue = await new TriggerQueueStore(new WorkspacePaths(workspace.RootPath), TriggerQueueQuota.Runtime).GetSnapshotAsync(_now);

        Assert.Equal(ScheduleRuntimeCreateStatus.Created, created.Status);
        Assert.NotNull(created.CurrentState);
        Assert.Equal(1, created.CurrentState.StateRevision);
        Assert.Equal(context.Definition.Enabled, created.CurrentState.Enabled);
        Assert.Equal(context.Definition.Recurrence.FirstLocalOccurrence, created.CurrentState.NextOccurrence!.ScheduledLocal);
        Assert.Equal(context.Occurrence.ScheduledAtUtc, created.CurrentState.NextOccurrence.ScheduledAtUtc);
        Assert.Equal(context.Definition.TimeZone.RulesFingerprint, created.CurrentState.NextOccurrence.TimeZone.RulesFingerprint);
        Assert.Equal(ScheduleStoreReadStatus.Found, persisted.Status);
        Assert.Equal(created.CurrentState, persisted.State);
        Assert.Equal(1, timeZone.LocalCalls);
        Assert.Equal(0, timeZone.InstantCalls);
        Assert.Equal(0, context.PayloadReadCount);
        Assert.Empty(queue.Entries);
    }

    [Fact]
    public async Task Create_persists_the_trusted_clock_watermark_and_first_evaluation_detects_rollback()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var clock = new MutableScheduleTimeProvider(_now);
        using var runtime = ScheduleRuntimeFactory.Create(
            new WorkspacePaths(workspace.RootPath),
            context.AdapterUnderTest(clock),
            new ClearOverlap(),
            new RuntimeTimeZone(),
            clock);

        var created = await runtime.CreateAsync(context.Definition);
        clock.UtcNow = _now.AddTicks(-1);
        var evaluated = await runtime.EvaluateOnceAsync(context.Definition.ScheduleId);

        Assert.Equal(ScheduleRuntimeCreateStatus.Created, created.Status);
        Assert.Equal(_now, created.CurrentState!.LastClockObservedAtUtc);
        Assert.Equal(ScheduleEvaluationStatus.ClockRollback, evaluated.Status);
        Assert.Equal(created.CurrentState, evaluated.State);
        Assert.Equal(0, context.PayloadReadCount);
    }

    [Fact]
    public async Task Create_is_idempotent_for_the_exact_definition_and_conflicts_with_an_immutable_identity_reuse()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var timeZone = new RuntimeTimeZone();
        using var runtime = CreateRuntime(workspace, context, timeZone);

        var created = await runtime.CreateAsync(context.Definition);
        var replayed = await runtime.CreateAsync(context.Definition);
        var conflict = await runtime.CreateAsync(context.Definition with { SurfaceId = "different-scheduler" });

        Assert.Equal(ScheduleRuntimeCreateStatus.Created, created.Status);
        Assert.Equal(ScheduleRuntimeCreateStatus.AlreadyExists, replayed.Status);
        Assert.Equal(created.CurrentState, replayed.CurrentState);
        Assert.Equal(ScheduleRuntimeCreateStatus.Conflict, conflict.Status);
        Assert.Equal(created.CurrentState, conflict.CurrentState);
        Assert.Equal(1, timeZone.LocalCalls);
        Assert.Equal(0, context.PayloadReadCount);
    }

    [Fact]
    public async Task Enablement_is_optimistic_and_disabled_evaluation_never_resolves_authority_or_queues()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        using var runtime = CreateRuntime(workspace, context, new RuntimeTimeZone());
        var created = await runtime.CreateAsync(context.Definition);

        var unchanged = await runtime.SetEnabledAsync(created.CurrentState!, true);
        var disabled = await runtime.SetEnabledAsync(created.CurrentState!, false);
        var stale = await runtime.SetEnabledAsync(created.CurrentState!, true);
        var evaluated = await runtime.EvaluateOnceAsync(context.Definition.ScheduleId);
        var queue = await new TriggerQueueStore(new WorkspacePaths(workspace.RootPath), TriggerQueueQuota.Runtime).GetSnapshotAsync(_now);

        Assert.Equal(ScheduleStoreMutationStatus.AlreadyExists, unchanged.Status);
        Assert.Equal(created.CurrentState, unchanged.CurrentState);
        Assert.Equal(ScheduleStoreMutationStatus.Applied, disabled.Status);
        Assert.False(disabled.CurrentState!.Enabled);
        Assert.Equal(2, disabled.CurrentState.StateRevision);
        Assert.Equal(ScheduleStoreMutationStatus.Conflict, stale.Status);
        Assert.Equal(disabled.CurrentState, stale.CurrentState);
        Assert.Equal(ScheduleEvaluationStatus.Disabled, evaluated.Status);
        Assert.Equal(0, context.PayloadReadCount);
        Assert.Empty(queue.Entries);
    }

    [Fact]
    public async Task Disable_and_reenable_retain_occurrence_history_and_clock_watermark_before_later_evaluation()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var clock = new MutableScheduleTimeProvider(_now);
        using var runtime = ScheduleRuntimeFactory.Create(
            new WorkspacePaths(workspace.RootPath),
            context.AdapterUnderTest(clock),
            new ClearOverlap(),
            new RuntimeTimeZone(),
            clock);
        var created = await runtime.CreateAsync(context.Definition);

        var disabled = await runtime.SetEnabledAsync(created.CurrentState!, false);
        var reenabled = await runtime.SetEnabledAsync(disabled.CurrentState!, true);
        clock.UtcNow = _now.AddTicks(-1);
        var rollback = await runtime.EvaluateOnceAsync(context.Definition.ScheduleId);
        clock.UtcNow = _now.AddTicks(1);
        var evaluated = await runtime.EvaluateOnceAsync(context.Definition.ScheduleId);

        Assert.Equal(ScheduleStoreMutationStatus.Applied, disabled.Status);
        Assert.Equal(ScheduleStoreMutationStatus.Applied, reenabled.Status);
        Assert.Equal(created.CurrentState!.NextOccurrence, disabled.CurrentState!.NextOccurrence);
        Assert.Equal(created.CurrentState.NextOccurrence, reenabled.CurrentState!.NextOccurrence);
        Assert.Equal(created.CurrentState.DispositionEvidence, reenabled.CurrentState.DispositionEvidence);
        Assert.Equal(created.CurrentState.TerminalDeliveryEvidence, reenabled.CurrentState.TerminalDeliveryEvidence);
        Assert.Equal(_now, reenabled.CurrentState.LastClockObservedAtUtc);
        Assert.Equal(ScheduleEvaluationStatus.ClockRollback, rollback.Status);
        Assert.Equal(reenabled.CurrentState, rollback.State);
        Assert.Equal(ScheduleEvaluationStatus.Queued, evaluated.Status);
        Assert.Single(evaluated.State!.TerminalDeliveryEvidence);
        Assert.Equal(2, evaluated.State.NextOccurrence!.Ordinal);
    }

    [Theory]
    [InlineData(ScheduleAmbiguousLocalTimePolicy.EarlierUtc, 7)]
    [InlineData(ScheduleAmbiguousLocalTimePolicy.LaterUtc, 8)]
    public async Task Create_applies_the_explicit_fold_policy(
        ScheduleAmbiguousLocalTimePolicy policy,
        int expectedUtcHour)
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var definition = context.Definition with
        {
            DaylightSaving = context.Definition.DaylightSaving with { AmbiguousLocalTime = policy },
        };
        var timeZone = new RuntimeTimeZone
        {
            LocalResolver = (zone, local) => new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.AmbiguousLocalTime,
                zone.RulesFingerprint,
                local,
                new DateTimeOffset(2026, 11, 1, 7, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero)),
        };
        using var runtime = CreateRuntime(workspace, context, timeZone);

        var result = await runtime.CreateAsync(definition);

        Assert.Equal(ScheduleRuntimeCreateStatus.Created, result.Status);
        Assert.Equal(expectedUtcHour, result.CurrentState!.NextOccurrence!.ScheduledAtUtc.Hour);
        Assert.Equal(definition.TimeZone.RulesFingerprint, result.CurrentState.NextOccurrence.TimeZone.RulesFingerprint);
    }

    [Fact]
    public async Task Create_shifts_a_gap_to_the_provider_proven_first_valid_instant()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var firstLocal = new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified);
        var definition = WithRecurrence(context.Definition, ScheduleRecurrenceKind.Daily, firstLocal);
        var firstValidLocal = new DateTime(2026, 3, 8, 3, 0, 0, DateTimeKind.Unspecified);
        var firstValidUtc = new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.Zero);
        var timeZone = new RuntimeTimeZone
        {
            LocalResolver = (zone, _) => new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.InvalidLocalTime,
                zone.RulesFingerprint,
                firstValidLocal,
                firstValidUtc,
                null),
        };
        using var runtime = CreateRuntime(workspace, context, timeZone);

        var result = await runtime.CreateAsync(definition);

        Assert.Equal(ScheduleRuntimeCreateStatus.Created, result.Status);
        Assert.Equal(firstLocal, result.CurrentState!.NextOccurrence!.ScheduledLocal);
        Assert.Equal(firstValidUtc, result.CurrentState.NextOccurrence.ScheduledAtUtc);
        Assert.Empty(result.CurrentState.DispositionEvidence);
    }

    [Theory]
    [InlineData(ScheduleTimeZoneResolutionStatus.Unavailable, ScheduleRuntimeCreateStatus.Unavailable)]
    [InlineData(ScheduleTimeZoneResolutionStatus.Backpressured, ScheduleRuntimeCreateStatus.Backpressured)]
    [InlineData(ScheduleTimeZoneResolutionStatus.Corrupt, ScheduleRuntimeCreateStatus.Corrupt)]
    [InlineData(ScheduleTimeZoneResolutionStatus.Unknown, ScheduleRuntimeCreateStatus.Corrupt)]
    public async Task Create_preserves_closed_local_resolution_failures(
        ScheduleTimeZoneResolutionStatus status,
        ScheduleRuntimeCreateStatus expected)
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var timeZone = new RuntimeTimeZone
        {
            LocalResolver = (_, _) => new ScheduleTimeZoneResolution(status, null, default, null, null),
        };
        using var runtime = CreateRuntime(workspace, context, timeZone);

        var result = await runtime.CreateAsync(context.Definition);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.CurrentState);
    }

    [Fact]
    public async Task Create_maps_time_zone_exceptions_and_clock_failures_without_persisting_state()
    {
        using var timeZoneWorkspace = new TestWorkspace();
        var timeZoneContext = ScheduleCurrentEvidenceTestContext.Create();
        var throwingTimeZone = new RuntimeTimeZone
        {
            LocalResolver = (_, _) => throw new IOException("time-zone unavailable"),
        };
        using var timeZoneRuntime = CreateRuntime(timeZoneWorkspace, timeZoneContext, throwingTimeZone);
        Assert.Equal(
            ScheduleRuntimeCreateStatus.Unavailable,
            (await timeZoneRuntime.CreateAsync(timeZoneContext.Definition)).Status);

        using var clockWorkspace = new TestWorkspace();
        var clockContext = ScheduleCurrentEvidenceTestContext.Create();
        using var clockRuntime = ScheduleRuntimeFactory.Create(
            new WorkspacePaths(clockWorkspace.RootPath),
            clockContext.AdapterUnderTest(),
            new ClearOverlap(),
            new RuntimeTimeZone(),
            new ThrowingTimeProvider());
        Assert.Equal(
            ScheduleRuntimeCreateStatus.Unavailable,
            (await clockRuntime.CreateAsync(clockContext.Definition)).Status);

        using var offsetWorkspace = new TestWorkspace();
        var offsetContext = ScheduleCurrentEvidenceTestContext.Create();
        using var offsetRuntime = ScheduleRuntimeFactory.Create(
            new WorkspacePaths(offsetWorkspace.RootPath),
            offsetContext.AdapterUnderTest(),
            new ClearOverlap(),
            new RuntimeTimeZone(),
            new FixedTimeProvider(_now.ToOffset(TimeSpan.FromHours(-5))));
        Assert.Equal(
            ScheduleRuntimeCreateStatus.Corrupt,
            (await offsetRuntime.CreateAsync(offsetContext.Definition)).Status);
    }

    [Fact]
    public async Task Once_gap_skip_creates_an_exhausted_schedule_with_immutable_skip_evidence()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var firstLocal = new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified);
        var definition = WithRecurrence(
            context.Definition,
            ScheduleRecurrenceKind.Once,
            firstLocal,
            invalidLocal: ScheduleInvalidLocalTimePolicy.Skip);
        var timeZone = InvalidGapTimeZone();
        using var runtime = CreateRuntime(workspace, context, timeZone);

        var result = await runtime.CreateAsync(definition);
        var evaluated = await runtime.EvaluateOnceAsync(definition.ScheduleId);

        Assert.Equal(ScheduleRuntimeCreateStatus.Created, result.Status);
        Assert.Null(result.CurrentState!.NextOccurrence);
        var skipped = Assert.Single(result.CurrentState.DispositionEvidence);
        Assert.Equal(1, skipped.FirstOrdinal);
        Assert.Equal(ScheduleOccurrenceDisposition.InvalidLocalTimeSkipped, skipped.Disposition);
        Assert.Equal(ScheduleEvaluationStatus.Exhausted, evaluated.Status);
        Assert.Equal(0, context.PayloadReadCount);
    }

    [Fact]
    public async Task Fixed_interval_gap_skip_anchors_elapsed_cadence_at_the_first_valid_boundary()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var firstLocal = new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified);
        var definition = WithRecurrence(
            context.Definition,
            ScheduleRecurrenceKind.FixedInterval,
            firstLocal,
            intervalSeconds: 3_600,
            invalidLocal: ScheduleInvalidLocalTimePolicy.Skip);
        var firstValidUtc = new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.Zero);
        var expectedUtc = firstValidUtc.AddHours(1);
        var expectedLocal = new DateTime(2026, 3, 8, 4, 0, 0, DateTimeKind.Unspecified);
        var timeZone = InvalidGapTimeZone(firstValidUtc);
        timeZone.InstantResolver = (zone, instant) => new ScheduleInstantResolution(
            ScheduleInstantResolutionStatus.Resolved,
            zone.RulesFingerprint,
            instant == expectedUtc ? expectedLocal : default);
        using var runtime = CreateRuntime(workspace, context, timeZone);

        var result = await runtime.CreateAsync(definition);

        Assert.Equal(ScheduleRuntimeCreateStatus.Created, result.Status);
        Assert.Equal(2, result.CurrentState!.NextOccurrence!.Ordinal);
        Assert.Equal(expectedUtc, result.CurrentState.NextOccurrence.ScheduledAtUtc);
        Assert.Equal(expectedLocal, result.CurrentState.NextOccurrence.ScheduledLocal);
        Assert.Single(result.CurrentState.DispositionEvidence);
        Assert.Equal(expectedUtc, Assert.Single(timeZone.InstantInputs));
    }

    [Theory]
    [InlineData(ScheduleInstantResolutionStatus.Unavailable, ScheduleRuntimeCreateStatus.Unavailable)]
    [InlineData(ScheduleInstantResolutionStatus.Backpressured, ScheduleRuntimeCreateStatus.Backpressured)]
    [InlineData(ScheduleInstantResolutionStatus.Corrupt, ScheduleRuntimeCreateStatus.Corrupt)]
    [InlineData(ScheduleInstantResolutionStatus.Unknown, ScheduleRuntimeCreateStatus.Corrupt)]
    public async Task Fixed_interval_gap_skip_preserves_closed_instant_resolution_failures(
        ScheduleInstantResolutionStatus status,
        ScheduleRuntimeCreateStatus expected)
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var definition = WithRecurrence(
            context.Definition,
            ScheduleRecurrenceKind.FixedInterval,
            new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified),
            intervalSeconds: 3_600,
            invalidLocal: ScheduleInvalidLocalTimePolicy.Skip);
        var timeZone = InvalidGapTimeZone(new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.Zero));
        timeZone.InstantResolver = (_, _) => new ScheduleInstantResolution(status, null, default);
        using var runtime = CreateRuntime(workspace, context, timeZone);

        var result = await runtime.CreateAsync(definition);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.CurrentState);
    }

    [Fact]
    public async Task Fixed_interval_gap_skip_maps_instant_exceptions_and_exhausts_at_the_supported_year_bound()
    {
        using var failureWorkspace = new TestWorkspace();
        var failureContext = ScheduleCurrentEvidenceTestContext.Create();
        var failureDefinition = WithRecurrence(
            failureContext.Definition,
            ScheduleRecurrenceKind.FixedInterval,
            new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified),
            intervalSeconds: 3_600,
            invalidLocal: ScheduleInvalidLocalTimePolicy.Skip);
        var failureTimeZone = InvalidGapTimeZone(new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.Zero));
        failureTimeZone.InstantResolver = (_, _) => throw new IOException("instant unavailable");
        using var failureRuntime = CreateRuntime(failureWorkspace, failureContext, failureTimeZone);
        Assert.Equal(
            ScheduleRuntimeCreateStatus.Unavailable,
            (await failureRuntime.CreateAsync(failureDefinition)).Status);

        using var exhaustedWorkspace = new TestWorkspace();
        var exhaustedContext = ScheduleCurrentEvidenceTestContext.Create();
        var exhaustedDefinition = WithRecurrence(
            exhaustedContext.Definition,
            ScheduleRecurrenceKind.FixedInterval,
            new DateTime(9998, 12, 31, 22, 30, 0, DateTimeKind.Unspecified),
            intervalSeconds: ScheduleContractLimits.MaxFixedIntervalSeconds,
            invalidLocal: ScheduleInvalidLocalTimePolicy.Skip);
        var exhaustedTimeZone = InvalidGapTimeZone(new DateTimeOffset(9998, 12, 31, 23, 0, 0, TimeSpan.Zero));
        using var exhaustedRuntime = CreateRuntime(exhaustedWorkspace, exhaustedContext, exhaustedTimeZone);

        var exhausted = await exhaustedRuntime.CreateAsync(exhaustedDefinition);

        Assert.Equal(ScheduleRuntimeCreateStatus.Created, exhausted.Status);
        Assert.Null(exhausted.CurrentState!.NextOccurrence);
        Assert.Single(exhausted.CurrentState.DispositionEvidence);
        Assert.Equal(0, exhaustedTimeZone.InstantCalls);
    }

    [Fact]
    public async Task Daily_gap_skip_advances_over_consecutive_invalid_nominals_within_the_evidence_bound()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var firstLocal = new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified);
        var definition = WithRecurrence(
            context.Definition,
            ScheduleRecurrenceKind.Daily,
            firstLocal,
            invalidLocal: ScheduleInvalidLocalTimePolicy.Skip);
        var timeZone = new RuntimeTimeZone();
        timeZone.LocalResolver = (zone, local) => timeZone.LocalCalls <= 2
            ? new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.InvalidLocalTime,
                zone.RulesFingerprint,
                local.AddMinutes(30),
                new DateTimeOffset(local.AddHours(6), TimeSpan.Zero),
                null)
            : RuntimeTimeZone.Unique(zone, local);
        using var runtime = CreateRuntime(workspace, context, timeZone);

        var result = await runtime.CreateAsync(definition);

        Assert.Equal(ScheduleRuntimeCreateStatus.Created, result.Status);
        Assert.Equal(3, result.CurrentState!.NextOccurrence!.Ordinal);
        Assert.Equal(firstLocal.AddDays(2), result.CurrentState.NextOccurrence.ScheduledLocal);
        Assert.Equal([1L, 2L], result.CurrentState.DispositionEvidence.Select(item => item.FirstOrdinal));
        Assert.Equal(3, timeZone.LocalCalls);
    }

    [Fact]
    public async Task Weekly_gap_skip_uses_the_exact_weekly_nominal_and_exhausts_at_the_supported_year_bound()
    {
        using var weeklyWorkspace = new TestWorkspace();
        var weeklyContext = ScheduleCurrentEvidenceTestContext.Create();
        var firstWeekly = new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified);
        var weeklyDefinition = WithRecurrence(
            weeklyContext.Definition,
            ScheduleRecurrenceKind.Weekly,
            firstWeekly,
            invalidLocal: ScheduleInvalidLocalTimePolicy.Skip);
        var weeklyTimeZone = new RuntimeTimeZone();
        weeklyTimeZone.LocalResolver = (zone, local) => weeklyTimeZone.LocalCalls == 1
            ? new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.InvalidLocalTime,
                zone.RulesFingerprint,
                local.AddMinutes(30),
                new DateTimeOffset(local.AddHours(6), TimeSpan.Zero),
                null)
            : RuntimeTimeZone.Unique(zone, local);
        using var weeklyRuntime = CreateRuntime(weeklyWorkspace, weeklyContext, weeklyTimeZone);
        var weekly = await weeklyRuntime.CreateAsync(weeklyDefinition);
        Assert.Equal(firstWeekly.AddDays(7), weekly.CurrentState!.NextOccurrence!.ScheduledLocal);

        using var exhaustedWorkspace = new TestWorkspace();
        var exhaustedContext = ScheduleCurrentEvidenceTestContext.Create();
        var exhaustedDefinition = WithRecurrence(
            exhaustedContext.Definition,
            ScheduleRecurrenceKind.Daily,
            new DateTime(9998, 12, 31, 2, 30, 0, DateTimeKind.Unspecified),
            invalidLocal: ScheduleInvalidLocalTimePolicy.Skip);
        using var exhaustedRuntime = CreateRuntime(exhaustedWorkspace, exhaustedContext, InvalidGapTimeZone());
        var exhausted = await exhaustedRuntime.CreateAsync(exhaustedDefinition);

        Assert.Equal(ScheduleRuntimeCreateStatus.Created, exhausted.Status);
        Assert.Null(exhausted.CurrentState!.NextOccurrence);
    }

    [Fact]
    public async Task Initial_recurrence_probe_bound_fails_closed_without_persisting_a_partial_schedule()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var definition = WithRecurrence(
            context.Definition,
            ScheduleRecurrenceKind.Daily,
            new DateTime(2026, 1, 1, 2, 30, 0, DateTimeKind.Unspecified),
            invalidLocal: ScheduleInvalidLocalTimePolicy.Skip);
        var timeZone = InvalidGapTimeZone();
        using var runtime = CreateRuntime(workspace, context, timeZone);

        var result = await runtime.CreateAsync(definition);
        var read = await runtime.ReadAsync(definition.ScheduleId);

        Assert.Equal(ScheduleRuntimeCreateStatus.BoundExceeded, result.Status);
        Assert.Null(result.CurrentState);
        Assert.Equal(ScheduleStoreReadStatus.NotFound, read.Status);
        Assert.Equal(ScheduleContractLimits.MaxDispositionEvidenceItems + 1, timeZone.LocalCalls);
        Assert.Equal(0, context.PayloadReadCount);
    }

    [Fact]
    public async Task Malformed_time_zone_evidence_fails_closed_without_persisting_caller_coordinates()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var timeZone = new RuntimeTimeZone
        {
            LocalResolver = (_, local) => new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.Unique,
                new string('0', 64),
                local,
                context.Occurrence.ScheduledAtUtc,
                null),
        };
        using var runtime = CreateRuntime(workspace, context, timeZone);

        var result = await runtime.CreateAsync(context.Definition);
        var read = await runtime.ReadAsync(context.Definition.ScheduleId);

        Assert.Equal(ScheduleRuntimeCreateStatus.Corrupt, result.Status);
        Assert.Null(result.CurrentState);
        Assert.Equal(ScheduleStoreReadStatus.NotFound, read.Status);
    }

    [Fact]
    public async Task Invalid_definition_state_and_cancellation_are_rejected_at_the_public_boundary()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        using var runtime = CreateRuntime(workspace, context, new RuntimeTimeZone());
        Assert.Equal(
            ScheduleRuntimeCreateStatus.Corrupt,
            (await runtime.CreateAsync(context.Definition with { Recurrence = null! })).Status);
        var created = await runtime.CreateAsync(context.Definition);
        var exhaustedRevision = created.CurrentState! with { StateRevision = ScheduleContractLimits.MaxRevision };
        Assert.Equal(
            ScheduleStoreMutationStatus.Corrupt,
            (await runtime.SetEnabledAsync(exhaustedRevision, false)).Status);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.CreateAsync(context.Definition, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.SetEnabledAsync(created.CurrentState!, false, cancellation.Token));
    }

    [Fact]
    public async Task Production_factory_retains_composition_sources_and_disposal_is_idempotent()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var runStore = new ScheduleOverlapRunStore();
        var runtime = ScheduleRuntimeFactory.Create(
            new WorkspacePaths(workspace.RootPath),
            context,
            runStore,
            new FixedTimeProvider(_now));

        Assert.Equal(ScheduleStoreReadStatus.NotFound, (await runtime.ReadAsync(context.Definition.ScheduleId)).Status);

        runtime.Dispose();
        runtime.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.ReadAsync(context.Definition.ScheduleId));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.EvaluateOnceAsync(context.Definition.ScheduleId));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.CreateAsync(context.Definition));
        Assert.Equal(0, runStore.DisposeCount);
    }

    [Fact]
    public async Task Current_evidence_and_run_store_composition_queues_when_the_exact_borrowed_canonical_store_is_idle()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var runStore = new CustomLoopRunStore(paths);
        using (var runtime = ScheduleRuntimeFactory.Create(
                   paths,
                   context.AdapterUnderTest(),
                   runStore,
                   new RuntimeTimeZone(),
                   new FixedTimeProvider(_now)))
        {
            Assert.Equal(ScheduleRuntimeCreateStatus.Created, (await runtime.CreateAsync(context.Definition)).Status);

            var evaluated = await runtime.EvaluateOnceAsync(context.Definition.ScheduleId);

            Assert.Equal(ScheduleEvaluationStatus.Queued, evaluated.Status);
        }

        Assert.Null(await runStore.GetNonterminalByLoopAsync(context.Definition.Target.LoopId));
    }

    [Fact]
    public async Task Current_evidence_and_run_store_composition_skips_when_the_exact_borrowed_canonical_store_has_an_active_target_run()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var runStore = new TrackingCustomLoopRunStore(new CustomLoopRunStore(paths));
        var (run, target) = await ScheduleRunOverlapAdapterTests.MaterializeGovernedRunAsync(runStore);
        var definition = context.Definition with { Target = target, Overlap = ScheduleOverlapPolicy.Skip };
        Assert.Equal(1, runStore.CreateCallCount);
        Assert.Equal(run.Id, runStore.LastCreatedRunId);
        Assert.Equal(0, runStore.GetNonterminalByLoopCallCount);

        using (var runtime = ScheduleRuntimeFactory.Create(
                   paths,
                   context.AdapterUnderTest(),
                   runStore,
                   new RuntimeTimeZone(),
                   new FixedTimeProvider(_now)))
        {
            var created = await runtime.CreateAsync(definition);
            Assert.Equal(ScheduleRuntimeCreateStatus.Created, created.Status);
            Assert.NotNull(created.CurrentState);
            Assert.True(ScheduleIdentityDerivation.TryDerive(
                created.CurrentState.ScheduleId,
                created.CurrentState.DefinitionRevision,
                created.CurrentState.DefinitionHash,
                created.CurrentState.NextOccurrence,
                out var identity,
                out _));
            var expectedOverlap = await new ScheduleRunOverlapAdapter(runStore).GetStatusAsync(definition.Target, identity!, _now);
            Assert.Equal(ScheduleOverlapStatus.Active, expectedOverlap.Status);
            Assert.Equal(1, runStore.GetNonterminalByLoopCallCount);
            Assert.Equal(run.LoopId, runStore.LastNonterminalLoopId);

            var evaluated = await runtime.EvaluateOnceAsync(definition.ScheduleId);

            Assert.Equal(ScheduleEvaluationStatus.Skipped, evaluated.Status);
            Assert.Equal("overlap-policy-skip", evaluated.ReasonCode);
            Assert.NotNull(evaluated.State);
            var disposition = Assert.Single(evaluated.State.DispositionEvidence);
            Assert.Equal(ScheduleOccurrenceDisposition.OverlapSkipped, disposition.Disposition);
            Assert.Matches("^[0-9a-f]{64}$", disposition.DecisionEvidenceHash!);
            Assert.Equal(expectedOverlap.EvidenceHash, disposition.DecisionEvidenceHash);
            Assert.Equal(0, context.PayloadReadCount);
            Assert.Equal(2, runStore.GetNonterminalByLoopCallCount);
            Assert.Equal(run.LoopId, runStore.LastNonterminalLoopId);
        }

        Assert.False(runStore.IsDisposed);
        Assert.Equal(0, runStore.DisposeCount);
        Assert.Equal(0, runStore.InnerDisposeCount);
        var retained = await runStore.GetNonterminalByLoopAsync(run.LoopId);
        Assert.NotNull(retained);
        Assert.Equal(run.Id, retained.Id);
        Assert.Equal(run.AdmissionOperationId, retained.AdmissionOperationId);
        Assert.Equal(3, runStore.GetNonterminalByLoopCallCount);
        Assert.Equal(run.LoopId, runStore.LastNonterminalLoopId);
    }

    [Fact]
    public void Current_evidence_and_run_store_composition_failure_does_not_dispose_the_borrowed_store()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var runStore = new ScheduleOverlapRunStore();

        Assert.Throws<ArgumentNullException>(() => ScheduleRuntimeFactory.Create(
            new WorkspacePaths(workspace.RootPath),
            context.AdapterUnderTest(),
            runStore,
            null!));

        Assert.Equal(0, runStore.DisposeCount);
    }

    [Fact]
    public async Task Production_factory_uses_the_trigger_workspace_token_before_resolving_the_governed_target()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var workspaceScope = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var triggerWorkspace = workspaceScope["workspace-sha256:".Length..];
        var timeZones = TimeZoneInfo.GetSystemTimeZones();
        var timeZoneAdapter = new SystemScheduleTimeZoneAdapter(timeZones);
        TimeZoneInfo? selectedTimeZone = null;
        ScheduleTimeZoneResolution? timeZoneEvidence = null;
        foreach (var candidate in timeZones)
        {
            var resolution = await timeZoneAdapter.ResolveLocalAsync(
                new ScheduleTimeZoneReference(candidate.Id, new string('0', 64)),
                context.Definition.Recurrence.FirstLocalOccurrence);
            if (resolution.Status == ScheduleTimeZoneResolutionStatus.Unique)
            {
                selectedTimeZone = candidate;
                timeZoneEvidence = resolution;
                break;
            }
        }

        Assert.NotNull(selectedTimeZone);
        Assert.NotNull(timeZoneEvidence);
        Assert.NotNull(timeZoneEvidence.RulesFingerprint);
        Assert.NotNull(timeZoneEvidence.EarlierUtc);
        var definition = context.Definition with
        {
            WorkspaceId = triggerWorkspace,
            TimeZone = new ScheduleTimeZoneReference(selectedTimeZone.Id, timeZoneEvidence.RulesFingerprint),
        };
        using var runtime = ScheduleRuntimeFactory.Create(
            paths,
            new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath),
            context,
            new ScheduleOverlapRunStore(),
            new FixedTimeProvider(timeZoneEvidence.EarlierUtc.Value.AddMinutes(1)));

        var created = await runtime.CreateAsync(definition);
        var evaluated = await runtime.EvaluateOnceAsync(definition.ScheduleId);

        Assert.Equal(ScheduleRuntimeCreateStatus.Created, created.Status);
        Assert.Equal(ScheduleEvaluationStatus.Unavailable, evaluated.Status);
        Assert.Equal("schedule-target-unavailable", evaluated.ReasonCode);
        Assert.Equal(0, context.FenceCount);
        Assert.Equal(0, context.PayloadReadCount);
    }

    [Fact]
    public async Task One_shot_evaluation_admits_a_real_time_envelope_to_the_durable_queue_without_provider_execution()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var timeZone = new RuntimeTimeZone();
        using var runtime = CreateRuntime(workspace, context, timeZone);
        Assert.Equal(ScheduleRuntimeCreateStatus.Created, (await runtime.CreateAsync(context.Definition)).Status);

        var evaluated = await runtime.EvaluateOnceAsync(context.Definition.ScheduleId);
        var store = new TriggerQueueStore(paths, TriggerQueueQuota.Runtime, timeProvider: new FixedTimeProvider(_now));
        var entry = Assert.Single((await store.GetSnapshotAsync(_now)).Entries);
        var history = await store.FindAsync(entry.DeliveryId, entry.DeduplicationId);

        Assert.Equal(ScheduleEvaluationStatus.Queued, evaluated.Status);
        Assert.Equal(TriggerDeliveryAdmissionHistoryLookupStatus.Available, history.Status);
        Assert.Equal(TriggerKind.Time, history.DeliveryMatch!.Envelope.Kind);
        Assert.Equal(context.Definition.Target, history.DeliveryMatch.Envelope.Loop);
        Assert.Equal(context.Definition.TimeAdapter, history.DeliveryMatch.Envelope.Adapter);
        Assert.Equal(2, context.PayloadReadCount);
        Assert.Null(entry.Dispatch);
    }

    [Fact]
    public async Task Schedule_runtime_queue_budget_outlives_the_rolling_schedule_evidence_window()
    {
        using var workspace = new TestWorkspace();
        var context = ScheduleCurrentEvidenceTestContext.Create();
        using var runtime = CreateRuntime(workspace, context, new RuntimeTimeZone());

        Assert.Equal(ScheduleRuntimeCreateStatus.Created, (await runtime.CreateAsync(context.Definition)).Status);
        Assert.Equal(ScheduleEvaluationStatus.Queued, (await runtime.EvaluateOnceAsync(context.Definition.ScheduleId)).Status);

        var snapshot = await new TriggerQueueStore(new WorkspacePaths(workspace.RootPath), TriggerQueueQuota.Runtime).GetSnapshotAsync(_now);

        Assert.True(snapshot.Quota.MaxRetainedEntries > ScheduleContractLimits.MaxTerminalDeliveryEvidenceItems);
        Assert.Equal(TriggerQueueQuota.Runtime.MaxRetainedEntries, snapshot.Quota.MaxRetainedEntries);
    }

    private static ScheduleRuntimeFacade CreateRuntime(
        TestWorkspace workspace,
        ScheduleCurrentEvidenceTestContext context,
        RuntimeTimeZone timeZone)
        => ScheduleRuntimeFactory.Create(
            new WorkspacePaths(workspace.RootPath),
            context.AdapterUnderTest(),
            new ClearOverlap(),
            timeZone,
            new FixedTimeProvider(_now));

    private static ScheduleDefinition WithRecurrence(
        ScheduleDefinition definition,
        ScheduleRecurrenceKind kind,
        DateTime firstLocal,
        long? intervalSeconds = null,
        ScheduleInvalidLocalTimePolicy invalidLocal = ScheduleInvalidLocalTimePolicy.ShiftForward)
        => definition with
        {
            Recurrence = new ScheduleRecurrenceRule(kind, firstLocal, intervalSeconds),
            DaylightSaving = definition.DaylightSaving with { InvalidLocalTime = invalidLocal },
        };

    private static RuntimeTimeZone InvalidGapTimeZone(DateTimeOffset? firstValidUtc = null)
        => new()
        {
            LocalResolver = (zone, local) => new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.InvalidLocalTime,
                zone.RulesFingerprint,
                local.AddMinutes(30),
                firstValidUtc ?? new DateTimeOffset(local.AddHours(6), TimeSpan.Zero),
                null),
        };

    private sealed class RuntimeTimeZone : IScheduleTimeZonePort
    {
        internal Func<ScheduleTimeZoneReference, DateTime, ScheduleTimeZoneResolution>? LocalResolver { get; set; }
        internal Func<ScheduleTimeZoneReference, DateTimeOffset, ScheduleInstantResolution>? InstantResolver { get; set; }
        internal int LocalCalls { get; private set; }
        internal int InstantCalls { get; private set; }
        internal List<DateTimeOffset> InstantInputs { get; } = [];

        public Task<ScheduleTimeZoneResolution> ResolveLocalAsync(
            ScheduleTimeZoneReference timeZone,
            DateTime scheduledLocal,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalCalls++;
            return Task.FromResult(LocalResolver?.Invoke(timeZone, scheduledLocal) ?? Unique(timeZone, scheduledLocal));
        }

        public Task<ScheduleInstantResolution> ResolveInstantAsync(
            ScheduleTimeZoneReference timeZone,
            DateTimeOffset scheduledAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InstantCalls++;
            InstantInputs.Add(scheduledAtUtc);
            var local = DateTime.SpecifyKind(scheduledAtUtc.UtcDateTime.AddHours(-5), DateTimeKind.Unspecified);
            return Task.FromResult(InstantResolver?.Invoke(timeZone, scheduledAtUtc)
                ?? new ScheduleInstantResolution(ScheduleInstantResolutionStatus.Resolved, timeZone.RulesFingerprint, local));
        }

        internal static ScheduleTimeZoneResolution Unique(ScheduleTimeZoneReference timeZone, DateTime local)
            => new(
                ScheduleTimeZoneResolutionStatus.Unique,
                timeZone.RulesFingerprint,
                local,
                new DateTimeOffset(local.AddHours(5), TimeSpan.Zero),
                null);
    }

    private sealed class ClearOverlap : IScheduleOverlapPort
    {
        public Task<ScheduleOverlapResult> GetStatusAsync(
            TriggerLoopReference target,
            ScheduleOccurrenceIdentity occurrenceIdentity,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ScheduleOverlapResult(ScheduleOverlapStatus.Clear, new string('a', 64)));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("clock unavailable");
    }
}
