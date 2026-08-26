using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Startup.Tests.Triggers.Schedules;
using EmbodySense.Core.Startup.Triggers;
using EmbodySense.Core.Startup.Triggers.Models;

namespace EmbodySense.Core.Startup.Tests.Triggers;

public sealed class TriggerWorkerCurrentEvidenceAuthorizerTests
{
    private const string TriggerWorkspaceId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Exact_unchanged_evidence_is_authorized_under_one_fence_and_has_a_stable_retained_overlap_proof()
    {
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var authorizer = Authorizer(context);
        var input = Input(context);

        var first = await authorizer.AuthorizeAsync(input, ScheduleCurrentEvidenceTestContext.ObservedAtUtc);
        var second = await authorizer.AuthorizeAsync(input, ScheduleCurrentEvidenceTestContext.ObservedAtUtc.AddMilliseconds(1));

        Assert.Equal("Authorized", first.Status);
        Assert.Equal("Authorized", second.Status);
        Assert.Matches("^[0-9a-f]{64}$", first.EvidenceHash);
        Assert.Equal(first.EvidenceHash, second.EvidenceHash);
        Assert.True(context.AllReadsInsideFence);
        Assert.Equal(2, context.FenceCount);
        Assert.Equal(2, context.TargetReadCount);
        Assert.Equal(2, context.GrantReadCount);
        Assert.Equal(2, context.ProfileReadCount);
        Assert.Equal(2, context.CatalogReadCount);
    }

    [Fact]
    public async Task Catalog_lifecycle_transition_changes_the_retained_overlap_proof_without_binding_evaluation_time()
    {
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var authorizer = Authorizer(context);
        var input = Input(context);
        var first = await authorizer.AuthorizeAsync(input, ScheduleCurrentEvidenceTestContext.ObservedAtUtc);
        var transitionedEntry = context.CatalogEntry with
        {
            Revision = 2,
            UpdatedAtUtc = ScheduleCurrentEvidenceTestContext.ObservedAtUtc.AddMinutes(-30),
            LastOperationId = "reenable-schedule-adapter",
        };
        context.CatalogRead = new CapabilityCatalogReadResult(
            CapabilityCatalogReadStatus.Available,
            new CapabilityCatalogPage(2, [transitionedEntry], null),
            "available after an enablement transition");

        var transitioned = await authorizer.AuthorizeAsync(input, ScheduleCurrentEvidenceTestContext.ObservedAtUtc.AddMilliseconds(1));
        context.CatalogRead = new CapabilityCatalogReadResult(
            CapabilityCatalogReadStatus.Available,
            new CapabilityCatalogPage(2, [transitionedEntry with
            {
                Lifecycle = transitionedEntry.Lifecycle with { Retirement = CapabilityRetirementState.Deprecated },
            }], null),
            "available after a lifecycle transition");

        var lifecycleTransition = await authorizer.AuthorizeAsync(input, ScheduleCurrentEvidenceTestContext.ObservedAtUtc.AddMilliseconds(2));
        var retained = await authorizer.AuthorizeAsync(input, ScheduleCurrentEvidenceTestContext.ObservedAtUtc.AddMilliseconds(3));

        Assert.Equal("Authorized", first.Status);
        Assert.Equal("Authorized", transitioned.Status);
        Assert.Equal("Authorized", lifecycleTransition.Status);
        Assert.Equal("Authorized", retained.Status);
        Assert.NotEqual(first.EvidenceHash, transitioned.EvidenceHash);
        Assert.NotEqual(transitioned.EvidenceHash, lifecycleTransition.EvidenceHash);
        Assert.Equal(lifecycleTransition.EvidenceHash, retained.EvidenceHash);
    }

