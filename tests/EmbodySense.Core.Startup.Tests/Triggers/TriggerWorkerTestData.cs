using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Startup.Capabilities;

namespace EmbodySense.Core.Startup.Tests.Triggers;

internal static class TriggerWorkerTestData
{
    internal static readonly DateTimeOffset CreatedAtUtc = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    internal static TriggerDeliveryEnvelope Envelope(TriggerPayloadEvidence? payload = null, TriggerLoopReference? loop = null, TriggerActorContext? actorContext = null)
    {
        Assert.True(TriggerDeliveryId.TryParse("delivery-1", out var delivery));
        Assert.True(TriggerDeduplicationId.TryParse("dedup-1", out var deduplication));
        Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(1, 1, delivery, out var redelivery, out _));
        Assert.True(CapabilityId.TryParse("org.embodysense/triggers/webhook", out var capabilityId, out _));
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var capabilityVersion, out _));
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + new string('a', 64), out var descriptorHash, out _));
        Assert.True(CapabilityProviderId.TryParse("org.embodysense", out var providerId, out _));
        var adapter = new TriggerAdapterReference(new CapabilityDescriptorIdentity(capabilityId!, capabilityVersion!, descriptorHash!), new CapabilityImplementationIdentity(providerId!, "triggers/webhook"));
        if (loop is null)
        {
            Assert.True(TriggerDeliveryFactory.TryCreateLoopReference("loop-1", 1, new string('b', 64), out loop, out _));
        }

        if (actorContext is null)
        {
            Assert.True(AuthorityActorId.TryParse("owner", out var actorId, out _));
            Assert.True(TriggerDeliveryFactory.TryCreateActorContext(actorId, "runtime", new string('1', 64), "operator", out actorContext, out _));
        }

        Assert.True(AuthorityProfileId.TryParse("trigger-operator", out var profileId, out _));
        Assert.True(AuthorityProfileRevision.TryParse("1", out var profileRevision, out _));
        var profile = new AuthorityProfileReference(profileId!, profileRevision!);
        Assert.True(AuthorityBoundaryReceiptFactory.TryCreate(1, AuthorityBoundaryDecision.Direct, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)], [profile], CreatedAtUtc.AddSeconds(2), out var receipt, out _));
        var authority = new TriggerAuthorityEvidence(profile, receipt!);
        Assert.True(TriggerDeliveryFactory.TryCreateTemporalEvidence(CreatedAtUtc.AddSeconds(1), CreatedAtUtc.AddSeconds(2), CreatedAtUtc, null, null, null, null, out var temporal, out _));
        payload ??= InlinePayload("dispatch"u8.ToArray());
        Assert.True(TriggerDeliveryFactory.TryCreateEnvelope(1, delivery, deduplication, TriggerKind.Webhook, adapter, loop, actorContext, authority, temporal, payload, redelivery, false, null, TriggerAdmissionStatus.Unknown, TriggerAdmissionReason.Unknown, out var envelope, out _));
        return envelope!;
    }

    internal static TriggerDeliveryEnvelope ScheduleEnvelope(
        TriggerLoopReference loop,
        TriggerActorContext actorContext,
        TriggerPayloadEvidence? payload = null)
    {
        Assert.True(ScheduleId.TryParse("trigger-worker-schedule", out var scheduleId));
        var occurrence = new ScheduleOccurrence(
            ScheduleOccurrence.CurrentSchemaVersion,
            1,
            DateTime.SpecifyKind(CreatedAtUtc.UtcDateTime, DateTimeKind.Unspecified),
            CreatedAtUtc,
            new ScheduleTimeZoneReference("Etc/UTC", new string('f', 64)));
        Assert.True(ScheduleIdentityDerivation.TryDerive(scheduleId, 1, new string('b', 64), occurrence, out var identity, out _));
        Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(1, 1, identity!.DeliveryId, out var redelivery, out _));
        var descriptor = Assert.Single(BuiltInCapabilityCatalog.Descriptors, item => item.Id.Value == "org.embodysense/triggers/time");
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var descriptorIdentity, out _));
        var adapter = new TriggerAdapterReference(descriptorIdentity!, descriptor.Implementation);
        Assert.True(AuthorityProfileId.TryParse("trigger-operator", out var profileId, out _));
        Assert.True(AuthorityProfileRevision.TryParse("1", out var profileRevision, out _));
        var profile = new AuthorityProfileReference(profileId!, profileRevision!);
        Assert.True(AuthorityBoundaryReceiptFactory.TryCreate(
            1,
            AuthorityBoundaryDecision.Direct,
            [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)],
            [profile],
            CreatedAtUtc.AddSeconds(2),
            out var receipt,
            out _));
        var authority = new TriggerAuthorityEvidence(profile, receipt!);
        Assert.True(TriggerDeliveryFactory.TryCreateTemporalEvidence(
            CreatedAtUtc.AddSeconds(1),
            CreatedAtUtc.AddSeconds(2),
            CreatedAtUtc,
            null,
            null,
            null,
            null,
            out var temporal,
            out _));
        payload ??= InlinePayload("dispatch"u8.ToArray());
        Assert.True(TriggerDeliveryFactory.TryCreateEnvelope(
            1,
            identity.DeliveryId,
            identity.DeduplicationId,
            TriggerKind.Time,
            adapter,
            loop,
            actorContext,
            authority,
            temporal,
            payload,
            redelivery,
            false,
            null,
            TriggerAdmissionStatus.Unknown,
            TriggerAdmissionReason.Unknown,
            out var envelope,
            out _));
        return envelope!;
    }

    internal static TriggerPayloadEvidence InlinePayload(byte[] bytes)
    {
        Assert.True(TriggerDeliveryFactory.TryCreateInlinePayload(bytes, out var payload, out _));
        return payload!;
    }

    internal static TriggerLoopReference GovernedLoop(
        string graphId = "graph-1",
        string revisionId = "revision-3",
        char executableHash = 'c',
        string publicationOperationId = "publish-3",
        char validationHash = 'd',
        string grantId = "grant-1",
        int grantRevision = 2,
        char grantHash = 'e')
    {
        var revision = GovernedLoopRevisionReference.Create(1, graphId, revisionId, new string(executableHash, 64));
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, revision, publicationOperationId, new string(validationHash, 64));
        Assert.True(AuthorityGrantId.TryParse(grantId, out var parsedGrantId, out _));
        Assert.True(AuthorityGrantRevision.TryParse(grantRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), out var parsedGrantRevision, out _));
        var grant = new AuthorityGrantReference(parsedGrantId!, parsedGrantRevision!, "sha256:" + new string(grantHash, 64));
        Assert.True(TriggerDeliveryFactory.TryCreateGovernedLoopReference(publication, grant, out var loop, out var validation), string.Join(',', validation.Errors.Select(error => error.Code)));
        return loop!;
    }
}
