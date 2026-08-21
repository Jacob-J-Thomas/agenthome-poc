using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Loops.Posture.Models;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Loops.Posture;
using EmbodySense.Core.Common.Tests.Triggers.Schedules;
using EmbodySense.Core.Common.Tests;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Triggers.Schedules;
using EmbodySense.Core.Persistence.Triggers.Schedules.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Triggers.Schedules;

public sealed class ScheduleStoreTests
{
    private static readonly TimeSpan _crossProcessReadinessTimeout = TimeSpan.FromSeconds(60);

    private const string CrossProcessWorkspace = "EMBODYSENSE_SCHEDULE_STORE_WORKSPACE";
    private const string CrossProcessGate = "EMBODYSENSE_SCHEDULE_STORE_GATE";
    private const string CrossProcessReady = "EMBODYSENSE_SCHEDULE_STORE_READY";
    private const string CrossProcessOutput = "EMBODYSENSE_SCHEDULE_STORE_OUTPUT";
    private const string CrossProcessScheduleId = "EMBODYSENSE_SCHEDULE_STORE_ID";
    private const string CrossProcessCrashBoundary = "EMBODYSENSE_SCHEDULE_STORE_CRASH_BOUNDARY";
    private const string CrossProcessOperation = "EMBODYSENSE_SCHEDULE_STORE_OPERATION";
    private const string CrossProcessVariant = "EMBODYSENSE_SCHEDULE_STORE_VARIANT";
    private const string CrossProcessMaxDurabilityArtifacts = "EMBODYSENSE_SCHEDULE_STORE_MAX_DURABILITY_ARTIFACTS";

    [Fact]
    public async Task Accepted_terminal_schedule_provenance_is_exact_restart_safe_and_conflict_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var (request, envelope) = ScheduleStoreTestData.ProvenanceRequest();
        Assert.Equal(ScheduleStoreMutationStatus.Applied, (await new ScheduleStore(paths).CreateAsync(request)).Status);

        var found = await new ScheduleStore(paths).ResolveAsync(envelope);
        Assert.Equal(ScheduleDeliveryProvenanceStatus.Found, found.Status);
        Assert.Equal(request.Definition, found.Evidence!.Definition);
        Assert.Equal(request.CanonicalDefinitionHash, found.Evidence.DefinitionHash);
        Assert.Equal(envelope.DeliveryId, found.Evidence.Identity.DeliveryId);
        Assert.Equal(envelope.DeduplicationId, found.Evidence.Identity.DeduplicationId);
        Assert.Equal(ScheduleDeliveryResultKind.Queued, found.Evidence.Result.Kind);

        var changedPayload = TriggerDeliveryTestData.InlinePayload([9, 9, 9]);
        var conflicting = ScheduledEnvelope(envelope, changedPayload);
        Assert.Equal(
            ScheduleDeliveryProvenanceStatus.Conflict,
            (await new ScheduleStore(paths).ResolveAsync(conflicting)).Status);

