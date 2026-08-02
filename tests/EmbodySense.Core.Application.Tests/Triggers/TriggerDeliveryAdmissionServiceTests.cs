using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Tests.Triggers;

public sealed class TriggerDeliveryAdmissionServiceTests
{
    private readonly ITriggerDeliveryAdmissionPort _port = new TriggerDeliveryAdmissionService(new TriggerDeliveryAdmissionHistoryStub());

    [Fact]
    public async Task Exact_current_evidence_is_admitted_but_never_grants_execution()
    {
        var result = await _port.AdmitAsync(TriggerAdmissionTestData.Request());

        Assert.Equal(TriggerAdmissionStatus.Admitted, result.Status);
        Assert.Equal(TriggerAdmissionReason.EvidenceAccepted, result.Reason);
        Assert.True(result.IsAdmitted);
        Assert.False(result.CanExecute);
        Assert.Matches("^[0-9a-f]{64}$", result.CanonicalEnvelopeHash!);
        Assert.Single(typeof(ITriggerDeliveryAdmissionPort).GetMethods());
        Assert.DoesNotContain(typeof(TriggerDeliveryAdmissionRequest).GetProperties(), property => property.Name.Contains("Grant", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Execute", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Dispatch", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Queue", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Catalog", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Caller_cannot_supply_or_mint_replay_evidence_through_the_request()
    {
        var envelope = TriggerAdmissionTestData.Envelope();
        var callerMintedReceipt = TriggerAdmissionTestData.Receipt(envelope);
        var request = TriggerAdmissionTestData.Request(envelope: envelope);
        var emptyHistory = new TriggerDeliveryAdmissionHistoryStub();
        var result = await new TriggerDeliveryAdmissionService(emptyHistory).AdmitAsync(request);

        Assert.NotNull(callerMintedReceipt);
        Assert.Equal(1, emptyHistory.QueryCount);
        AssertOutcome(result, TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted, admitted: true);
        Assert.False(result.IsReplay);
        Assert.Null(result.OriginalStatus);
        Assert.Null(result.OriginalReason);
        Assert.DoesNotContain(typeof(TriggerDeliveryAdmissionRequest).GetProperties(), property => property.Name.Contains("Existing", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Receipt", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Replay", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(TriggerDeliveryAdmissionRequestFactory).GetMethods().Single(method => method.Name == nameof(TriggerDeliveryAdmissionRequestFactory.TryCreate)).GetParameters(), parameter => parameter.ParameterType.Name.Contains("Receipt", StringComparison.OrdinalIgnoreCase) || parameter.ParameterType.Name.Contains("History", StringComparison.OrdinalIgnoreCase));
        Assert.Single(typeof(ITriggerDeliveryAdmissionHistoryPort).GetMethods());
    }

    [Fact]
    public async Task Authenticated_admitted_outcome_replays_exact_delivery_and_permitted_redelivery()
    {
        var original = TriggerAdmissionTestData.Envelope();
        var receipt = TriggerAdmissionTestData.Receipt(original);
        var history = new TriggerDeliveryAdmissionHistoryStub(new TriggerDeliveryAdmissionHistoryEntry(original, receipt));
        var port = new TriggerDeliveryAdmissionService(history);
        var exactReplay = await port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: original));
        var redelivery = TriggerAdmissionTestData.Envelope(
            deliveryId: "delivery-2",
            temporal: TriggerAdmissionTestData.Temporal(receivedAtUtc: TriggerAdmissionTestData.CreatedAtUtc.AddSeconds(3)),
            redeliveryAttempt: 2,
            redeliveryCount: 2,
            originalDeliveryId: "delivery-1");
        var redeliveryReplay = await port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: redelivery, evaluatedAtUtc: TriggerAdmissionTestData.CreatedAtUtc.AddSeconds(4)));

        AssertReplay(exactReplay, TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted, admitted: true);
        AssertReplay(redeliveryReplay, TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted, admitted: true);
        Assert.Equal(2, history.QueryCount);
        Assert.Equal(redelivery.DeliveryId, history.RequestedDeliveryId);
        Assert.Equal(redelivery.DeduplicationId, history.RequestedDeduplicationId);
    }

