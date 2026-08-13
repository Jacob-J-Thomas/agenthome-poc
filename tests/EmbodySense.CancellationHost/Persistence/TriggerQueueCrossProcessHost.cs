using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Triggers;

namespace EmbodySense.CancellationHost.Persistence;

internal static class TriggerQueueCrossProcessHost
{
    private static readonly DateTimeOffset _createdAtUtc = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    internal static async Task<int> RunAdmissionAsync(
        string workspaceRoot,
        string releaseMarker,
        string readyMarker,
        string resultMarker,
        string deliveryId,
        string deduplicationId,
        string loopId,
        string crashBoundary)
    {
        var request = CreateQueueRequest(deliveryId, deduplicationId, loopId);
        ITriggerQueueDurabilityObserver? observer = crashBoundary switch
        {
            "none" => null,
            "staged" => new TriggerQueueCrashObserver(crashAfterStaged: true),
            "precursor" => new TriggerQueueCrashObserver(crashAfterStaged: false),
            _ => throw new ArgumentOutOfRangeException(nameof(crashBoundary), crashBoundary, "The trigger queue crash boundary is not supported.")
        };
        var store = new TriggerQueueStore(new WorkspacePaths(workspaceRoot), RaceQuota(), observer);
        var service = new TriggerQueueAdmissionService(new TriggerDeliveryAdmissionService(store), store);

        await CrossProcessMarkerProtocol.SignalReadyAndWaitForReleaseAsync(readyMarker, releaseMarker);
        var result = await service.AdmitAsync(request);
        await CrossProcessMarkerProtocol.WriteResultAsync(resultMarker, result.Status.ToString());
        return 0;
    }

    internal static async Task<int> RunWorkerSelectionAsync(
        string workspaceRoot,
        string releaseMarker,
        string readyMarker,
        string resultMarker,
        string workerId,
        string expectedGenerationText)
    {
        if (!long.TryParse(expectedGenerationText, System.Globalization.CultureInfo.InvariantCulture, out var expectedGeneration))
        {
            throw new ArgumentException("The expected trigger queue generation is not a canonical integer.", nameof(expectedGenerationText));
        }

        var request = new TriggerWorkerSelectionRequest(workerId, expectedGeneration, _createdAtUtc.AddSeconds(4), TimeSpan.FromSeconds(30), [], 2);
        var store = new TriggerQueueStore(new WorkspacePaths(workspaceRoot));
        await CrossProcessMarkerProtocol.SignalReadyAndWaitForReleaseAsync(readyMarker, releaseMarker);
        var result = await store.SelectAsync(request);
        await CrossProcessMarkerProtocol.WriteResultAsync(resultMarker, result.Status.ToString());
        return 0;
    }

    internal static async Task<int> RunLockHolderAsync(string workspaceRoot, string releaseMarker, string readyMarker, string resultMarker)
    {
        var observer = new TriggerQueueLockHoldingObserver(readyMarker, releaseMarker);
        var store = new TriggerQueueStore(new WorkspacePaths(workspaceRoot), observer: observer);
        _ = await store.GetSnapshotAsync(_createdAtUtc.AddSeconds(3));
        await CrossProcessMarkerProtocol.WriteResultAsync(resultMarker, "released");
        return 0;
    }

    private static TriggerQueueAdmissionRequest CreateQueueRequest(string deliveryId, string deduplicationId, string loopId)
    {
        var envelope = CreateEnvelope(deliveryId, deduplicationId, loopId);
        if (!TriggerDeliveryAdmissionRequestFactory.TryCreate(
            envelope,
            envelope.Loop,
            envelope.Adapter,
            isAdapterAvailable: true,
            envelope.ActorContext,
            envelope.Authority,
            _createdAtUtc.AddSeconds(3),
            out var deliveryRequest,
            out var validation))
        {
            throw new InvalidOperationException(string.Join(',', validation.Errors.Select(error => error.Code)));
        }

        return TriggerQueueAdmissionRequestFactory.Create(deliveryRequest!, TriggerQueueAdmissionMode.Queued, TriggerQueuePriority.Normal);
    }

    private static TriggerDeliveryEnvelope CreateEnvelope(string deliveryId, string deduplicationId, string loopId)
    {
        if (!TriggerDeliveryId.TryParse(deliveryId, out var delivery)
            || !TriggerDeduplicationId.TryParse(deduplicationId, out var deduplication)
            || !TriggerDeliveryFactory.TryCreateRedeliveryEvidence(1, 1, delivery, out var redelivery, out _)
            || !TriggerDeliveryFactory.TryCreateLoopReference(loopId, 1, new string('b', 64), out var loop, out _)
            || !AuthorityActorId.TryParse("owner", out var actor, out _)
            || !TriggerDeliveryFactory.TryCreateActorContext(actor, "runtime", "workspace-1", "operator", out var actorContext, out _)
            || !AuthorityProfileId.TryParse("trigger-operator", out var profileId, out _)
            || !AuthorityProfileRevision.TryParse("1", out var profileRevision, out _))
        {
            throw new InvalidOperationException("The trigger queue child-host fixture identity is invalid.");
        }

        var adapter = CreateAdapter();
        var profile = new AuthorityProfileReference(profileId!, profileRevision!);
        if (!AuthorityBoundaryReceiptFactory.TryCreate(
            1,
            AuthorityBoundaryDecision.Direct,
            [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)],
            [profile],
            _createdAtUtc.AddSeconds(2),
            out var receipt,
            out _)
            || !TriggerDeliveryFactory.TryCreateTemporalEvidence(
                _createdAtUtc.AddSeconds(1),
                _createdAtUtc.AddSeconds(2),
                _createdAtUtc,
                null,
                null,
                null,
                null,
                out var temporal,
                out _)
            || !TriggerDeliveryFactory.TryCreateInlinePayload([1], out var payload, out _))
        {
            throw new InvalidOperationException("The trigger queue child-host fixture evidence is invalid.");
        }

        var authority = new TriggerAuthorityEvidence(profile, receipt!);
        if (!TriggerDeliveryFactory.TryCreateEnvelope(
            TriggerDeliveryEnvelope.CurrentSchemaVersion,
            delivery,
            deduplication,
            TriggerKind.Webhook,
            adapter,
            loop,
            actorContext,
            authority,
            temporal,
            payload,
            redelivery,
            publicationRequested: false,
            invokingConversation: null,
            TriggerAdmissionStatus.Unknown,
            TriggerAdmissionReason.Unknown,
            out var envelope,
            out var validation))
        {
            throw new InvalidOperationException(string.Join(',', validation.Errors.Select(error => error.Code)));
        }

        return envelope!;
    }

    private static TriggerAdapterReference CreateAdapter()
    {
        if (!CapabilityId.TryParse("org.embodysense/triggers/webhook", out var id, out _)
            || !CapabilityVersion.TryParse("1.0.0", out var version, out _)
            || !CapabilityDescriptorHash.TryParse("sha256:" + new string('a', 64), out var hash, out _)
            || !CapabilityProviderId.TryParse("org.embodysense", out var provider, out _))
        {
            throw new InvalidOperationException("The trigger queue child-host adapter fixture is invalid.");
        }

        return new TriggerAdapterReference(new CapabilityDescriptorIdentity(id!, version!, hash!), new CapabilityImplementationIdentity(provider!, "triggers/webhook"));
    }

    private static TriggerQueueQuota RaceQuota()
        => new(1, 4, 128 * 1024, 128 * 1024, 512 * 1024, 1);
}