    [Fact]
    public async Task Disabled_exact_adapter_is_rejected_without_fabricating_an_authorization_proof()
    {
        var context = ScheduleCurrentEvidenceTestContext.Create();
        context.CatalogRead = context.AvailableCatalog(context.CatalogEntry with
        {
            Lifecycle = context.CatalogEntry.Lifecycle with { Enablement = CapabilityEnablementState.Disabled },
        });

        var response = await Authorizer(context).AuthorizeAsync(Input(context), ScheduleCurrentEvidenceTestContext.ObservedAtUtc);

        Assert.Equal("Rejected", response.Status);
        Assert.Equal(new string('0', 64), response.EvidenceHash);
        Assert.True(context.AllReadsInsideFence);
    }

    [Fact]
    public async Task Unknown_adapter_retirement_posture_is_unavailable_without_an_authorization_proof()
    {
        var context = ScheduleCurrentEvidenceTestContext.Create();
        context.CatalogRead = context.AvailableCatalog(context.CatalogEntry with
        {
            Lifecycle = context.CatalogEntry.Lifecycle with { Retirement = CapabilityRetirementState.Unknown },
        });

        var response = await Authorizer(context).AuthorizeAsync(Input(context), ScheduleCurrentEvidenceTestContext.ObservedAtUtc);

        Assert.Equal("Unavailable", response.Status);
        Assert.Equal(new string('0', 64), response.EvidenceHash);
    }

    [Fact]
    public async Task A_mutation_between_fenced_publication_and_grant_reads_cannot_authorize_a_mixed_snapshot()
    {
        var context = ScheduleCurrentEvidenceTestContext.Create();
        context.BeforeGrantResolve = () => context.GrantResolution = context.GrantResolution with
        {
            EffectiveCeiling = new AuthorityCeiling([], [], 0, CapabilitySideEffectClass.None, true, false, false),
        };

        var response = await Authorizer(context).AuthorizeAsync(Input(context), ScheduleCurrentEvidenceTestContext.ObservedAtUtc);

        Assert.Equal("Unavailable", response.Status);
        Assert.Equal(new string('0', 64), response.EvidenceHash);
        Assert.True(context.AllReadsInsideFence);
        Assert.Equal(1, context.FenceCount);
    }

    [Fact]
    public async Task Malformed_catalog_cursor_and_source_exception_are_unavailable()
    {
        var cursorContext = ScheduleCurrentEvidenceTestContext.Create();
        cursorContext.CatalogRead = new CapabilityCatalogReadResult(
            CapabilityCatalogReadStatus.Available,
            new CapabilityCatalogPage(1, [cursorContext.CatalogEntry], "not-a-capability-id"),
            "malformed cursor");
        var sourceContext = ScheduleCurrentEvidenceTestContext.Create();
        sourceContext.GrantFailure = new IOException("grant source unavailable");

        var cursor = await Authorizer(cursorContext).AuthorizeAsync(Input(cursorContext), ScheduleCurrentEvidenceTestContext.ObservedAtUtc);
        var source = await Authorizer(sourceContext).AuthorizeAsync(Input(sourceContext), ScheduleCurrentEvidenceTestContext.ObservedAtUtc);

        Assert.Equal("Unavailable", cursor.Status);
        Assert.Equal("Unavailable", source.Status);
        Assert.Equal(new string('0', 64), cursor.EvidenceHash);
        Assert.Equal(new string('0', 64), source.EvidenceHash);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("unavailable")]
    public async Task Real_trigger_worker_never_writes_intent_or_invokes_provider_when_current_evidence_is_not_authorized(string posture)
    {
        var context = ScheduleCurrentEvidenceTestContext.Create();
        if (posture == "denied")
        {
            context.CatalogRead = context.AvailableCatalog(context.CatalogEntry with
            {
                Lifecycle = context.CatalogEntry.Lifecycle with { Enablement = CapabilityEnablementState.Disabled },
            });
        }
        else
        {
            context.GrantFailure = new IOException("authority source unavailable");
        }

        var observedAtUtc = ScheduleCurrentEvidenceTestContext.GrantEvaluatedAtUtc.AddSeconds(1);
        var envelope = Envelope(context, observedAtUtc);
        var harness = new TriggerWorkerCurrentEvidenceWorkerHarness(envelope, observedAtUtc);
        var worker = new TriggerWorkerService(
            harness,
            new TriggerWorkerCurrentEvidenceAuthorizerAdapter(Authorizer(context)),
            harness,
            harness,
            new FixedTimeProvider(observedAtUtc));

        var result = await worker.RunOnceAsync(new TriggerWorkerRunRequest(new TriggerWorkerSelectionRequest("worker-1", 1, observedAtUtc, TimeSpan.FromMinutes(1), [], 2)));

        Assert.Equal(TriggerWorkerSelectionStatus.Acquired, result.SelectionStatus);
        Assert.Equal(TriggerWorkerMutationStatus.Committed, result.MutationStatus);
        Assert.Equal(0, harness.DurableIntentWrites);
        Assert.Equal(0, harness.ProviderCalls);
        Assert.Equal(1, harness.RejectionWrites);
    }