    [Theory]
    [InlineData(TriggerAdmissionStatus.Expired, TriggerAdmissionReason.Expired)]
    [InlineData(TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.StaleAuthority)]
    public async Task Authenticated_non_admitted_terminal_outcome_is_preserved_without_admission(TriggerAdmissionStatus status, TriggerAdmissionReason reason)
    {
        var envelope = status == TriggerAdmissionStatus.Expired
            ? TriggerAdmissionTestData.Envelope(temporal: TriggerAdmissionTestData.Temporal(expiresAtUtc: TriggerAdmissionTestData.CreatedAtUtc.AddSeconds(3)))
            : TriggerAdmissionTestData.Envelope();
        var receipt = TriggerAdmissionTestData.Receipt(envelope, status, reason);
        var port = Port(envelope, receipt);
        var result = await port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: envelope));

        AssertOutcome(result, status, reason);
        Assert.True(result.IsReplay);
        Assert.Equal(status, result.OriginalStatus);
        Assert.Equal(reason, result.OriginalReason);
    }

    [Fact]
    public async Task Reused_identity_conflicts_when_payload_target_authority_or_redelivery_lineage_changes()
    {
        var envelope = TriggerAdmissionTestData.Envelope();
        var receipt = TriggerAdmissionTestData.Receipt(envelope);
        var port = Port(envelope, receipt);
        var laterReceived = TriggerAdmissionTestData.Temporal(receivedAtUtc: TriggerAdmissionTestData.CreatedAtUtc.AddSeconds(3));
        var deliveryConflict = await port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: TriggerAdmissionTestData.Envelope(payload: TriggerAdmissionTestData.Payload(2))));
        var payloadConflict = await port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: TriggerAdmissionTestData.Envelope(deliveryId: "delivery-2", temporal: laterReceived, payload: TriggerAdmissionTestData.Payload(3), redeliveryAttempt: 2, redeliveryCount: 2, originalDeliveryId: "delivery-1")));
        var loopConflict = await port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: TriggerAdmissionTestData.Envelope(deliveryId: "delivery-3", loop: TriggerAdmissionTestData.Loop(version: 4), temporal: laterReceived, redeliveryAttempt: 2, redeliveryCount: 2, originalDeliveryId: "delivery-1"), currentLoop: TriggerAdmissionTestData.Loop(version: 4)));
        var authorityConflict = await port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: TriggerAdmissionTestData.Envelope(deliveryId: "delivery-4", authority: TriggerAdmissionTestData.Authority(revisionValue: 8), temporal: laterReceived, redeliveryAttempt: 2, redeliveryCount: 2, originalDeliveryId: "delivery-1"), currentAuthority: TriggerAdmissionTestData.Authority(revisionValue: 8)));
        var temporalConflict = await port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: TriggerAdmissionTestData.Envelope(deliveryId: "delivery-5", temporal: TriggerAdmissionTestData.Temporal(createdAtUtc: TriggerAdmissionTestData.CreatedAtUtc.AddTicks(1), receivedAtUtc: TriggerAdmissionTestData.CreatedAtUtc.AddSeconds(3)), redeliveryAttempt: 2, redeliveryCount: 2, originalDeliveryId: "delivery-1")));
        var lineageConflict = await port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: TriggerAdmissionTestData.Envelope(deliveryId: "delivery-6", temporal: laterReceived, redeliveryAttempt: 2, redeliveryCount: 2, originalDeliveryId: "delivery-0")));

        AssertOutcome(deliveryConflict, TriggerAdmissionStatus.Conflicting, TriggerAdmissionReason.IdentityConflict);
        AssertOutcome(payloadConflict, TriggerAdmissionStatus.Conflicting, TriggerAdmissionReason.IdentityConflict);
        AssertOutcome(loopConflict, TriggerAdmissionStatus.Conflicting, TriggerAdmissionReason.IdentityConflict);
        AssertOutcome(authorityConflict, TriggerAdmissionStatus.Conflicting, TriggerAdmissionReason.IdentityConflict);
        AssertOutcome(temporalConflict, TriggerAdmissionStatus.Conflicting, TriggerAdmissionReason.IdentityConflict);
        AssertOutcome(lineageConflict, TriggerAdmissionStatus.Conflicting, TriggerAdmissionReason.IdentityConflict);
    }

    [Fact]
    public async Task Redelivery_requires_strictly_later_server_receive_time()
    {
        var original = TriggerAdmissionTestData.Envelope();
        var port = Port(original, TriggerAdmissionTestData.Receipt(original));
        var equal = TriggerAdmissionTestData.Envelope(deliveryId: "delivery-2", redeliveryAttempt: 2, redeliveryCount: 2, originalDeliveryId: "delivery-1");
        var earlier = TriggerAdmissionTestData.Envelope(deliveryId: "delivery-3", temporal: TriggerAdmissionTestData.Temporal(receivedAtUtc: TriggerAdmissionTestData.CreatedAtUtc.AddSeconds(1)), redeliveryAttempt: 2, redeliveryCount: 2, originalDeliveryId: "delivery-1");
        var later = TriggerAdmissionTestData.Envelope(deliveryId: "delivery-4", temporal: TriggerAdmissionTestData.Temporal(receivedAtUtc: TriggerAdmissionTestData.CreatedAtUtc.AddSeconds(3)), redeliveryAttempt: 2, redeliveryCount: 2, originalDeliveryId: "delivery-1");

        AssertOutcome(await port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: equal)), TriggerAdmissionStatus.Conflicting, TriggerAdmissionReason.IdentityConflict);
        AssertOutcome(await port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: earlier)), TriggerAdmissionStatus.Conflicting, TriggerAdmissionReason.IdentityConflict);
        AssertReplay(await port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: later)), TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted, admitted: true);
    }

    [Fact]
    public async Task Divergent_delivery_and_deduplication_history_matches_conflict()
    {
        var deliveryMatch = TriggerAdmissionTestData.Envelope();
        var deduplicationMatch = TriggerAdmissionTestData.Envelope(deliveryId: "delivery-2", deduplicationId: "dedup-2");
        var history = new TriggerDeliveryAdmissionHistoryStub(
            new TriggerDeliveryAdmissionHistoryEntry(deliveryMatch, TriggerAdmissionTestData.Receipt(deliveryMatch)),
            new TriggerDeliveryAdmissionHistoryEntry(deduplicationMatch, TriggerAdmissionTestData.Receipt(deduplicationMatch)));
        var current = TriggerAdmissionTestData.Envelope(
            deliveryId: "delivery-1",
            deduplicationId: "dedup-2",
            temporal: TriggerAdmissionTestData.Temporal(receivedAtUtc: TriggerAdmissionTestData.CreatedAtUtc.AddSeconds(3)),
            redeliveryAttempt: 2,
            redeliveryCount: 2,
            originalDeliveryId: "delivery-2");

        var result = await new TriggerDeliveryAdmissionService(history).AdmitAsync(TriggerAdmissionTestData.Request(envelope: current, evaluatedAtUtc: TriggerAdmissionTestData.CreatedAtUtc.AddSeconds(4)));

        AssertOutcome(result, TriggerAdmissionStatus.Conflicting, TriggerAdmissionReason.IdentityConflict);
    }

    [Fact]
    public async Task Unavailable_history_fails_closed_with_a_structured_outcome()
    {
        var result = await new TriggerDeliveryAdmissionService(TriggerDeliveryAdmissionHistoryStub.Unavailable()).AdmitAsync(TriggerAdmissionTestData.Request());

        AssertOutcome(result, TriggerAdmissionStatus.Unavailable, TriggerAdmissionReason.HistoryUnavailable);
    }

    [Fact]
    public async Task Admitted_replay_revalidates_current_evidence_before_reporting_admission()
    {
        var envelope = TriggerAdmissionTestData.Envelope();
        var port = Port(envelope, TriggerAdmissionTestData.Receipt(envelope));
        var cases = new[]
        {
            (TriggerAdmissionTestData.Request(envelope: envelope, currentLoop: TriggerAdmissionTestData.Loop(version: 4)), TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.StaleLoop),
            (TriggerAdmissionTestData.Request(envelope: envelope, currentAdapter: TriggerAdmissionTestData.Adapter(version: "1.0.1")), TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.StaleAdapter),
            (TriggerAdmissionTestData.Request(envelope: envelope, isAdapterAvailable: false), TriggerAdmissionStatus.Unavailable, TriggerAdmissionReason.AdapterUnavailable),
            (TriggerAdmissionTestData.Request(envelope: envelope, currentActorContext: TriggerAdmissionTestData.ActorContext(actor: "other")), TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.ActorMismatch),
            (TriggerAdmissionTestData.Request(envelope: envelope, currentActorContext: TriggerAdmissionTestData.ActorContext(surface: "web")), TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.SurfaceMismatch),
            (TriggerAdmissionTestData.Request(envelope: envelope, currentActorContext: TriggerAdmissionTestData.ActorContext(workspace: "workspace-2")), TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.WorkspaceMismatch),
            (TriggerAdmissionTestData.Request(envelope: envelope, currentActorContext: TriggerAdmissionTestData.ActorContext(role: "reviewer")), TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.RoleMismatch),
            (TriggerAdmissionTestData.Request(envelope: envelope, currentAuthority: TriggerAdmissionTestData.Authority(revisionValue: 8)), TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.AuthorityMismatch),
            (TriggerAdmissionTestData.Request(envelope: envelope, evaluatedAtUtc: envelope.Authority.BoundaryReceipt.EvaluatedAtUtc + TriggerDeliveryLimits.MaxAuthorityEvidenceAge + TimeSpan.FromTicks(1)), TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.StaleAuthority)
        };

        foreach (var (request, status, reason) in cases)
        {
            var result = await port.AdmitAsync(request);
            AssertOutcome(result, status, reason);
            Assert.False(result.IsReplay);
        }

        var reviewAuthority = TriggerAdmissionTestData.Authority(decision: AuthorityBoundaryDecision.Review);
        var reviewEnvelope = TriggerAdmissionTestData.Envelope(authority: reviewAuthority);
        var reviewReplay = await Port(reviewEnvelope, TriggerAdmissionTestData.Receipt(reviewEnvelope)).AdmitAsync(TriggerAdmissionTestData.Request(envelope: reviewEnvelope, currentAuthority: reviewAuthority));
        AssertOutcome(reviewReplay, TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.AuthorityBoundary);

        var expiry = TriggerAdmissionTestData.CreatedAtUtc.AddSeconds(5);
        var expiringEnvelope = TriggerAdmissionTestData.Envelope(temporal: TriggerAdmissionTestData.Temporal(expiresAtUtc: expiry));
        var expiredReplay = await Port(expiringEnvelope, TriggerAdmissionTestData.Receipt(expiringEnvelope)).AdmitAsync(TriggerAdmissionTestData.Request(envelope: expiringEnvelope, evaluatedAtUtc: expiry));
        AssertOutcome(expiredReplay, TriggerAdmissionStatus.Expired, TriggerAdmissionReason.Expired);
    }

    [Fact]
    public async Task Temporal_endpoints_are_exact_and_do_not_read_wall_clock()
    {
        var created = TriggerAdmissionTestData.CreatedAtUtc;
        var temporal = TriggerAdmissionTestData.Temporal(notBeforeUtc: created.AddSeconds(3), deadlineUtc: created.AddSeconds(4), expiresAtUtc: created.AddSeconds(5));
        var envelope = TriggerAdmissionTestData.Envelope(temporal: temporal, authority: TriggerAdmissionTestData.Authority(evaluatedAtUtc: created.AddSeconds(2)));

        AssertOutcome(await AdmitAt(envelope, created.AddSeconds(3).AddTicks(-1)), TriggerAdmissionStatus.NotYetEligible, TriggerAdmissionReason.NotBefore);
        AssertOutcome(await AdmitAt(envelope, created.AddSeconds(3)), TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted, admitted: true);
        AssertOutcome(await AdmitAt(envelope, created.AddSeconds(4)), TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted, admitted: true);
        AssertOutcome(await AdmitAt(envelope, created.AddSeconds(4).AddTicks(1)), TriggerAdmissionStatus.Expired, TriggerAdmissionReason.DeadlineExceeded);
        AssertOutcome(await AdmitAt(envelope, created.AddSeconds(5)), TriggerAdmissionStatus.Expired, TriggerAdmissionReason.Expired);
    }

    [Fact]
    public async Task Every_current_binding_mismatch_fails_closed_with_a_stable_reason()
    {
        var envelope = TriggerAdmissionTestData.Envelope();
        var cases = new[]
        {
            (TriggerAdmissionTestData.Request(envelope: envelope, currentLoop: TriggerAdmissionTestData.Loop(version: 4)), TriggerAdmissionReason.StaleLoop),
            (TriggerAdmissionTestData.Request(envelope: envelope, currentAdapter: TriggerAdmissionTestData.Adapter(version: "1.0.1")), TriggerAdmissionReason.StaleAdapter),
            (TriggerAdmissionTestData.Request(envelope: envelope, currentActorContext: TriggerAdmissionTestData.ActorContext(actor: "other")), TriggerAdmissionReason.ActorMismatch),
            (TriggerAdmissionTestData.Request(envelope: envelope, currentActorContext: TriggerAdmissionTestData.ActorContext(surface: "web")), TriggerAdmissionReason.SurfaceMismatch),
            (TriggerAdmissionTestData.Request(envelope: envelope, currentActorContext: TriggerAdmissionTestData.ActorContext(workspace: "workspace-2")), TriggerAdmissionReason.WorkspaceMismatch),
            (TriggerAdmissionTestData.Request(envelope: envelope, currentActorContext: TriggerAdmissionTestData.ActorContext(role: "reviewer")), TriggerAdmissionReason.RoleMismatch),
            (TriggerAdmissionTestData.Request(envelope: envelope, currentAuthority: TriggerAdmissionTestData.Authority(revisionValue: 8)), TriggerAdmissionReason.AuthorityMismatch)
        };

        foreach (var (request, reason) in cases)
        {
            AssertOutcome(await _port.AdmitAsync(request), TriggerAdmissionStatus.Unauthorized, reason);
        }
    }

    [Fact]
    public async Task Adapter_availability_and_boundary_evidence_never_become_ambient_authority()
    {
        var unavailable = await _port.AdmitAsync(TriggerAdmissionTestData.Request(isAdapterAvailable: false));
        AssertOutcome(unavailable, TriggerAdmissionStatus.Unavailable, TriggerAdmissionReason.AdapterUnavailable);

        var reviewAuthority = TriggerAdmissionTestData.Authority(decision: AuthorityBoundaryDecision.Review);
        var reviewEnvelope = TriggerAdmissionTestData.Envelope(authority: reviewAuthority, visibleStatus: TriggerAdmissionStatus.Admitted, visibleReason: TriggerAdmissionReason.EvidenceAccepted);
        var review = await _port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: reviewEnvelope, currentAuthority: reviewAuthority));
        AssertOutcome(review, TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.AuthorityBoundary);
        Assert.False(review.CanExecute);
    }

    [Fact]
    public async Task Authority_and_delivery_age_bounds_accept_exact_boundary_and_reject_boundary_plus_one()
    {
        var authorityEvaluated = TriggerAdmissionTestData.CreatedAtUtc.AddSeconds(2);
        var authority = TriggerAdmissionTestData.Authority(evaluatedAtUtc: authorityEvaluated);
        var envelope = TriggerAdmissionTestData.Envelope(authority: authority);
        var exactAuthority = await _port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: envelope, currentAuthority: authority, evaluatedAtUtc: authorityEvaluated + TriggerDeliveryLimits.MaxAuthorityEvidenceAge));
        var staleAuthority = await _port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: envelope, currentAuthority: authority, evaluatedAtUtc: authorityEvaluated + TriggerDeliveryLimits.MaxAuthorityEvidenceAge + TimeSpan.FromTicks(1)));
        AssertOutcome(exactAuthority, TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted, admitted: true);
        AssertOutcome(staleAuthority, TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.StaleAuthority);

        var created = TriggerAdmissionTestData.CreatedAtUtc;
        var received = created.AddSeconds(2);
        var exactEvaluation = received + TriggerDeliveryLimits.MaxAdmissionAge;
        var exactDeliveryAuthority = TriggerAdmissionTestData.Authority(evaluatedAtUtc: exactEvaluation);
        var exactDeliveryEnvelope = TriggerAdmissionTestData.Envelope(authority: exactDeliveryAuthority);
        var exactDelivery = await _port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: exactDeliveryEnvelope, currentAuthority: exactDeliveryAuthority, evaluatedAtUtc: exactEvaluation));
        var staleEvaluation = exactEvaluation.AddTicks(1);
        var staleDeliveryAuthority = TriggerAdmissionTestData.Authority(evaluatedAtUtc: staleEvaluation);
        var staleDeliveryEnvelope = TriggerAdmissionTestData.Envelope(authority: staleDeliveryAuthority);
        var staleDelivery = await _port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: staleDeliveryEnvelope, currentAuthority: staleDeliveryAuthority, evaluatedAtUtc: staleEvaluation));
        AssertOutcome(exactDelivery, TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted, admitted: true);
        AssertOutcome(staleDelivery, TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.StaleDelivery);
    }

    [Fact]
    public void Request_factory_rejects_forged_malformed_or_non_utc_current_evidence()
    {
        var envelope = TriggerAdmissionTestData.Envelope();
        Assert.False(TriggerDeliveryAdmissionRequestFactory.TryCreate(null, envelope.Loop, envelope.Adapter, true, envelope.ActorContext, envelope.Authority, TriggerAdmissionTestData.CreatedAtUtc, out _, out var missingValidation));
        Assert.Contains(missingValidation.Errors, error => error.Code == "required");

        Assert.False(TriggerDeliveryAdmissionRequestFactory.TryCreate(envelope, null, envelope.Adapter, true, envelope.ActorContext, envelope.Authority, TriggerAdmissionTestData.CreatedAtUtc, out _, out var loopValidation));
        Assert.Contains(loopValidation.Errors, error => error.Code == "invalid_current_loop");

        Assert.False(TriggerDeliveryAdmissionRequestFactory.TryCreate(envelope, envelope.Loop, envelope.Adapter, true, null, envelope.Authority, TriggerAdmissionTestData.CreatedAtUtc, out _, out var actorValidation));
        Assert.Contains(actorValidation.Errors, error => error.Code == "invalid_current_actor_context");

        var forgedAdapter = new TriggerAdapterReference(null!, new CapabilityImplementationIdentity(null!, "../secret"));
        Assert.False(TriggerDeliveryAdmissionRequestFactory.TryCreate(envelope, envelope.Loop, forgedAdapter, true, envelope.ActorContext, envelope.Authority, TriggerAdmissionTestData.CreatedAtUtc, out _, out var adapterValidation));
        Assert.Contains(adapterValidation.Errors, error => error.Code == "invalid_current_adapter");

        var forgedAuthority = new TriggerAuthorityEvidence(new AuthorityProfileReference(TriggerAdmissionTestData.Authority(revisionValue: 8).Profile.ProfileId, TriggerAdmissionTestData.Authority(revisionValue: 8).Profile.Revision), envelope.Authority.BoundaryReceipt);
        Assert.False(TriggerDeliveryAdmissionRequestFactory.TryCreate(envelope, envelope.Loop, envelope.Adapter, true, envelope.ActorContext, forgedAuthority, TriggerAdmissionTestData.CreatedAtUtc, out _, out var authorityValidation));
        Assert.Contains(authorityValidation.Errors, error => error.Code == "invalid_current_authority");

        Assert.False(TriggerDeliveryAdmissionRequestFactory.TryCreate(envelope, envelope.Loop, envelope.Adapter, true, envelope.ActorContext, envelope.Authority, TriggerAdmissionTestData.CreatedAtUtc.ToOffset(TimeSpan.FromHours(1)), out _, out var timeValidation));
        Assert.Contains(timeValidation.Errors, error => error.Code == "utc_required");
    }

    [Fact]
    public async Task Server_history_binding_rejects_forged_mismatched_or_unrelated_terminal_receipts()
    {
        var envelope = TriggerAdmissionTestData.Envelope();
        var receipt = TriggerAdmissionTestData.Receipt(envelope);
        var forgedHash = receipt with { CanonicalEnvelopeHash = new string('f', 64) };
        var forgedBinding = receipt with { ReplayBindingHash = new string('e', 64) };
        Assert.True(TriggerDeliveryId.TryParse("delivery-forged", out var forgedId));
        var forgedIdentity = receipt with { DeliveryId = forgedId! };
        var missingIdentity = receipt with { DeliveryId = null!, DeduplicationId = null! };
        var forgedOutcome = receipt with { Reason = TriggerAdmissionReason.InvalidEnvelope };
        var forgedReceipts = new[] { forgedHash, forgedBinding, forgedIdentity, missingIdentity, forgedOutcome };
        foreach (var forgedReceipt in forgedReceipts)
        {
            var result = await Port(envelope, forgedReceipt).AdmitAsync(TriggerAdmissionTestData.Request(envelope: envelope));
            AssertOutcome(result, TriggerAdmissionStatus.Invalid, TriggerAdmissionReason.InvalidEnvelope);
        }

    }

    [Fact]
    public void Receipt_factory_enforces_terminal_outcome_schema_and_exact_utc_time_bounds()
    {
        var envelope = TriggerAdmissionTestData.Envelope();
        var received = envelope.Temporal.ReceivedAtUtc;
        Assert.True(TriggerDeliveryAdmissionReceiptFactory.TryCreate(envelope, TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted, received, out var exactReceipt, out _));
        Assert.Equal(received, exactReceipt!.RecordedAtUtc);

        Assert.False(TriggerDeliveryAdmissionReceiptFactory.TryCreate(envelope, TriggerAdmissionStatus.Unknown, TriggerAdmissionReason.Unknown, received, out _, out var outcomeValidation));
        Assert.Contains(outcomeValidation.Errors, error => error.Code == "invalid_terminal_outcome");
        Assert.False(TriggerDeliveryAdmissionReceiptFactory.TryCreate(envelope, TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted, received.AddTicks(-1), out _, out var earlyValidation));
        Assert.Contains(earlyValidation.Errors, error => error.Code == "invalid_receipt_time");
        Assert.False(TriggerDeliveryAdmissionReceiptFactory.TryCreate(envelope, TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted, received.ToOffset(TimeSpan.FromHours(1)), out _, out var utcValidation));
        Assert.Contains(utcValidation.Errors, error => error.Code == "utc_required");
        Assert.False(TriggerDeliveryAdmissionReceiptFactory.TryCreate(envelope, TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted, envelope.Temporal.CreatedAtUtc + TriggerDeliveryLimits.MaxTemporalHorizon + TimeSpan.FromTicks(1), out _, out var horizonValidation));
        Assert.Contains(horizonValidation.Errors, error => error.Code == "invalid_receipt_time");
        Assert.False(TriggerDeliveryAdmissionReceiptFactory.TryCreate(null, TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted, received, out _, out var envelopeValidation));
        Assert.Contains(envelopeValidation.Errors, error => error.Code == "required");

        var unsupported = exactReceipt with { SchemaVersion = 2 };
        var unsupportedValidation = TriggerDeliveryAdmissionReceiptFactory.Validate(unsupported, envelope);
        Assert.Contains(unsupportedValidation.Errors, error => error.Code == "unsupported_schema_version");
        Assert.Contains(TriggerDeliveryAdmissionReceiptFactory.Validate(null, envelope).Errors, error => error.Code == "required");
    }

    [Theory]
    [InlineData(TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted)]
    [InlineData(TriggerAdmissionStatus.Replayed, TriggerAdmissionReason.ExactReplay)]
    public void Successful_receipt_time_must_be_inside_exact_eligibility_endpoints(TriggerAdmissionStatus status, TriggerAdmissionReason reason)
    {
        var created = TriggerAdmissionTestData.CreatedAtUtc;
        var notBefore = created.AddSeconds(4);
        var deadline = created.AddSeconds(5);
        var expiry = created.AddSeconds(6);
        var boundedEnvelope = TriggerAdmissionTestData.Envelope(temporal: TriggerAdmissionTestData.Temporal(notBeforeUtc: notBefore, deadlineUtc: deadline, expiresAtUtc: expiry));

        Assert.False(TriggerDeliveryAdmissionReceiptFactory.TryCreate(boundedEnvelope, status, reason, notBefore.AddTicks(-1), out _, out var beforeValidation));
        Assert.Contains(beforeValidation.Errors, error => error.Code == "receipt_before_not_before");
        Assert.True(TriggerDeliveryAdmissionReceiptFactory.TryCreate(boundedEnvelope, status, reason, notBefore, out _, out _));
        Assert.True(TriggerDeliveryAdmissionReceiptFactory.TryCreate(boundedEnvelope, status, reason, deadline, out _, out _));
        Assert.False(TriggerDeliveryAdmissionReceiptFactory.TryCreate(boundedEnvelope, status, reason, deadline.AddTicks(1), out _, out var deadlineValidation));
        Assert.Contains(deadlineValidation.Errors, error => error.Code == "receipt_after_deadline");

        var expiryEnvelope = TriggerAdmissionTestData.Envelope(temporal: TriggerAdmissionTestData.Temporal(notBeforeUtc: notBefore, expiresAtUtc: expiry));
        Assert.True(TriggerDeliveryAdmissionReceiptFactory.TryCreate(expiryEnvelope, status, reason, expiry.AddTicks(-1), out _, out _));
        Assert.False(TriggerDeliveryAdmissionReceiptFactory.TryCreate(expiryEnvelope, status, reason, expiry, out _, out var expiryValidation));
        Assert.Contains(expiryValidation.Errors, error => error.Code == "receipt_at_or_after_expiry");
    }

    [Fact]
    public void Expired_receipts_require_the_exact_endpoint_that_explains_the_terminal_reason()
    {
        var created = TriggerAdmissionTestData.CreatedAtUtc;
        var unbounded = TriggerAdmissionTestData.Envelope();
        Assert.False(TriggerDeliveryAdmissionReceiptFactory.TryCreate(unbounded, TriggerAdmissionStatus.Expired, TriggerAdmissionReason.Expired, created.AddSeconds(3), out _, out var missingExpiry));
        Assert.Contains(missingExpiry.Errors, error => error.Code == "invalid_expiry_outcome_time");
        Assert.False(TriggerDeliveryAdmissionReceiptFactory.TryCreate(unbounded, TriggerAdmissionStatus.Expired, TriggerAdmissionReason.DeadlineExceeded, created.AddSeconds(3), out _, out var missingDeadline));
        Assert.Contains(missingDeadline.Errors, error => error.Code == "invalid_deadline_outcome_time");

        var deadline = created.AddSeconds(4);
        var expiry = created.AddSeconds(6);
        var bounded = TriggerAdmissionTestData.Envelope(temporal: TriggerAdmissionTestData.Temporal(deadlineUtc: deadline, expiresAtUtc: expiry));
        Assert.False(TriggerDeliveryAdmissionReceiptFactory.TryCreate(bounded, TriggerAdmissionStatus.Expired, TriggerAdmissionReason.DeadlineExceeded, deadline, out _, out var atDeadline));
        Assert.Contains(atDeadline.Errors, error => error.Code == "invalid_deadline_outcome_time");
        Assert.True(TriggerDeliveryAdmissionReceiptFactory.TryCreate(bounded, TriggerAdmissionStatus.Expired, TriggerAdmissionReason.DeadlineExceeded, deadline.AddTicks(1), out _, out _));
        Assert.False(TriggerDeliveryAdmissionReceiptFactory.TryCreate(bounded, TriggerAdmissionStatus.Expired, TriggerAdmissionReason.DeadlineExceeded, expiry, out _, out var expiryPrecedence));
        Assert.Contains(expiryPrecedence.Errors, error => error.Code == "invalid_deadline_outcome_time");
        Assert.False(TriggerDeliveryAdmissionReceiptFactory.TryCreate(bounded, TriggerAdmissionStatus.Expired, TriggerAdmissionReason.Expired, expiry.AddTicks(-1), out _, out var beforeExpiry));
        Assert.Contains(beforeExpiry.Errors, error => error.Code == "invalid_expiry_outcome_time");
        Assert.True(TriggerDeliveryAdmissionReceiptFactory.TryCreate(bounded, TriggerAdmissionStatus.Expired, TriggerAdmissionReason.Expired, expiry, out _, out _));
    }

    [Fact]
    public async Task Cancellation_is_observed_before_admission()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => _port.AdmitAsync(TriggerAdmissionTestData.Request(), source.Token));
        Assert.Throws<ArgumentNullException>(() => new TriggerDeliveryAdmissionService(null!));
    }

    private static ITriggerDeliveryAdmissionPort Port(TriggerDeliveryEnvelope envelope, TriggerDeliveryAdmissionReceipt receipt)
    {
        return new TriggerDeliveryAdmissionService(new TriggerDeliveryAdmissionHistoryStub(new TriggerDeliveryAdmissionHistoryEntry(envelope, receipt)));
    }

    private Task<TriggerDeliveryAdmissionResult> AdmitAt(TriggerDeliveryEnvelope envelope, DateTimeOffset evaluatedAtUtc)
    {
        return _port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: envelope, evaluatedAtUtc: evaluatedAtUtc));
    }

    private static void AssertOutcome(TriggerDeliveryAdmissionResult result, TriggerAdmissionStatus status, TriggerAdmissionReason reason, bool admitted = false)
    {
        Assert.Equal(status, result.Status);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(admitted, result.IsAdmitted);
        Assert.False(result.CanExecute);
    }

    private static void AssertReplay(TriggerDeliveryAdmissionResult result, TriggerAdmissionStatus originalStatus, TriggerAdmissionReason originalReason, bool admitted)
    {
        AssertOutcome(result, TriggerAdmissionStatus.Replayed, TriggerAdmissionReason.ExactReplay, admitted);
        Assert.True(result.IsReplay);
        Assert.Equal(originalStatus, result.OriginalStatus);
        Assert.Equal(originalReason, result.OriginalReason);
    }
}