        Assert.True(ScheduleId.TryParse("forged-schedule", out var forgedScheduleId));
        var forgedDirective = TriggerDeliveryTestData.ScheduleDirective(
            envelope.ScheduleExecutionDirective!.Occurrence,
            forgedScheduleId,
            target: envelope.Loop);
        var forged = ScheduledEnvelope(envelope, envelope.Payload, forgedDirective);
        Assert.Equal(
            ScheduleDeliveryProvenanceStatus.NotFound,
            (await new ScheduleStore(paths).ResolveAsync(forged)).Status);
    }

    [Fact]
    public async Task Rejected_terminal_schedule_evidence_never_authenticates_dispatch()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var (request, envelope) = ScheduleStoreTestData.ProvenanceRequest(ScheduleDeliveryResultKind.Rejected);
        Assert.Equal(ScheduleStoreMutationStatus.Applied, (await new ScheduleStore(paths).CreateAsync(request)).Status);

        var resolved = await new ScheduleStore(paths).ResolveAsync(envelope);

        Assert.Equal(ScheduleDeliveryProvenanceStatus.NotFound, resolved.Status);
        Assert.Null(resolved.Evidence);
    }

    [Fact]
    public async Task Exact_pending_result_observation_defers_while_mismatched_identity_evidence_conflicts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var request = ScheduleStoreTestData.CreateRequest(comprehensiveState: true);
        var pendingEnvelope = request.InitialState.PendingDelivery!.Prepared!.Envelope;
        Assert.Equal(SchedulePendingDeliveryPhase.ResultObserved, request.InitialState.PendingDelivery.Phase);
        Assert.Equal(ScheduleStoreMutationStatus.Applied, (await new ScheduleStore(paths).CreateAsync(request)).Status);

        var store = new ScheduleStore(paths);
        var resolved = await store.ResolveAsync(pendingEnvelope);
        var conflictingPayload = TriggerDeliveryTestData.InlinePayload([9, 9, 9]);
        var conflicting = ScheduledEnvelope(pendingEnvelope, conflictingPayload);

        Assert.Equal(ScheduleDeliveryProvenanceStatus.PendingFinalization, resolved.Status);
        Assert.Null(resolved.Evidence);
        Assert.Equal(ScheduleDeliveryProvenanceStatus.Conflict, (await store.ResolveAsync(conflicting)).Status);

        using var preparedWorkspace = new TestWorkspace();
        var preparedPaths = new WorkspacePaths(preparedWorkspace.RootPath);
        var preparedRequest = ScheduleStoreTestData.CreateRequest(comprehensiveState: true);
        var preparedState = preparedRequest.InitialState with
        {
            PendingDelivery = preparedRequest.InitialState.PendingDelivery! with
            {
                Phase = SchedulePendingDeliveryPhase.Prepared,
                Result = null,
            },
        };
        Assert.True(ScheduleContractValidator.ValidateDefinitionStateComposition(
            preparedRequest.Definition,
            preparedState).IsValid);
        Assert.Equal(
            ScheduleStoreMutationStatus.Applied,
            (await new ScheduleStore(preparedPaths).CreateAsync(
                preparedRequest with { InitialState = preparedState })).Status);
        Assert.Equal(
            ScheduleDeliveryProvenanceStatus.Conflict,
            (await new ScheduleStore(preparedPaths).ResolveAsync(
                preparedState.PendingDelivery!.Prepared!.Envelope)).Status);
    }

    [Fact]
    public async Task Operational_pages_are_deterministic_cursor_safe_detached_and_validate_the_whole_catalog_before_projection()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ScheduleStore(paths);
        foreach (var suffix in new[] { "c", "a", "b" })
        {
            Assert.Equal(
                ScheduleStoreMutationStatus.Applied,
                (await store.CreateAsync(ScheduleStoreTestData.CreateRequest($"schedule-{suffix}"))).Status);
        }

        var first = await store.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1));
        var firstAgain = await store.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1));
        var second = await store.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1, first.ContinuationCursor));
        var third = await store.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1, second.ContinuationCursor));
        var nonexistent = await store.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1, "schedule-aa"));
        var beyondTail = await store.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1, "schedule-z"));
        var malformed = await store.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1, "bad cursor"));

        Assert.Equal("schedule-a", Assert.Single(first.Items).Definition.ScheduleId.Value);
        Assert.Equal("schedule-a", first.ContinuationCursor);
        Assert.Equal("schedule-b", Assert.Single(second.Items).Definition.ScheduleId.Value);
        Assert.Equal("schedule-b", second.ContinuationCursor);
        Assert.Equal("schedule-c", Assert.Single(third.Items).Definition.ScheduleId.Value);
        Assert.False(third.HasMore);
        Assert.Null(third.ContinuationCursor);
        Assert.Equal("schedule-b", Assert.Single(nonexistent.Items).Definition.ScheduleId.Value);
        Assert.Equal(GovernedLoopOperationalEvidenceReadStatus.Empty, beyondTail.Status);
        Assert.Equal(GovernedLoopOperationalEvidenceReadStatus.Corrupt, malformed.Status);
        Assert.NotSame(first.Items[0].Definition, firstAgain.Items[0].Definition);
        Assert.NotSame(first.Items[0].State, firstAgain.Items[0].State);
        var exposed = Assert.IsAssignableFrom<IList<GovernedLoopScheduleEvidenceSnapshot>>(first.Items);
        Assert.Throws<NotSupportedException>(() => exposed.Add(first.Items[0]));

        await File.WriteAllTextAsync(Assert.Single(Directory.EnumerateFiles(StoreRoot(paths), "ledger-*.json")), "{}");
        var corruptOffPage = await store.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1, "schedule-z"));
        Assert.Equal(GovernedLoopOperationalEvidenceReadStatus.Corrupt, corruptOffPage.Status);
    }

    [Fact]
    public async Task Create_read_restart_and_exact_retry_preserve_canonical_contracts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var request = ScheduleStoreTestData.CreateRequest(comprehensiveState: true);
        var created = await new ScheduleStore(paths).CreateAsync(request);

        var restarted = new ScheduleStore(paths);
        var read = await restarted.ReadAsync(request.Definition.ScheduleId);
        var replay = await restarted.CreateAsync(request);

        Assert.Equal(ScheduleStoreMutationStatus.Applied, created.Status);
        Assert.Equal(ScheduleStoreReadStatus.Found, read.Status);
        Assert.Equal(request.Definition, read.Definition);
        Assert.True(ScheduleContractHash.TryComputeState(request.InitialState, out var expectedStateHash, out _));
        Assert.True(ScheduleContractHash.TryComputeState(read.State!, out var actualStateHash, out _));
        Assert.Equal(expectedStateHash, actualStateHash);
        Assert.Equal(
            request.InitialState.PendingDelivery!.Prepared!.CanonicalEnvelopeHash,
            read.State!.PendingDelivery!.Prepared!.CanonicalEnvelopeHash);
        Assert.Equal(
            request.InitialState.PendingDelivery.OverlapEvidenceHash,
            read.State.PendingDelivery.OverlapEvidenceHash);
        Assert.Equal(request.InitialState.DispositionEvidence, read.State.DispositionEvidence);
        Assert.Equal(
            request.InitialState.DispositionEvidence[0].DecisionEvidenceHash,
            read.State.DispositionEvidence[0].DecisionEvidenceHash);
        Assert.Equal(request.InitialState.TerminalDeliveryEvidence, read.State.TerminalDeliveryEvidence);
        Assert.Equal(
            request.InitialState.TerminalDeliveryEvidence[0].CurrentEvidenceHash,
            read.State.TerminalDeliveryEvidence[0].CurrentEvidenceHash);
        Assert.Equal(
            request.InitialState.TerminalDeliveryEvidence[0].RecurrenceProofHash,
            read.State.TerminalDeliveryEvidence[0].RecurrenceProofHash);
        Assert.Equal(
            request.InitialState.TerminalDeliveryEvidence[0].OverlapEvidenceHash,
            read.State.TerminalDeliveryEvidence[0].OverlapEvidenceHash);
        Assert.NotSame(request.Definition, read.Definition);
        Assert.NotSame(request.InitialState, read.State);
        Assert.Equal(ScheduleStoreMutationStatus.AlreadyExists, replay.Status);
        Assert.True(ScheduleContractHash.TryComputeState(replay.CurrentState!, out var replayStateHash, out _));
        Assert.Equal(expectedStateHash, replayStateHash);
        Assert.Single(Directory.EnumerateFiles(StoreRoot(paths), "ledger-*.json"));
    }

    [Fact]
    public async Task Canonical_catalog_round_trips_every_closed_schedule_policy_token()
    {
        using var workspace = new TestWorkspace();
        var store = new ScheduleStore(new WorkspacePaths(workspace.RootPath));
        var variants = new Func<ScheduleDefinition, ScheduleDefinition>[]
        {
            definition => definition with
            {
                Priority = SchedulePriority.Background,
                Recurrence = definition.Recurrence with
                {
                    Kind = ScheduleRecurrenceKind.Once,
                    FixedIntervalSeconds = null,
                },
                DaylightSaving = new ScheduleDaylightSavingPolicy(
                    ScheduleInvalidLocalTimePolicy.Skip,
                    ScheduleAmbiguousLocalTimePolicy.LaterUtc),
                Misfire = new ScheduleMisfirePolicy(ScheduleMisfirePolicyKind.Skip, 0),
                Overlap = ScheduleOverlapPolicy.Allow,
                Enabled = false,
            },
            definition => definition with
            {
                Priority = SchedulePriority.Elevated,
                Recurrence = definition.Recurrence with
                {
                    Kind = ScheduleRecurrenceKind.FixedInterval,
                    FixedIntervalSeconds = 60,
                },
                Misfire = new ScheduleMisfirePolicy(ScheduleMisfirePolicyKind.FireLatestOnce, 0),
                Overlap = ScheduleOverlapPolicy.Skip,
            },
            definition => definition with
            {
                Priority = SchedulePriority.Critical,
                Recurrence = definition.Recurrence with
                {
                    Kind = ScheduleRecurrenceKind.Weekly,
                    FixedIntervalSeconds = null,
                },
            },
        };

        for (var index = 0; index < variants.Length; index++)
        {
            var seed = ScheduleStoreTestData.CreateRequest($"schedule-policy-{index}");
            var definition = variants[index](seed.Definition);
            Assert.True(ScheduleContractHash.TryComputeDefinition(definition, out var definitionHash, out var validation),
                string.Join(',', validation.Errors.Select(error => $"{error.Path}:{error.Code}")));
            var state = seed.InitialState with
            {
                DefinitionHash = definitionHash!,
                Enabled = definition.Enabled,
            };
            var composition = ScheduleContractValidator.ValidateDefinitionStateComposition(definition, state);
            Assert.True(composition.IsValid,
                string.Join(',', composition.Errors.Select(error => $"{error.Path}:{error.Code}")));

            var created = await store.CreateAsync(new ScheduleStoreCreateRequest(definition, state, definitionHash!));
            var read = await new ScheduleStore(new WorkspacePaths(workspace.RootPath))
                .ReadAsync(definition.ScheduleId);

            Assert.Equal(ScheduleStoreMutationStatus.Applied, created.Status);
            Assert.Equal(ScheduleStoreReadStatus.Found, read.Status);
            Assert.Equal(definition, read.Definition);
            Assert.Equal(state, read.State);
        }
    }

    [Fact]
    public async Task Canonical_catalog_round_trips_active_catch_up_and_deferred_state()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ScheduleStore(paths);

        var catchUpRequest = ScheduleStoreTestData.CreateRequest("catch-up-schedule");
        var catchUpState = catchUpRequest.InitialState with
        {
            CatchUpEpisode = new ScheduleCatchUpEpisode(
                ScheduleCatchUpEpisode.CurrentSchemaVersion,
                catchUpRequest.InitialState.NextOccurrence!.Ordinal + 2,
                2),
        };
        var catchUpComposition = ScheduleContractValidator.ValidateDefinitionStateComposition(
            catchUpRequest.Definition,
            catchUpState);
        Assert.True(catchUpComposition.IsValid, string.Join(',', catchUpComposition.Errors.Select(error => $"{error.Path}:{error.Code}")));

        var deferredRequest = ScheduleStoreTestData.CreateRequest("deferred-schedule");
        var deferredOccurrence = deferredRequest.InitialState.NextOccurrence!;
        Assert.True(ScheduleIdentityDerivation.TryDerive(
            deferredRequest.Definition.ScheduleId,
            deferredRequest.Definition.Revision,
            deferredRequest.CanonicalDefinitionHash,
            deferredOccurrence,
            out var deferredIdentity,
            out var identityValidation), string.Join(',', identityValidation.Errors.Select(error => $"{error.Path}:{error.Code}")));
        var deferredState = deferredRequest.InitialState with
        {
            DeferredOccurrence = new ScheduleDeferredOccurrence(
                ScheduleDeferredOccurrence.CurrentSchemaVersion,
                deferredOccurrence,
                deferredIdentity!,
                deferredOccurrence.ScheduledAtUtc.AddSeconds(1)),
            DispositionEvidence =
            [
                ScheduleContractTestData.Disposition(
                    deferredOccurrence.Ordinal,
                    disposition: ScheduleOccurrenceDisposition.OverlapDeferred,
                    firstScheduledLocal: deferredOccurrence.ScheduledLocal,
                    lastScheduledLocal: deferredOccurrence.ScheduledLocal,
                    firstScheduledAtUtc: deferredOccurrence.ScheduledAtUtc,
                    lastScheduledAtUtc: deferredOccurrence.ScheduledAtUtc),
            ],
        };
        var deferredComposition = ScheduleContractValidator.ValidateDefinitionStateComposition(
            deferredRequest.Definition,
            deferredState);
        Assert.True(deferredComposition.IsValid, string.Join(',', deferredComposition.Errors.Select(error => $"{error.Path}:{error.Code}")));

        Assert.Equal(
            ScheduleStoreMutationStatus.Applied,
            (await store.CreateAsync(catchUpRequest with { InitialState = catchUpState })).Status);
        Assert.Equal(
            ScheduleStoreMutationStatus.Applied,
            (await store.CreateAsync(deferredRequest with { InitialState = deferredState })).Status);

        var restarted = new ScheduleStore(paths);
        var catchUpRead = await restarted.ReadAsync(catchUpRequest.Definition.ScheduleId);
        var deferredRead = await restarted.ReadAsync(deferredRequest.Definition.ScheduleId);

        Assert.Equal(ScheduleStoreReadStatus.Found, catchUpRead.Status);
        Assert.Equal(catchUpState.CatchUpEpisode, catchUpRead.State!.CatchUpEpisode);
        AssertSameState(catchUpState, catchUpRead.State);
        Assert.Equal(ScheduleStoreReadStatus.Found, deferredRead.Status);
        Assert.Equal(deferredState.DeferredOccurrence, deferredRead.State!.DeferredOccurrence);
        Assert.Equal(deferredState.DispositionEvidence, deferredRead.State.DispositionEvidence);
        AssertSameState(deferredState, deferredRead.State);
    }

    [Fact]
    public async Task Read_missing_and_conflicting_create_return_closed_results()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ScheduleStore(paths);
        var request = ScheduleStoreTestData.CreateRequest();

        var missing = await store.ReadAsync(request.Definition.ScheduleId);
        var created = await store.CreateAsync(request);
        var conflictingDefinition = request.Definition with { Priority = SchedulePriority.Elevated };
        Assert.True(ScheduleContractHash.TryComputeDefinition(conflictingDefinition, out var conflictingHash, out _));
        var conflict = await store.CreateAsync(new(
            conflictingDefinition,
            request.InitialState with { DefinitionHash = conflictingHash! },
            conflictingHash!));

        Assert.Equal(ScheduleStoreReadStatus.NotFound, missing.Status);
        Assert.Null(missing.Definition);
        Assert.Null(missing.State);
        Assert.Equal(ScheduleStoreMutationStatus.Applied, created.Status);
        Assert.Equal(ScheduleStoreMutationStatus.Conflict, conflict.Status);
        Assert.Equal(request.InitialState, conflict.CurrentState);
    }

    [Fact]
    public async Task Whole_state_compare_exchange_is_optimistic_replayable_and_restart_safe()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ScheduleStore(paths);
        var request = ScheduleStoreTestData.CreateRequest();
        var replacement = ScheduleStoreTestData.Replacement(request.InitialState);
        var competing = ScheduleStoreTestData.Replacement(request.InitialState, 2);
        Assert.Equal(ScheduleStoreMutationStatus.Applied, (await store.CreateAsync(request)).Status);

        var applied = await store.CompareExchangeAsync(new ScheduleStateCompareExchange(request.InitialState, replacement));
        var replay = await new ScheduleStore(paths).CompareExchangeAsync(new ScheduleStateCompareExchange(request.InitialState, replacement));
        var conflict = await new ScheduleStore(paths).CompareExchangeAsync(new ScheduleStateCompareExchange(request.InitialState, competing));
        var read = await new ScheduleStore(paths).ReadAsync(request.Definition.ScheduleId);

        Assert.Equal(ScheduleStoreMutationStatus.Applied, applied.Status);
        Assert.Equal(replacement, applied.CurrentState);
        Assert.False(applied.ExactReplay);
        Assert.Equal(ScheduleStoreMutationStatus.Applied, replay.Status);
        Assert.Equal(replacement, replay.CurrentState);
        Assert.True(replay.ExactReplay);
        Assert.Equal(ScheduleStoreMutationStatus.Conflict, conflict.Status);
        Assert.Equal(replacement, conflict.CurrentState);
        Assert.False(conflict.ExactReplay);
        Assert.Equal(replacement, read.State);
    }

    [Fact]
    public async Task Unix_compare_exchange_recycles_authenticated_tombstones_beyond_the_durability_bound()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var options = new ScheduleStoreOptions { MaxDurabilityArtifacts = 1 };
        var request = ScheduleStoreTestData.CreateRequest();
        var current = request.InitialState;
        Assert.Equal(
            ScheduleStoreMutationStatus.Applied,
            (await new ScheduleStore(paths, options).CreateAsync(request)).Status);
        var interruptedCleanupContent = await File.ReadAllBytesAsync(LatestLedger(paths));

        var mutationCount = ScheduleStoreOptions.DefaultMaximumDurabilityArtifacts + 4;
        for (var mutation = 0; mutation < mutationCount; mutation++)
        {
            var replacement = ScheduleStoreTestData.Replacement(current);
            var result = await new ScheduleStore(paths, options)
                .CompareExchangeAsync(new ScheduleStateCompareExchange(current, replacement));

            Assert.Equal(ScheduleStoreMutationStatus.Applied, result.Status);
            Assert.Equal(replacement, result.CurrentState);
            current = replacement;
            if (mutation == 0)
            {
                // Model loss after the authenticated rename but before the old generation was zeroed.
                var interruptedTombstone = Assert.Single(
                    Directory.EnumerateFiles(StoreRoot(paths), ".tombstone-*.tmp"));
                await File.WriteAllBytesAsync(interruptedTombstone, interruptedCleanupContent);
            }
        }

        var read = await new ScheduleStore(paths, options).ReadAsync(request.Definition.ScheduleId);
        Assert.Equal(ScheduleStoreReadStatus.Found, read.Status);
        AssertSameState(current, read.State!);
        Assert.Single(Directory.EnumerateFiles(StoreRoot(paths), "ledger-*.json"));
        var tombstone = Assert.Single(Directory.EnumerateFiles(StoreRoot(paths), ".tombstone-*.tmp"));
        Assert.Equal(0, new FileInfo(tombstone).Length);
        Assert.DoesNotContain(Directory.EnumerateFiles(StoreRoot(paths)), path =>
            Path.GetFileName(path).StartsWith(".staged-", StringComparison.Ordinal)
            || Path.GetFileName(path).StartsWith(".discard-", StringComparison.Ordinal)
            || Path.GetFileName(path).StartsWith(".cleanup-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unix_recycled_tombstone_staging_interruption_preserves_exact_retry()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var options = new ScheduleStoreOptions { MaxDurabilityArtifacts = 1 };
        var request = ScheduleStoreTestData.CreateRequest();
        Assert.Equal(
            ScheduleStoreMutationStatus.Applied,
            (await new ScheduleStore(paths, options).CreateAsync(request)).Status);
        var current = ScheduleStoreTestData.Replacement(request.InitialState);
        Assert.Equal(
            ScheduleStoreMutationStatus.Applied,
            (await new ScheduleStore(paths, options).CompareExchangeAsync(new(request.InitialState, current))).Status);
        Assert.Single(Directory.EnumerateFiles(StoreRoot(paths), ".tombstone-*.tmp"));

        var replacement = ScheduleStoreTestData.Replacement(current);
        var interrupted = await new ScheduleStore(paths, new ScheduleStoreOptions
        {
            MaxDurabilityArtifacts = 1,
            DurableBoundaryObserver = boundary =>
            {
                if (boundary == ScheduleStorePersistenceBoundary.Staged)
                {
                    throw new IOException("simulated recycled staging interruption");
                }
            },
        }).CompareExchangeAsync(new(current, replacement));
        var retry = await new ScheduleStore(paths, options).CompareExchangeAsync(new(current, replacement));
        var read = await new ScheduleStore(paths, options).ReadAsync(request.Definition.ScheduleId);

        Assert.Equal(ScheduleStoreMutationStatus.Unavailable, interrupted.Status);
        Assert.Equal(ScheduleStoreMutationStatus.Applied, retry.Status);
        AssertSameState(replacement, read.State!);
        Assert.Single(Directory.EnumerateFiles(StoreRoot(paths), ".tombstone-*.tmp"));
    }

    [Fact]
    public async Task Unix_external_process_loss_after_recycled_tombstone_staging_preserves_exact_retry()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var options = new ScheduleStoreOptions { MaxDurabilityArtifacts = 1 };
        var request = ScheduleStoreTestData.CreateRequest("schedule-recycled-crash");
        var current = ScheduleStoreTestData.Replacement(request.InitialState);
        Assert.Equal(
            ScheduleStoreMutationStatus.Applied,
            (await new ScheduleStore(paths, options).CreateAsync(request)).Status);
        Assert.Equal(
            ScheduleStoreMutationStatus.Applied,
            (await new ScheduleStore(paths, options).CompareExchangeAsync(new(request.InitialState, current))).Status);
        Assert.Single(Directory.EnumerateFiles(StoreRoot(paths), ".tombstone-*.tmp"));

        var gate = workspace.File("release-recycled-schedule-host");
        var ready = workspace.File("recycled-schedule-host-ready");
        var output = workspace.File("recycled-schedule-host-output");
        using var process = StartCrossProcessHost(
            workspace.RootPath,
            gate,
            ready,
            output,
            request.Definition.ScheduleId.Value,
            ScheduleStorePersistenceBoundary.Staged,
            "compare-exchange-current",
            variant: 2,
            maxDurabilityArtifacts: 1);
        await WaitForPathAsync(ready);
        await File.WriteAllTextAsync(gate, "go");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotEqual(0, process.ExitCode);
        Assert.Single(Directory.EnumerateFiles(StoreRoot(paths), "ledger-*.json"));
        Assert.Single(Directory.EnumerateFiles(StoreRoot(paths), ".staged-*.tmp"));
        Assert.Empty(Directory.EnumerateFiles(StoreRoot(paths), ".tombstone-*.tmp"));

        var restarted = new ScheduleStore(paths, options);
        var recovered = await restarted.ReadAsync(request.Definition.ScheduleId);
        var replacement = ScheduleStoreTestData.Replacement(current, 2);
        var retry = await restarted.CompareExchangeAsync(new(current, replacement));
        var read = await new ScheduleStore(paths, options).ReadAsync(request.Definition.ScheduleId);

        Assert.Equal(ScheduleStoreReadStatus.Found, recovered.Status);
        AssertSameState(current, recovered.State!);
        Assert.Equal(ScheduleStoreMutationStatus.Applied, retry.Status);
        AssertSameState(replacement, retry.CurrentState!);
        Assert.Equal(ScheduleStoreReadStatus.Found, read.Status);
        AssertSameState(replacement, read.State!);
        Assert.Single(Directory.EnumerateFiles(StoreRoot(paths), "ledger-*.json"));
        var tombstone = Assert.Single(Directory.EnumerateFiles(StoreRoot(paths), ".tombstone-*.tmp"));
        Assert.Equal(0, new FileInfo(tombstone).Length);
        Assert.DoesNotContain(Directory.EnumerateFiles(StoreRoot(paths)), path =>
            Path.GetFileName(path).StartsWith(".ledger-", StringComparison.Ordinal)
            || Path.GetFileName(path).StartsWith(".staged-", StringComparison.Ordinal)
            || Path.GetFileName(path).StartsWith(".discard-", StringComparison.Ordinal)
            || Path.GetFileName(path).StartsWith(".cleanup-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_and_compare_exchange_enforce_contiguous_state_history()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ScheduleStore(paths);
        var request = ScheduleStoreTestData.CreateRequest();
        var nonInitial = request with
        {
            InitialState = request.InitialState with { StateRevision = 2 },
        };
        Assert.Equal(ScheduleStoreMutationStatus.Corrupt, (await store.CreateAsync(nonInitial)).Status);
        Assert.Equal(ScheduleStoreMutationStatus.Applied, (await store.CreateAsync(request)).Status);

        var sameRevision = request.InitialState with
        {
            LastClockObservedAtUtc = request.InitialState.LastClockObservedAtUtc!.Value.AddSeconds(1),
        };
        var jumpedRevision = sameRevision with { StateRevision = 3 };
        Assert.Equal(
            ScheduleStoreMutationStatus.Corrupt,
            (await store.CompareExchangeAsync(new(request.InitialState, sameRevision))).Status);
        Assert.Equal(
            ScheduleStoreMutationStatus.Corrupt,
            (await store.CompareExchangeAsync(new(request.InitialState, jumpedRevision))).Status);

        var read = await store.ReadAsync(request.Definition.ScheduleId);
        Assert.Equal(1, read.State!.StateRevision);
    }

    [Fact]
    public async Task Compare_exchange_rejects_shape_valid_destructive_state_successors()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ScheduleStore(paths);
        var request = ScheduleStoreTestData.CreateRequest(comprehensiveState: true);
        var current = request.InitialState;
        var later = current.LastClockObservedAtUtc!.Value.AddSeconds(1);
        Assert.Equal(ScheduleStoreMutationStatus.Applied, (await store.CreateAsync(request)).Status);

        var rewrittenDisposition = current.DispositionEvidence[0] with
        {
            ReasonCode = "rewritten-deferral",
        };
        var replacements = new[]
        {
            current with
            {
                StateRevision = 2,
                LastClockObservedAtUtc = later,
                DispositionEvidence = [],
            },
            current with
            {
                StateRevision = 2,
                LastClockObservedAtUtc = later,
                DispositionEvidence = [rewrittenDisposition],
            },
            current with
            {
                StateRevision = 2,
                LastClockObservedAtUtc = later,
                TerminalDeliveryEvidence = [],
            },
            current with
            {
                StateRevision = 2,
                LastClockObservedAtUtc = later,
                PendingDelivery = current.PendingDelivery! with
                {
                    Phase = SchedulePendingDeliveryPhase.Prepared,
                    Result = null,
                },
            },
        };

        foreach (var replacement in replacements)
        {
            var composition = ScheduleContractValidator.ValidateDefinitionStateComposition(
                request.Definition,
                replacement);
            Assert.True(composition.IsValid, string.Join(',', composition.Errors.Select(error => $"{error.Path}:{error.Code}")));
            var result = await store.CompareExchangeAsync(new(current, replacement));
            Assert.Equal(ScheduleStoreMutationStatus.Corrupt, result.Status);
            AssertSameState(current, result.CurrentState!);
        }

        var read = await store.ReadAsync(request.Definition.ScheduleId);
        AssertSameState(current, read.State!);
    }

    [Fact]
    public async Task Concurrent_store_instances_serialize_the_final_slot_and_state_cas()
    {
        using var createWorkspace = new TestWorkspace();
        var createPaths = new WorkspacePaths(createWorkspace.RootPath);
        var quota = new ScheduleStoreOptions { MaxSchedules = 1 };
        var createResults = await Task.WhenAll(
            new ScheduleStore(createPaths, quota).CreateAsync(ScheduleStoreTestData.CreateRequest("schedule-a")),
            new ScheduleStore(createPaths, quota).CreateAsync(ScheduleStoreTestData.CreateRequest("schedule-b")));
        Assert.Single(createResults, result => result.Status == ScheduleStoreMutationStatus.Applied);
        Assert.Single(createResults, result => result.Status == ScheduleStoreMutationStatus.Backpressured);

        using var casWorkspace = new TestWorkspace();
        var casPaths = new WorkspacePaths(casWorkspace.RootPath);
        var request = ScheduleStoreTestData.CreateRequest();
        await new ScheduleStore(casPaths).CreateAsync(request);
        var casResults = await Task.WhenAll(
            new ScheduleStore(casPaths).CompareExchangeAsync(new(request.InitialState, ScheduleStoreTestData.Replacement(request.InitialState, 1))),
            new ScheduleStore(casPaths).CompareExchangeAsync(new(request.InitialState, ScheduleStoreTestData.Replacement(request.InitialState, 2))));
        Assert.Single(casResults, result => result.Status == ScheduleStoreMutationStatus.Applied);
        Assert.Single(casResults, result => result.Status == ScheduleStoreMutationStatus.Conflict);
    }

    [Fact]
    public async Task Two_process_final_slot_and_exact_identity_races_have_one_durable_decision()
    {
        using var finalWorkspace = new TestWorkspace();
        var final = await RunCrossProcessRaceAsync(finalWorkspace.RootPath, "schedule-a", "schedule-b");
        Assert.Single(final, status => status == ScheduleStoreMutationStatus.Applied.ToString());
        Assert.Single(final, status => status == ScheduleStoreMutationStatus.Backpressured.ToString());

        using var replayWorkspace = new TestWorkspace();
        var replay = await RunCrossProcessRaceAsync(replayWorkspace.RootPath, "schedule-same", "schedule-same");
        Assert.Single(replay, status => status == ScheduleStoreMutationStatus.Applied.ToString());
        Assert.Single(replay, status => status == ScheduleStoreMutationStatus.AlreadyExists.ToString());
        var read = await new ScheduleStore(new WorkspacePaths(replayWorkspace.RootPath), RaceOptions())
            .ReadAsync(ScheduleStoreTestData.CreateRequest("schedule-same").Definition.ScheduleId);
        Assert.Equal(ScheduleStoreReadStatus.Found, read.Status);
    }

    [Fact]
    public async Task Two_process_whole_state_compare_exchange_has_one_durable_winner()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var request = ScheduleStoreTestData.CreateRequest();
        Assert.Equal(ScheduleStoreMutationStatus.Applied, (await new ScheduleStore(paths).CreateAsync(request)).Status);
        var results = await RunCrossProcessRaceAsync(
            workspace.RootPath,
            request.Definition.ScheduleId.Value,
            request.Definition.ScheduleId.Value,
            "compare-exchange");

        Assert.Single(results, status => status == ScheduleStoreMutationStatus.Applied.ToString());
        Assert.Single(results, status => status == ScheduleStoreMutationStatus.Conflict.ToString());
        var read = await new ScheduleStore(paths).ReadAsync(request.Definition.ScheduleId);
        Assert.Equal(2, read.State!.StateRevision);
    }

    [Fact]
    public async Task Cross_process_schedule_create_host()
    {
        var workspace = Environment.GetEnvironmentVariable(CrossProcessWorkspace);
        if (string.IsNullOrEmpty(workspace))
        {
            return;
        }

        var ready = Environment.GetEnvironmentVariable(CrossProcessReady)!;
        var gate = Environment.GetEnvironmentVariable(CrossProcessGate)!;
        var output = Environment.GetEnvironmentVariable(CrossProcessOutput)!;
        var scheduleId = Environment.GetEnvironmentVariable(CrossProcessScheduleId)!;
        await File.WriteAllTextAsync(ready, "ready");
        await WaitForPathAsync(gate);
        Action<ScheduleStorePersistenceBoundary>? observer = null;
        if (Enum.TryParse<ScheduleStorePersistenceBoundary>(
            Environment.GetEnvironmentVariable(CrossProcessCrashBoundary),
            out var crashBoundary))
        {
            observer = boundary =>
            {
                if (boundary == crashBoundary)
                {
                    TerminateCrossProcessHost();
                }
            };
        }

        var options = new ScheduleStoreOptions
        {
            MaxSchedules = 1,
            MaxDurabilityArtifacts = int.TryParse(
                Environment.GetEnvironmentVariable(CrossProcessMaxDurabilityArtifacts),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var maxDurabilityArtifacts)
                    ? maxDurabilityArtifacts
                    : ScheduleStoreOptions.DefaultMaximumDurabilityArtifacts,
            DurableBoundaryObserver = observer,
        };
        var store = new ScheduleStore(new WorkspacePaths(workspace), options);
        var request = ScheduleStoreTestData.CreateRequest(scheduleId);
        ScheduleStoreMutationResult result;
        if (string.Equals(
                Environment.GetEnvironmentVariable(CrossProcessOperation),
                "compare-exchange-current",
                StringComparison.Ordinal))
        {
            var read = await store.ReadAsync(request.Definition.ScheduleId);
            Assert.Equal(ScheduleStoreReadStatus.Found, read.Status);
            var variant = int.Parse(
                Environment.GetEnvironmentVariable(CrossProcessVariant)!,
                System.Globalization.CultureInfo.InvariantCulture);
            result = await store.CompareExchangeAsync(new(
                read.State!,
                ScheduleStoreTestData.Replacement(read.State!, variant)));
        }
        else if (string.Equals(
                Environment.GetEnvironmentVariable(CrossProcessOperation),
                "compare-exchange",
                StringComparison.Ordinal))
        {
            var variant = int.Parse(
                Environment.GetEnvironmentVariable(CrossProcessVariant)!,
                System.Globalization.CultureInfo.InvariantCulture);
            result = await store.CompareExchangeAsync(new(
                request.InitialState,
                ScheduleStoreTestData.Replacement(request.InitialState, variant)));
        }
        else
        {
            result = await store.CreateAsync(request);
        }

        await File.WriteAllTextAsync(output, result.Status.ToString());
    }

    [Fact]
    public async Task External_process_loss_after_staging_recovers_without_partial_publication()
    {
        using var workspace = new TestWorkspace();
        var gate = workspace.File("release-schedule-host");
        var ready = workspace.File("schedule-host-ready");
        var output = workspace.File("schedule-host-output");
        using var process = StartCrossProcessHost(
            workspace.RootPath,
            gate,
            ready,
            output,
            "schedule-crash",
            ScheduleStorePersistenceBoundary.Staged);
        await WaitForPathAsync(ready);
        await File.WriteAllTextAsync(gate, "go");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual(0, process.ExitCode);

        var paths = new WorkspacePaths(workspace.RootPath);
        var request = ScheduleStoreTestData.CreateRequest("schedule-crash");
        var recovered = await new ScheduleStore(paths, RaceOptions()).CreateAsync(request);
        var read = await new ScheduleStore(paths, RaceOptions()).ReadAsync(request.Definition.ScheduleId);

        Assert.Equal(ScheduleStoreMutationStatus.Applied, recovered.Status);
        Assert.Equal(ScheduleStoreReadStatus.Found, read.Status);
        Assert.DoesNotContain(Directory.EnumerateFiles(StoreRoot(paths)), path =>
            Path.GetFileName(path).StartsWith(".staged-", StringComparison.Ordinal)
            || Path.GetFileName(path).StartsWith(".discard-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ScheduleStorePersistenceBoundary.PrecursorCreated, ScheduleStoreMutationStatus.Applied)]
    [InlineData(ScheduleStorePersistenceBoundary.Staged, ScheduleStoreMutationStatus.Applied)]
    [InlineData(ScheduleStorePersistenceBoundary.Publishing, ScheduleStoreMutationStatus.Applied)]
    [InlineData(ScheduleStorePersistenceBoundary.Published, ScheduleStoreMutationStatus.AlreadyExists)]
    public async Task Boundary_interruption_is_unavailable_and_exact_retry_resolves_durable_outcome(
        ScheduleStorePersistenceBoundary boundary,
        ScheduleStoreMutationStatus retryStatus)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var request = ScheduleStoreTestData.CreateRequest();
        var interrupted = new ScheduleStore(paths, new ScheduleStoreOptions
        {
            DurableBoundaryObserver = observed =>
            {
                if (observed == boundary)
                {
                    throw new IOException("simulated abrupt boundary interruption");
                }
            },
        });

        var first = await interrupted.CreateAsync(request);
        var retry = await new ScheduleStore(paths).CreateAsync(request);
        var read = await new ScheduleStore(paths).ReadAsync(request.Definition.ScheduleId);

        Assert.Equal(ScheduleStoreMutationStatus.Unavailable, first.Status);
        Assert.Equal(retryStatus, retry.Status);
        Assert.Equal(ScheduleStoreReadStatus.Found, read.Status);
    }

    [Fact]
    public async Task Cancellation_is_honored_before_commit_and_ignored_after_staging_begins()
    {
        using var canceledWorkspace = new TestWorkspace();
        using var preCanceled = new CancellationTokenSource();
        preCanceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new ScheduleStore(new WorkspacePaths(canceledWorkspace.RootPath))
                .CreateAsync(ScheduleStoreTestData.CreateRequest(), preCanceled.Token));

        using var committingWorkspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var paths = new WorkspacePaths(committingWorkspace.RootPath);
        var request = ScheduleStoreTestData.CreateRequest();
        var store = new ScheduleStore(paths, new ScheduleStoreOptions
        {
            DurableBoundaryObserver = boundary =>
            {
                if (boundary == ScheduleStorePersistenceBoundary.PrecursorCreated)
                {
                    cancellation.Cancel();
                }
            },
        });

        var result = await store.CreateAsync(request, cancellation.Token);
        Assert.Equal(ScheduleStoreMutationStatus.Applied, result.Status);
        Assert.Equal(ScheduleStoreReadStatus.Found, (await new ScheduleStore(paths).ReadAsync(request.Definition.ScheduleId)).Status);
    }

    [Fact]
    public async Task Malformed_non_arrays_are_corrupt_while_real_count_and_byte_limits_backpressure()
    {
        foreach (var property in new[] { "dispositionEvidence", "terminalDeliveryEvidence" })
        {
            using var malformedWorkspace = new TestWorkspace();
            var malformedPaths = new WorkspacePaths(malformedWorkspace.RootPath);
            var request = ScheduleStoreTestData.CreateRequest(comprehensiveState: true);
            await new ScheduleStore(malformedPaths).CreateAsync(request);
            RewriteLatest(malformedPaths, root =>
            {
                var state = (JsonObject)((JsonObject)((JsonArray)root["entries"]!)[0]!)["state"]!;
                state[property] = new JsonObject();
            });
            var malformed = await new ScheduleStore(malformedPaths).ReadAsync(request.Definition.ScheduleId);
            var provenance = await new ScheduleStore(malformedPaths).ResolveAsync(request.InitialState.PendingDelivery!.Prepared!.Envelope);
            Assert.Equal(ScheduleStoreReadStatus.Corrupt, malformed.Status);
            Assert.Equal(ScheduleDeliveryProvenanceStatus.Corrupt, provenance.Status);
        }

        using var countWorkspace = new TestWorkspace();
        var countPaths = new WorkspacePaths(countWorkspace.RootPath);
        await new ScheduleStore(countPaths).CreateAsync(ScheduleStoreTestData.CreateRequest("schedule-a"));
        await new ScheduleStore(countPaths).CreateAsync(ScheduleStoreTestData.CreateRequest("schedule-b"));
        var countLimited = await new ScheduleStore(countPaths, new ScheduleStoreOptions { MaxSchedules = 1 })
            .ReadAsync(ScheduleStoreTestData.CreateRequest("schedule-a").Definition.ScheduleId);
        var countLimitedProvenance = await new ScheduleStore(countPaths, new ScheduleStoreOptions { MaxSchedules = 1 })
            .ResolveAsync(ScheduleContractTestData.Prepared().Envelope);
        Assert.Equal(ScheduleStoreReadStatus.Backpressured, countLimited.Status);
        Assert.Equal(ScheduleDeliveryProvenanceStatus.Backpressured, countLimitedProvenance.Status);

        using var byteWorkspace = new TestWorkspace();
        var bytePaths = new WorkspacePaths(byteWorkspace.RootPath);
        var bytes = await new ScheduleStore(bytePaths, new ScheduleStoreOptions { MaxCatalogUtf8Bytes = 128 })
            .CreateAsync(ScheduleStoreTestData.CreateRequest());
        Assert.Equal(ScheduleStoreMutationStatus.Backpressured, bytes.Status);
    }

    [Theory]
    [InlineData("dispositionEvidence", ScheduleContractLimits.MaxDispositionEvidenceItems)]
    [InlineData("terminalDeliveryEvidence", ScheduleContractLimits.MaxTerminalDeliveryEvidenceItems)]
    public async Task Schema_one_evidence_collection_bounds_report_backpressure(
        string property,
        int maximum)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var request = ScheduleStoreTestData.CreateRequest(comprehensiveState: true);
        Assert.Equal(ScheduleStoreMutationStatus.Applied, (await new ScheduleStore(paths).CreateAsync(request)).Status);
        RewriteLatest(paths, root =>
        {
            var state = (JsonObject)((JsonObject)((JsonArray)root["entries"]!)[0]!)["state"]!;
            var evidence = (JsonArray)state[property]!;
            var seed = evidence[0]!.DeepClone();
            while (evidence.Count <= maximum)
            {
                evidence.Add(seed.DeepClone());
            }
        });

        var read = await new ScheduleStore(paths).ReadAsync(request.Definition.ScheduleId);

        Assert.Equal(ScheduleStoreReadStatus.Backpressured, read.Status);
        Assert.Null(read.Definition);
        Assert.Null(read.State);
    }

    [Theory]
    [InlineData("fixed-interval")]
    [InlineData("last-clock")]
    public async Task Noncanonical_nullable_schedule_scalars_are_corrupt(string mutation)
    {
        await AssertCorruptMutationAsync(bytes => MutateJson(bytes, root =>
        {
            var entry = (JsonObject)((JsonArray)root["entries"]!)[0]!;
            if (mutation == "fixed-interval")
            {
                var definition = (JsonObject)entry["definition"]!;
                ((JsonObject)definition["recurrence"]!)["fixedIntervalSeconds"] = "60";
            }
            else
            {
                ((JsonObject)entry["state"]!)["lastClockObservedAtUtc"] = 1;
            }
        }));
    }

    [Fact]
    public async Task Invalid_identity_missing_exchange_and_generation_substitution_fail_closed()
    {
        using var emptyWorkspace = new TestWorkspace();
        var emptyPaths = new WorkspacePaths(emptyWorkspace.RootPath);
        var request = ScheduleStoreTestData.CreateRequest();
        var missingExchange = await new ScheduleStore(emptyPaths).CompareExchangeAsync(new(
            request.InitialState,
            ScheduleStoreTestData.Replacement(request.InitialState)));
        var invalidIdentity = await new ScheduleStore(emptyPaths).ReadAsync((ScheduleId)null!);

        Assert.Equal(ScheduleStoreMutationStatus.Conflict, missingExchange.Status);
        Assert.Null(missingExchange.CurrentState);
        Assert.Equal(ScheduleStoreReadStatus.Corrupt, invalidIdentity.Status);

        using var generationWorkspace = new TestWorkspace();
        var generationPaths = new WorkspacePaths(generationWorkspace.RootPath);
        Assert.Equal(
            ScheduleStoreMutationStatus.Applied,
            (await new ScheduleStore(generationPaths).CreateAsync(request)).Status);
        RewriteLatest(generationPaths, root => root["generation"] = root["generation"]!.GetValue<long>() + 1);

        var substituted = await new ScheduleStore(generationPaths).ReadAsync(request.Definition.ScheduleId);

        Assert.Equal(ScheduleStoreReadStatus.Corrupt, substituted.Status);
        Assert.Null(substituted.Definition);
        Assert.Null(substituted.State);
    }

    [Fact]
    public async Task Noncanonical_duplicate_unsupported_and_bom_catalogs_fail_closed()
    {
        await AssertCorruptMutationAsync(bytes => [.. bytes, (byte)' ']);
        await AssertCorruptMutationAsync(bytes => [0xEF, 0xBB, 0xBF, .. bytes]);
        await AssertCorruptMutationAsync(bytes => MutateJson(bytes, root => root["schemaVersion"] = 2));
        await AssertCorruptMutationAsync(bytes => MutateJson(bytes, root =>
        {
            var entries = (JsonArray)root["entries"]!;
            entries.Add(entries[0]!.DeepClone());
        }));
    }

    [Theory]
    [InlineData("pending-overlap")]
    [InlineData("terminal-current")]
    [InlineData("terminal-recurrence")]
    [InlineData("terminal-overlap")]
    [InlineData("overlap-decision-missing")]
    [InlineData("overlap-decision-noncanonical")]
    public async Task Missing_or_noncanonical_retained_proof_hashes_fail_closed(string mutation)
    {
        await AssertCorruptMutationAsync(bytes => MutateJson(bytes, root =>
        {
            var state = (JsonObject)((JsonObject)((JsonArray)root["entries"]!)[0]!)["state"]!;
            var pending = (JsonObject)state["pendingDelivery"]!;
            var terminal = (JsonObject)((JsonArray)state["terminalDeliveryEvidence"]!)[0]!;
            switch (mutation)
            {
                case "pending-overlap":
                    pending.Remove("overlapEvidenceHash");
                    break;
                case "terminal-current":
                    terminal["currentEvidenceHash"] = new string('F', ScheduleContractLimits.Sha256HexCharacters);
                    break;
                case "terminal-recurrence":
                    terminal.Remove("recurrenceProofHash");
                    break;
                case "terminal-overlap":
                    terminal["overlapEvidenceHash"] = "not-a-proof-hash";
                    break;
                case "overlap-decision-missing":
                    ((JsonObject)((JsonArray)state["dispositionEvidence"]!)[0]!).Remove("decisionEvidenceHash");
                    break;
                case "overlap-decision-noncanonical":
                    ((JsonObject)((JsonArray)state["dispositionEvidence"]!)[0]!)["decisionEvidenceHash"] =
                        new string('A', ScheduleContractLimits.Sha256HexCharacters);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        }));
    }

    [Fact]
    public async Task Malformed_proposals_and_definition_state_mismatches_never_publish()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var request = ScheduleStoreTestData.CreateRequest();
        var invalidTarget = request with { Definition = request.Definition with { Target = null! } };
        var wrongHash = request with { CanonicalDefinitionHash = new string('0', 64) };
        var mismatchedState = request with
        {
            InitialState = request.InitialState with { DefinitionHash = new string('1', 64) },
        };

        Assert.Equal(ScheduleStoreMutationStatus.Corrupt, (await new ScheduleStore(paths).CreateAsync(invalidTarget)).Status);
        Assert.Equal(ScheduleStoreMutationStatus.Corrupt, (await new ScheduleStore(paths).CreateAsync(wrongHash)).Status);
        Assert.Equal(ScheduleStoreMutationStatus.Corrupt, (await new ScheduleStore(paths).CreateAsync(mismatchedState)).Status);
        Assert.False(Directory.Exists(StoreRoot(paths)));
    }

    [Fact]
    public async Task Schedule_count_quota_is_workspace_scoped_and_does_not_cross_tenants()
    {
        using var first = new TestWorkspace();
        using var second = new TestWorkspace();
        var options = new ScheduleStoreOptions { MaxSchedules = 1 };
        var request = ScheduleStoreTestData.CreateRequest("shared-schedule");

        Assert.Equal(ScheduleStoreMutationStatus.Applied, (await new ScheduleStore(new WorkspacePaths(first.RootPath), options).CreateAsync(request)).Status);
        Assert.Equal(ScheduleStoreMutationStatus.Applied, (await new ScheduleStore(new WorkspacePaths(second.RootPath), options).CreateAsync(request)).Status);
        Assert.Equal(
            ScheduleStoreMutationStatus.Backpressured,
            (await new ScheduleStore(new WorkspacePaths(first.RootPath), options).CreateAsync(ScheduleStoreTestData.CreateRequest("second-schedule"))).Status);
    }

    [Fact]
    public void Invalid_configuration_bounds_are_rejected_before_filesystem_access()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduleStore(paths, new ScheduleStoreOptions { MaxSchedules = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduleStore(paths, new ScheduleStoreOptions { MaxCatalogUtf8Bytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduleStore(paths, new ScheduleStoreOptions { MaxDurabilityArtifacts = 0 }));
        Assert.False(Directory.Exists(StoreRoot(paths)));
    }

    [Fact]
    public async Task Unix_symlink_fifo_hard_link_and_root_swap_substitutions_fail_closed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var symlinkWorkspace = new TestWorkspace();
        var symlinkPaths = new WorkspacePaths(symlinkWorkspace.RootPath);
        var outside = symlinkWorkspace.File("outside-schedules");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(symlinkPaths.AgentPath);
        File.CreateSymbolicLink(symlinkPaths.AgentFile("triggers"), outside);
        var symlinkResult = await new ScheduleStore(symlinkPaths).CreateAsync(ScheduleStoreTestData.CreateRequest());
        Assert.Equal(ScheduleStoreMutationStatus.Corrupt, symlinkResult.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));

        using var fifoWorkspace = new TestWorkspace();
        var fifoPaths = new WorkspacePaths(fifoWorkspace.RootPath);
        Directory.CreateDirectory(StoreRoot(fifoPaths));
        Assert.Equal(0, MkFifo(Path.Combine(StoreRoot(fifoPaths), "ledger-0000000000000000001.json"), Convert.ToUInt32("600", 8)));
        var fifoResult = await new ScheduleStore(fifoPaths).CreateAsync(ScheduleStoreTestData.CreateRequest());
        Assert.Equal(ScheduleStoreMutationStatus.Corrupt, fifoResult.Status);

        using var hardLinkWorkspace = new TestWorkspace();
        var hardLinkPaths = new WorkspacePaths(hardLinkWorkspace.RootPath);
        var hardLinkRequest = ScheduleStoreTestData.CreateRequest();
        await new ScheduleStore(hardLinkPaths).CreateAsync(hardLinkRequest);
        var ledger = LatestLedger(hardLinkPaths);
        Assert.Equal(0, Link(ledger, hardLinkWorkspace.File("linked-ledger")));
        var hardLinkResult = await new ScheduleStore(hardLinkPaths).ReadAsync(hardLinkRequest.Definition.ScheduleId);
        Assert.Equal(ScheduleStoreReadStatus.Corrupt, hardLinkResult.Status);

        using var swapWorkspace = new TestWorkspace();
        var swapPaths = new WorkspacePaths(swapWorkspace.RootPath);
        var swapRoot = StoreRoot(swapPaths);
        var movedRoot = swapRoot + "-moved";
        var swapping = new ScheduleStore(swapPaths, new ScheduleStoreOptions
        {
            DurableBoundaryObserver = boundary =>
            {
                if (boundary == ScheduleStorePersistenceBoundary.Publishing)
                {
                    Directory.Move(swapRoot, movedRoot);
                    Directory.CreateDirectory(swapRoot);
                    File.WriteAllText(Path.Combine(swapRoot, "sentinel"), "untouched");
                }
            },
        });
        var swapResult = await swapping.CreateAsync(ScheduleStoreTestData.CreateRequest());
        Assert.Equal(ScheduleStoreMutationStatus.Corrupt, swapResult.Status);
        Assert.Equal("untouched", await File.ReadAllTextAsync(Path.Combine(swapRoot, "sentinel")));
        Assert.Empty(Directory.EnumerateFiles(movedRoot, "ledger-*.json"));
    }

    private static TriggerDeliveryEnvelope ScheduledEnvelope(
        TriggerDeliveryEnvelope source,
        TriggerPayloadEvidence payload,
        ScheduleExecutionDirective? directive = null)
    {
        directive ??= source.ScheduleExecutionDirective!;
        Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(
            source.Redelivery.Attempt,
            source.Redelivery.Count,
            directive.Identity.DeliveryId,
            out var redelivery,
            out _));
        Assert.True(TriggerDeliveryFactory.TryCreateScheduledEnvelope(
            source.SchemaVersion,
            directive.Identity.DeliveryId,
            directive.Identity.DeduplicationId,
            source.Adapter,
            source.Loop,
            source.ActorContext,
            source.Authority,
            source.Temporal,
            payload,
            redelivery,
            directive,
            source.PublicationRequested,
            source.InvokingConversation,
            source.VisibleStatus,
            source.VisibleReason,
            out var envelope,
            out var validation),
            string.Join(',', validation.Errors.Select(error => $"{error.Field}:{error.Code}")));
        return envelope!;
    }

    private static async Task AssertCorruptMutationAsync(Func<byte[], byte[]> mutation)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var request = ScheduleStoreTestData.CreateRequest(comprehensiveState: true);
        await new ScheduleStore(paths).CreateAsync(request);
        var ledger = LatestLedger(paths);
        await File.WriteAllBytesAsync(ledger, mutation(await File.ReadAllBytesAsync(ledger)));
        Assert.Equal(ScheduleStoreReadStatus.Corrupt, (await new ScheduleStore(paths).ReadAsync(request.Definition.ScheduleId)).Status);
    }

    private static void AssertSameState(ScheduleState expected, ScheduleState actual)
    {
        Assert.True(ScheduleContractHash.TryComputeState(expected, out var expectedHash, out _));
        Assert.True(ScheduleContractHash.TryComputeState(actual, out var actualHash, out _));
        Assert.Equal(expectedHash, actualHash);
    }

    private static void RewriteLatest(WorkspacePaths paths, Action<JsonObject> mutation)
    {
        var ledger = LatestLedger(paths);
        File.WriteAllBytes(ledger, MutateJson(File.ReadAllBytes(ledger), mutation));
    }

    private static byte[] MutateJson(byte[] bytes, Action<JsonObject> mutation)
    {
        var root = JsonNode.Parse(bytes)!.AsObject();
        mutation(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static string StoreRoot(WorkspacePaths paths)
        => paths.AgentFile(Path.Combine("triggers", "schedules"));

    private static string LatestLedger(WorkspacePaths paths)
        => Directory.EnumerateFiles(StoreRoot(paths), "ledger-*.json").Order(StringComparer.Ordinal).Last();

    private static ScheduleStoreOptions RaceOptions()
        => new() { MaxSchedules = 1 };

    private static async Task<string[]> RunCrossProcessRaceAsync(
        string workspace,
        string firstSchedule,
        string secondSchedule,
        string operation = "create")
    {
        var gate = Path.Combine(workspace, "release-schedule-hosts");
        var firstReady = Path.Combine(workspace, "first-schedule-ready");
        var secondReady = Path.Combine(workspace, "second-schedule-ready");
        var firstOutput = Path.Combine(workspace, "first-schedule-output");
        var secondOutput = Path.Combine(workspace, "second-schedule-output");
        using var first = StartCrossProcessHost(workspace, gate, firstReady, firstOutput, firstSchedule, operation: operation, variant: 1);
        using var second = StartCrossProcessHost(workspace, gate, secondReady, secondOutput, secondSchedule, operation: operation, variant: 2);
        await Task.WhenAll(WaitForPathAsync(firstReady), WaitForPathAsync(secondReady));
        await File.WriteAllTextAsync(gate, "go");
        await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(30));
        await AssertProcessSucceededAsync(first);
        await AssertProcessSucceededAsync(second);
        return [await File.ReadAllTextAsync(firstOutput), await File.ReadAllTextAsync(secondOutput)];
    }

    private static Process StartCrossProcessHost(
        string workspace,
        string gate,
        string ready,
        string output,
        string scheduleId,
        ScheduleStorePersistenceBoundary? crashBoundary = null,
        string operation = "create",
        int variant = 1,
        int? maxDurabilityArtifacts = null)
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
            typeof(ScheduleStoreTests).Assembly.Location,
            "EmbodySense.Core.Persistence.Tests.Triggers.Schedules.ScheduleStoreTests.Cross_process_schedule_create_host");
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment[CrossProcessWorkspace] = workspace;
        startInfo.Environment[CrossProcessGate] = gate;
        startInfo.Environment[CrossProcessReady] = ready;
        startInfo.Environment[CrossProcessOutput] = output;
        startInfo.Environment[CrossProcessScheduleId] = scheduleId;
        startInfo.Environment[CrossProcessOperation] = operation;
        startInfo.Environment[CrossProcessVariant] = variant.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (maxDurabilityArtifacts is not null)
        {
            startInfo.Environment[CrossProcessMaxDurabilityArtifacts] = maxDurabilityArtifacts.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        if (crashBoundary is not null)
        {
            startInfo.Environment[CrossProcessCrashBoundary] = crashBoundary.Value.ToString();
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Cross-process schedule-store test host did not start.");
    }

    private static async Task WaitForPathAsync(string path)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.True(wait.Elapsed < _crossProcessReadinessTimeout, $"Cross-process schedule host did not create `{path}`.");
            await Task.Delay(10);
        }
    }

    private static async Task AssertProcessSucceededAsync(Process process)
    {
        var standardError = await process.StandardError.ReadToEndAsync();
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, standardError + Environment.NewLine + standardOutput);
    }

    private static void TerminateCrossProcessHost()
    {
        Process.GetCurrentProcess().Kill();
        Thread.Sleep(Timeout.Infinite);
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existingPath, string newPath);
}