    [Fact]
    public async Task Cancellation_during_catalog_read_releases_the_selected_delivery_without_a_terminal_rejection()
    {
        var context = ScheduleCurrentEvidenceTestContext.Create();
        using var cancellation = new CancellationTokenSource();
        context.BeforeCatalogRead = cancellation.Cancel;
        var observedAtUtc = ScheduleCurrentEvidenceTestContext.GrantEvaluatedAtUtc.AddSeconds(1);
        var envelope = Envelope(context, observedAtUtc);
        var harness = new TriggerWorkerCurrentEvidenceWorkerHarness(envelope, observedAtUtc);
        var worker = new TriggerWorkerService(
            harness,
            new TriggerWorkerCurrentEvidenceAuthorizerAdapter(Authorizer(context)),
            harness,
            harness,
            new FixedTimeProvider(observedAtUtc));

        var result = await worker.RunOnceAsync(new TriggerWorkerRunRequest(new TriggerWorkerSelectionRequest("worker-1", 1, observedAtUtc, TimeSpan.FromMinutes(1), [], 2)), cancellation.Token);

        Assert.Equal(TriggerWorkerSelectionStatus.Acquired, result.SelectionStatus);
        Assert.Equal(TriggerWorkerMutationStatus.Committed, result.MutationStatus);
        Assert.Equal(0, harness.DurableIntentWrites);
        Assert.Equal(0, harness.ProviderCalls);
        Assert.Equal(0, harness.RejectionWrites);
        Assert.Equal(1, harness.ReleaseWrites);
        Assert.Equal(TriggerQueueEntryState.Queued, result.Entry!.State);
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("role")]
    [InlineData("profile")]
    [InlineData("adapter")]
    public async Task Selected_coordinate_drift_fails_closed(string drift)
    {
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var input = Input(context);
        input = drift switch
        {
            "workspace" => input with { WorkspaceId = new string('b', 64) },
            "role" => input with { RoleId = "other-role" },
            "profile" => input with { AuthorityProfileRevision = "8" },
            "adapter" => input with { AdapterImplementationId = "other/implementation" },
            _ => throw new InvalidOperationException(),
        };

        var response = await Authorizer(context).AuthorizeAsync(input, ScheduleCurrentEvidenceTestContext.ObservedAtUtc);

        Assert.NotEqual("Authorized", response.Status);
        Assert.Equal(new string('0', 64), response.EvidenceHash);
    }

    [Fact]
    public async Task Malformed_selected_coordinates_never_reach_mutable_sources()
    {
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var malformedDelivery = Input(context) with { DeliveryId = "INVALID" };
        var malformedAdapter = Input(context) with { AdapterImplementationId = "Invalid/Adapter" };

        var deliveryResponse = await Authorizer(context).AuthorizeAsync(malformedDelivery, ScheduleCurrentEvidenceTestContext.ObservedAtUtc);
        var adapterResponse = await Authorizer(context).AuthorizeAsync(malformedAdapter, ScheduleCurrentEvidenceTestContext.ObservedAtUtc);

        Assert.Equal("Unavailable", deliveryResponse.Status);
        Assert.Equal("Unavailable", adapterResponse.Status);
        Assert.Equal(0, context.FenceCount);
        Assert.Equal(0, context.TargetReadCount);
    }

