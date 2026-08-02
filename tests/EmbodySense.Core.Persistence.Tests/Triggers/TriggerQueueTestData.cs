using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Persistence.Tests.Triggers;

internal static class TriggerQueueTestData
{
    internal static readonly DateTimeOffset CreatedAtUtc = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    internal static TriggerDeliveryEnvelope Envelope(string deliveryId = "delivery-1", string deduplicationId = "dedup-1", string loopId = "loop-1", TriggerTemporalEvidence? temporal = null, TriggerPayloadEvidence? payload = null, TriggerAuthorityEvidence? authority = null, int attempt = 1, int count = 1, string? originalDeliveryId = null)
    {
        Assert.True(TriggerDeliveryId.TryParse(deliveryId, out var delivery));
        Assert.True(TriggerDeduplicationId.TryParse(deduplicationId, out var deduplication));
        Assert.True(TriggerDeliveryId.TryParse(originalDeliveryId ?? deliveryId, out var original));
        Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(attempt, count, original, out var redelivery, out _));
        var adapter = Adapter();
        var loop = Loop(loopId);
        var actor = Actor();
        authority ??= Authority();
        temporal ??= Temporal();
        payload ??= Payload();
        Assert.True(TriggerDeliveryFactory.TryCreateEnvelope(1, delivery, deduplication, TriggerKind.Webhook, adapter, loop, actor, authority, temporal, payload, redelivery, false, null, TriggerAdmissionStatus.Unknown, TriggerAdmissionReason.Unknown, out var envelope, out var validation), string.Join(',', validation.Errors.Select(error => error.Code)));
        return envelope!;
    }

    internal static TriggerDeliveryAdmissionRequest DeliveryRequest(TriggerDeliveryEnvelope envelope, DateTimeOffset? evaluatedAtUtc = null, bool adapterAvailable = true, TriggerLoopReference? currentLoop = null, TriggerAuthorityEvidence? currentAuthority = null)
    {
        Assert.True(TriggerDeliveryAdmissionRequestFactory.TryCreate(envelope, currentLoop ?? envelope.Loop, envelope.Adapter, adapterAvailable, envelope.ActorContext, currentAuthority ?? envelope.Authority, evaluatedAtUtc ?? CreatedAtUtc.AddSeconds(3), out var request, out var validation), string.Join(',', validation.Errors.Select(error => error.Code)));
        return request!;
    }

    internal static TriggerQueueAdmissionRequest QueueRequest(TriggerDeliveryEnvelope envelope, TriggerQueuePriority priority = TriggerQueuePriority.Normal, DateTimeOffset? evaluatedAtUtc = null, bool adapterAvailable = true)
    {
        return TriggerQueueAdmissionRequestFactory.Create(DeliveryRequest(envelope, evaluatedAtUtc, adapterAvailable), TriggerQueueAdmissionMode.Queued, priority);
    }

    internal static TriggerQueueAdmissionService Service(EmbodySense.Core.Persistence.Triggers.TriggerQueueStore store)
    {
        return new TriggerQueueAdmissionService(new TriggerDeliveryAdmissionService(store), store);
    }

    internal static TriggerAdapterReference Adapter()
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/triggers/webhook", out var id, out _));
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var version, out _));
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + new string('a', 64), out var hash, out _));
        Assert.True(CapabilityProviderId.TryParse("org.embodysense", out var provider, out _));
        return new TriggerAdapterReference(new CapabilityDescriptorIdentity(id!, version!, hash!), new CapabilityImplementationIdentity(provider!, "triggers/webhook"));
    }

    internal static TriggerLoopReference Loop(string loopId)
    {
        Assert.True(TriggerDeliveryFactory.TryCreateLoopReference(loopId, 1, new string('b', 64), out var loop, out _));
        return loop!;
    }

    internal static TriggerActorContext Actor()
    {
        Assert.True(AuthorityActorId.TryParse("owner", out var actor, out _));
        Assert.True(TriggerDeliveryFactory.TryCreateActorContext(actor, "runtime", "workspace-1", "operator", out var context, out _));
        return context!;
    }

    internal static TriggerAuthorityEvidence Authority(string revisionValue = "1", DateTimeOffset? evaluatedAtUtc = null)
    {
        Assert.True(AuthorityProfileId.TryParse("trigger-operator", out var profileId, out _));
        Assert.True(AuthorityProfileRevision.TryParse(revisionValue, out var revision, out _));
        var profile = new AuthorityProfileReference(profileId!, revision!);
        Assert.True(AuthorityBoundaryReceiptFactory.TryCreate(1, AuthorityBoundaryDecision.Direct, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)], [profile], evaluatedAtUtc ?? CreatedAtUtc.AddSeconds(2), out var receipt, out _));
        return new TriggerAuthorityEvidence(profile, receipt!);
    }

    internal static TriggerTemporalEvidence Temporal(DateTimeOffset? receivedAtUtc = null, DateTimeOffset? notBeforeUtc = null, DateTimeOffset? deadlineUtc = null, DateTimeOffset? expiresAtUtc = null)
    {
        Assert.True(TriggerDeliveryFactory.TryCreateTemporalEvidence(CreatedAtUtc.AddSeconds(1), receivedAtUtc ?? CreatedAtUtc.AddSeconds(2), CreatedAtUtc, null, notBeforeUtc, deadlineUtc, expiresAtUtc, out var temporal, out var validation), string.Join(',', validation.Errors.Select(error => error.Code)));
        return temporal!;
    }

    internal static TriggerPayloadEvidence Payload(int bytes = 1, byte value = 1)
    {
        Assert.True(TriggerDeliveryFactory.TryCreateInlinePayload(Enumerable.Repeat(value, bytes).ToArray(), out var payload, out _));
        return payload!;
    }
}
