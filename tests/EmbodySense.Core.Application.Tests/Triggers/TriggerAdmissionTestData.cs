using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Tests.Triggers;

internal static class TriggerAdmissionTestData
{
    internal static readonly DateTimeOffset CreatedAtUtc = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    internal static TriggerDeliveryEnvelope Envelope(
        string deliveryId = "delivery-1",
        string deduplicationId = "dedup-1",
        TriggerAdapterReference? adapter = null,
        TriggerLoopReference? loop = null,
        TriggerActorContext? actorContext = null,
        TriggerAuthorityEvidence? authority = null,
        TriggerTemporalEvidence? temporal = null,
        TriggerPayloadEvidence? payload = null,
        int redeliveryAttempt = 1,
        int redeliveryCount = 1,
        string? originalDeliveryId = null,
        TriggerAdmissionStatus visibleStatus = TriggerAdmissionStatus.Unknown,
        TriggerAdmissionReason visibleReason = TriggerAdmissionReason.Unknown)
    {
        Assert.True(TriggerDeliveryId.TryParse(deliveryId, out var delivery));
        Assert.True(TriggerDeduplicationId.TryParse(deduplicationId, out var deduplication));
        Assert.True(TriggerDeliveryId.TryParse(originalDeliveryId ?? deliveryId, out var originalDelivery));
        Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(redeliveryAttempt, redeliveryCount, originalDelivery, out var redelivery, out _));
        adapter ??= Adapter();
        loop ??= Loop();
        actorContext ??= ActorContext();
        authority ??= Authority();
        temporal ??= Temporal(admittedAtUtc: visibleStatus is TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed ? CreatedAtUtc.AddSeconds(3) : null);
        payload ??= Payload();
        Assert.True(TriggerDeliveryFactory.TryCreateEnvelope(1, delivery, deduplication, TriggerKind.Webhook, adapter, loop, actorContext, authority, temporal, payload, redelivery, false, null, visibleStatus, visibleReason, out var envelope, out var validation), string.Join(',', validation.Errors.Select(error => error.Code)));
        return envelope!;
    }

    internal static TriggerDeliveryAdmissionRequest Request(
        TriggerDeliveryEnvelope? envelope = null,
        TriggerLoopReference? currentLoop = null,
        TriggerAdapterReference? currentAdapter = null,
        bool isAdapterAvailable = true,
        TriggerActorContext? currentActorContext = null,
        TriggerAuthorityEvidence? currentAuthority = null,
        DateTimeOffset? evaluatedAtUtc = null)
    {
        envelope ??= Envelope();
        Assert.True(TriggerDeliveryAdmissionRequestFactory.TryCreate(envelope, currentLoop ?? envelope.Loop, currentAdapter ?? envelope.Adapter, isAdapterAvailable, currentActorContext ?? envelope.ActorContext, currentAuthority ?? envelope.Authority, evaluatedAtUtc ?? CreatedAtUtc.AddSeconds(3), out var request, out var validation), string.Join(',', validation.Errors.Select(error => error.Code)));
        return request!;
    }

    internal static TriggerDeliveryAdmissionReceipt Receipt(TriggerDeliveryEnvelope envelope, TriggerAdmissionStatus status = TriggerAdmissionStatus.Admitted, TriggerAdmissionReason reason = TriggerAdmissionReason.EvidenceAccepted, DateTimeOffset? recordedAtUtc = null)
    {
        Assert.True(TriggerDeliveryAdmissionReceiptFactory.TryCreate(envelope, status, reason, recordedAtUtc ?? CreatedAtUtc.AddSeconds(3), out var receipt, out var validation), string.Join(',', validation.Errors.Select(error => error.Code)));
        return receipt!;
    }

    internal static TriggerAdapterReference Adapter(string version = "1.0.0", char hashCharacter = 'a', string implementation = "triggers/webhook")
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/triggers/webhook", out var id, out _));
        Assert.True(CapabilityVersion.TryParse(version, out var exactVersion, out _));
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + new string(hashCharacter, 64), out var hash, out _));
        Assert.True(CapabilityProviderId.TryParse("org.embodysense", out var provider, out _));
        return new TriggerAdapterReference(new CapabilityDescriptorIdentity(id!, exactVersion!, hash!), new CapabilityImplementationIdentity(provider!, implementation));
    }

    internal static TriggerLoopReference Loop(string loopId = "loop-1", int version = 3, char hashCharacter = 'b')
    {
        Assert.True(TriggerDeliveryFactory.TryCreateLoopReference(loopId, version, new string(hashCharacter, 64), out var loop, out _));
        return loop!;
    }

    internal static TriggerActorContext ActorContext(string actor = "owner", string surface = "runtime", string workspace = "workspace-1", string role = "operator")
    {
        Assert.True(AuthorityActorId.TryParse(actor, out var actorId, out _));
        Assert.True(TriggerDeliveryFactory.TryCreateActorContext(actorId, surface, workspace, role, out var context, out _));
        return context!;
    }

    internal static TriggerAuthorityEvidence Authority(DateTimeOffset? evaluatedAtUtc = null, AuthorityBoundaryDecision decision = AuthorityBoundaryDecision.Direct, string profileIdText = "trigger-operator", int revisionValue = 7)
    {
        Assert.True(AuthorityProfileId.TryParse(profileIdText, out var profileId, out _));
        Assert.True(AuthorityProfileRevision.TryParse(revisionValue.ToString(System.Globalization.CultureInfo.InvariantCulture), out var revision, out _));
        var profile = new AuthorityProfileReference(profileId!, revision!);
        var reason = decision switch
        {
            AuthorityBoundaryDecision.Direct => AuthorityBoundaryReason.NoBoundary,
            AuthorityBoundaryDecision.Review => AuthorityBoundaryReason.MandatoryReview,
            AuthorityBoundaryDecision.Pause => AuthorityBoundaryReason.UncertainUserIntent,
            _ => AuthorityBoundaryReason.InvalidContract
        };
        Assert.True(AuthorityBoundaryReceiptFactory.TryCreate(1, decision, [new AuthorityBoundaryCondition(decision, reason)], [profile], evaluatedAtUtc ?? CreatedAtUtc.AddSeconds(2), out var receipt, out _));
        return new TriggerAuthorityEvidence(profile, receipt!);
    }

    internal static TriggerTemporalEvidence Temporal(DateTimeOffset? createdAtUtc = null, DateTimeOffset? observedAtUtc = null, DateTimeOffset? receivedAtUtc = null, DateTimeOffset? admittedAtUtc = null, DateTimeOffset? notBeforeUtc = null, DateTimeOffset? deadlineUtc = null, DateTimeOffset? expiresAtUtc = null)
    {
        var created = createdAtUtc ?? CreatedAtUtc;
        Assert.True(TriggerDeliveryFactory.TryCreateTemporalEvidence(observedAtUtc ?? created.AddSeconds(1), receivedAtUtc ?? created.AddSeconds(2), created, admittedAtUtc, notBeforeUtc, deadlineUtc, expiresAtUtc, out var temporal, out var validation), string.Join(',', validation.Errors.Select(error => error.Code)));
        return temporal!;
    }

    internal static TriggerPayloadEvidence Payload(byte value = 1)
    {
        Assert.True(TriggerDeliveryFactory.TryCreateInlinePayload([value], out var payload, out _));
        return payload!;
    }
}
