using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Common.Tests;

internal static class TriggerDeliveryTestData
{
    internal static readonly DateTimeOffset CreatedAtUtc = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    internal static TriggerDeliveryEnvelope Envelope(
        string deliveryId = "delivery-1",
        string deduplicationId = "dedup-1",
        TriggerKind kind = TriggerKind.Webhook,
        TriggerAdapterReference? adapter = null,
        TriggerLoopReference? loop = null,
        TriggerActorContext? actorContext = null,
        TriggerAuthorityEvidence? authority = null,
        TriggerTemporalEvidence? temporal = null,
        TriggerPayloadEvidence? payload = null,
        TriggerRedeliveryEvidence? redelivery = null,
        bool publicationRequested = false,
        CustomLoopConversationReference? conversation = null,
        TriggerAdmissionStatus visibleStatus = TriggerAdmissionStatus.Unknown,
        TriggerAdmissionReason visibleReason = TriggerAdmissionReason.Unknown)
    {
        Assert.True(TriggerDeliveryId.TryParse(deliveryId, out var delivery));
        Assert.True(TriggerDeduplicationId.TryParse(deduplicationId, out var deduplication));
        adapter ??= Adapter();
        loop ??= Loop();
        actorContext ??= ActorContext();
        authority ??= Authority();
        temporal ??= Temporal(admittedAtUtc: visibleStatus is TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed ? CreatedAtUtc.AddSeconds(3) : null);
        payload ??= InlinePayload();
        if (redelivery is null)
        {
            Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(1, 1, delivery, out redelivery, out _));
        }

        Assert.True(TriggerDeliveryFactory.TryCreateEnvelope(1, delivery, deduplication, kind, adapter, loop, actorContext, authority, temporal, payload, redelivery, publicationRequested, conversation, visibleStatus, visibleReason, out var envelope, out var validation), string.Join(',', validation.Errors.Select(error => error.Code)));
        return envelope!;
    }

    internal static TriggerAdapterReference Adapter(string id = "org.embodysense/triggers/webhook", string version = "1.0.0", string hashCharacter = "a", string implementation = "triggers/webhook", string provider = "org.embodysense")
    {
        Assert.True(CapabilityId.TryParse(id, out var capabilityId, out _));
        Assert.True(CapabilityVersion.TryParse(version, out var capabilityVersion, out _));
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + new string(hashCharacter[0], 64), out var hash, out _));
        Assert.True(CapabilityProviderId.TryParse(provider, out var providerId, out _));
        return new TriggerAdapterReference(new CapabilityDescriptorIdentity(capabilityId!, capabilityVersion!, hash!), new CapabilityImplementationIdentity(providerId!, implementation));
    }

    internal static TriggerLoopReference Loop(string id = "loop-1", int version = 3, char hashCharacter = 'b')
    {
        Assert.True(TriggerDeliveryFactory.TryCreateLoopReference(id, version, new string(hashCharacter, 64), out var loop, out _));
        return loop!;
    }

    internal static TriggerActorContext ActorContext(string actor = "owner", string surface = "runtime", string workspace = "workspace-1", string role = "operator")
    {
        Assert.True(AuthorityActorId.TryParse(actor, out var actorId, out _));
        Assert.True(TriggerDeliveryFactory.TryCreateActorContext(actorId, surface, workspace, role, out var context, out _));
        return context!;
    }

    internal static TriggerAuthorityEvidence Authority(DateTimeOffset? evaluatedAtUtc = null, AuthorityBoundaryDecision decision = AuthorityBoundaryDecision.Direct, string profileIdText = "trigger-operator", int revisionValue = 7, bool reverseProfiles = false)
    {
        Assert.True(AuthorityProfileId.TryParse(profileIdText, out var profileId, out _));
        Assert.True(AuthorityProfileRevision.TryParse(revisionValue.ToString(System.Globalization.CultureInfo.InvariantCulture), out var revision, out _));
        var profile = new AuthorityProfileReference(profileId!, revision!);
        Assert.True(AuthorityProfileId.TryParse("workspace-base", out var otherProfileId, out _));
        Assert.True(AuthorityProfileRevision.TryParse("2", out var otherRevision, out _));
        var other = new AuthorityProfileReference(otherProfileId!, otherRevision!);
        var profiles = reverseProfiles ? new[] { profile, other } : new[] { other, profile };
        var reason = decision switch
        {
            AuthorityBoundaryDecision.Direct => AuthorityBoundaryReason.NoBoundary,
            AuthorityBoundaryDecision.Review => AuthorityBoundaryReason.MandatoryReview,
            AuthorityBoundaryDecision.Pause => AuthorityBoundaryReason.UncertainUserIntent,
            _ => AuthorityBoundaryReason.InvalidContract
        };
        Assert.True(AuthorityBoundaryReceiptFactory.TryCreate(1, decision, [new AuthorityBoundaryCondition(decision, reason)], profiles, evaluatedAtUtc ?? CreatedAtUtc.AddSeconds(2), out var receipt, out var validation), string.Join(',', validation.Errors.Select(error => error.Code)));
        return new TriggerAuthorityEvidence(profile, receipt!);
    }

    internal static TriggerTemporalEvidence Temporal(DateTimeOffset? createdAtUtc = null, DateTimeOffset? observedAtUtc = null, DateTimeOffset? receivedAtUtc = null, DateTimeOffset? admittedAtUtc = null, DateTimeOffset? notBeforeUtc = null, DateTimeOffset? deadlineUtc = null, DateTimeOffset? expiresAtUtc = null)
    {
        var created = createdAtUtc ?? CreatedAtUtc;
        Assert.True(TriggerDeliveryFactory.TryCreateTemporalEvidence(observedAtUtc ?? created.AddSeconds(1), receivedAtUtc ?? created.AddSeconds(2), created, admittedAtUtc, notBeforeUtc, deadlineUtc, expiresAtUtc, out var temporal, out var validation), string.Join(',', validation.Errors.Select(error => error.Code)));
        return temporal!;
    }

    internal static TriggerPayloadEvidence InlinePayload(byte[]? bytes = null)
    {
        Assert.True(TriggerDeliveryFactory.TryCreateInlinePayload(bytes ?? [1, 2, 3], out var payload, out _));
        return payload!;
    }

    internal static TriggerPayloadEvidence ReferencedPayload(string reference = "payload/artifact-1", byte[]? content = null)
    {
        var hash = CapabilityIntegrityDigest.Compute(content ?? [4, 5, 6]);
        Assert.True(TriggerDeliveryFactory.TryCreateReferencedPayload(reference, hash, out var payload, out _));
        return payload!;
    }
}
