using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Startup.Tests.Triggers;

internal static class TriggerWorkerTestData
{
    internal static readonly DateTimeOffset CreatedAtUtc = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    internal static TriggerDeliveryEnvelope Envelope(TriggerPayloadEvidence? payload = null)
    {
        Assert.True(TriggerDeliveryId.TryParse("delivery-1", out var delivery));
        Assert.True(TriggerDeduplicationId.TryParse("dedup-1", out var deduplication));
        Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(1, 1, delivery, out var redelivery, out _));
        Assert.True(CapabilityId.TryParse("org.embodysense/triggers/webhook", out var capabilityId, out _));
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var capabilityVersion, out _));
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + new string('a', 64), out var descriptorHash, out _));
        Assert.True(CapabilityProviderId.TryParse("org.embodysense", out var providerId, out _));
        var adapter = new TriggerAdapterReference(new CapabilityDescriptorIdentity(capabilityId!, capabilityVersion!, descriptorHash!), new CapabilityImplementationIdentity(providerId!, "triggers/webhook"));
        Assert.True(TriggerDeliveryFactory.TryCreateLoopReference("loop-1", 1, new string('b', 64), out var loop, out _));
        Assert.True(AuthorityActorId.TryParse("owner", out var actorId, out _));
        Assert.True(TriggerDeliveryFactory.TryCreateActorContext(actorId, "runtime", "workspace-1", "operator", out var actor, out _));
        Assert.True(AuthorityProfileId.TryParse("trigger-operator", out var profileId, out _));
        Assert.True(AuthorityProfileRevision.TryParse("1", out var profileRevision, out _));
        var profile = new AuthorityProfileReference(profileId!, profileRevision!);
        Assert.True(AuthorityBoundaryReceiptFactory.TryCreate(1, AuthorityBoundaryDecision.Direct, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)], [profile], CreatedAtUtc.AddSeconds(2), out var receipt, out _));
        var authority = new TriggerAuthorityEvidence(profile, receipt!);
        Assert.True(TriggerDeliveryFactory.TryCreateTemporalEvidence(CreatedAtUtc.AddSeconds(1), CreatedAtUtc.AddSeconds(2), CreatedAtUtc, null, null, null, null, out var temporal, out _));
        payload ??= InlinePayload("dispatch"u8.ToArray());
        Assert.True(TriggerDeliveryFactory.TryCreateEnvelope(1, delivery, deduplication, TriggerKind.Webhook, adapter, loop, actor, authority, temporal, payload, redelivery, false, null, TriggerAdmissionStatus.Unknown, TriggerAdmissionReason.Unknown, out var envelope, out _));
        return envelope!;
    }

    internal static TriggerPayloadEvidence InlinePayload(byte[] bytes)
    {
        Assert.True(TriggerDeliveryFactory.TryCreateInlinePayload(bytes, out var payload, out _));
        return payload!;
    }
}