    private static TriggerWorkerCurrentEvidenceAuthorizer Authorizer(ScheduleCurrentEvidenceTestContext context)
    {
        return new TriggerWorkerCurrentEvidenceAuthorizer(
            "workspace-sha256:" + TriggerWorkspaceId,
            context,
            context,
            context,
            context,
            context,
            new FixedTimeProvider(ScheduleCurrentEvidenceTestContext.GrantEvaluatedAtUtc.AddSeconds(1)));
    }

    private static TriggerWorkerCurrentEvidenceInput Input(ScheduleCurrentEvidenceTestContext context)
    {
        return new TriggerWorkerCurrentEvidenceInput(
            "delivery-1",
            context.Target,
            context.Adapter.Capability.Id.Value,
            context.Adapter.Capability.Version.Value,
            context.Adapter.Capability.Hash.Value,
            context.Adapter.Implementation.ProviderId.Value,
            context.Adapter.Implementation.ImplementationId,
            "owner",
            "scheduler",
            TriggerWorkspaceId,
            "operator",
            context.Profile.ProfileId.Value,
            context.Profile.Revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static TriggerDeliveryEnvelope Envelope(ScheduleCurrentEvidenceTestContext context, DateTimeOffset observedAtUtc)
    {
        Assert.True(AuthorityActorId.TryParse("owner", out var actor, out _));
        Assert.True(TriggerDeliveryFactory.TryCreateActorContext(actor, "scheduler", TriggerWorkspaceId, "operator", out var actorContext, out _));
        Assert.True(AuthorityBoundaryReceiptFactory.TryCreate(
            1,
            AuthorityBoundaryDecision.Direct,
            [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)],
            [context.Profile],
            ScheduleCurrentEvidenceTestContext.GrantEvaluatedAtUtc,
            out var receipt,
            out _));
        Assert.True(ScheduleId.TryParse("trigger-authorizer-schedule", out var scheduleId));
        var occurrence = new ScheduleOccurrence(
            ScheduleOccurrence.CurrentSchemaVersion,
            1,
            DateTime.SpecifyKind(observedAtUtc.AddSeconds(-2).UtcDateTime, DateTimeKind.Unspecified),
            observedAtUtc.AddSeconds(-2),
            new ScheduleTimeZoneReference("Etc/UTC", new string('f', 64)));
        Assert.True(ScheduleIdentityDerivation.TryDerive(scheduleId!, 1, new string('b', 64), occurrence, out var identity, out _));
        Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(1, 1, identity!.DeliveryId, out var redelivery, out _));
        Assert.True(TriggerDeliveryFactory.TryCreateTemporalEvidence(observedAtUtc.AddSeconds(-1), observedAtUtc, occurrence.ScheduledAtUtc, observedAtUtc, null, null, null, out var temporal, out _));
        Assert.True(TriggerDeliveryFactory.TryCreateInlinePayload("dispatch"u8.ToArray(), out var payload, out _));
        var directive = new ScheduleExecutionDirective(
            ScheduleExecutionDirective.CurrentSchemaVersion,
            scheduleId!,
            1,
            new string('b', 64),
            occurrence,
            identity,
            context.Target,
            ScheduleOverlapPolicy.DeferOne,
            new string('e', 64));
        Assert.True(TriggerDeliveryFactory.TryCreateScheduledEnvelope(
            1,
            identity.DeliveryId,
            identity.DeduplicationId,
            context.Adapter,
            context.Target,
            actorContext,
            new TriggerAuthorityEvidence(context.Profile, receipt!),
            temporal,
            payload,
            redelivery,
            directive,
            false,
            null,
            TriggerAdmissionStatus.Admitted,
            TriggerAdmissionReason.EvidenceAccepted,
            out var envelope,
            out var validation), string.Join(',', validation.Errors.Select(error => error.Code)));
        return envelope!;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
