using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Tests.Triggers;

public sealed class TriggerQueueAdmissionServiceTests
{
    [Fact]
    public async Task Immediate_mode_is_artifact_free_and_never_calls_delivery_admission()
    {
        var delivery = new AdmissionStub(new TriggerDeliveryAdmissionResult(TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted, new string('a', 64)));
        var queue = new QueueStub();
        var request = TriggerQueueAdmissionRequestFactory.Create(TriggerAdmissionTestData.Request(), TriggerQueueAdmissionMode.ImmediateOnly);

        var result = await new TriggerQueueAdmissionService(delivery, queue).AdmitAsync(request);

        Assert.Equal(TriggerQueueAdmissionStatus.ImmediateRejected, result.Status);
        Assert.Equal(TriggerQueueAdmissionReason.ImmediateModeBusy, result.Reason);
        Assert.Equal(0, delivery.Calls);
        Assert.Equal(0, queue.Calls);
    }

    [Fact]
    public async Task Admitted_and_authorized_not_before_outcomes_reach_the_durable_boundary_with_distinct_receipt_shapes()
    {
        var admittedEnvelope = TriggerAdmissionTestData.Envelope();
        var admittedHash = AssertHash(admittedEnvelope);
        var admittedQueue = new QueueStub();
        var admittedResult = new TriggerDeliveryAdmissionResult(TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted, admittedHash);
        var admitted = await new TriggerQueueAdmissionService(new AdmissionStub(admittedResult), admittedQueue).AdmitAsync(TriggerQueueAdmissionRequestFactory.Create(TriggerAdmissionTestData.Request(envelope: admittedEnvelope)));

        var future = TriggerAdmissionTestData.CreatedAtUtc.AddSeconds(5);
        var pendingEnvelope = TriggerAdmissionTestData.Envelope(temporal: TriggerAdmissionTestData.Temporal(notBeforeUtc: future));
        var pendingHash = AssertHash(pendingEnvelope);
        var pendingQueue = new QueueStub();
        var pendingResult = new TriggerDeliveryAdmissionResult(TriggerAdmissionStatus.NotYetEligible, TriggerAdmissionReason.NotBefore, pendingHash);
        var pending = await new TriggerQueueAdmissionService(new AdmissionStub(pendingResult), pendingQueue).AdmitAsync(TriggerQueueAdmissionRequestFactory.Create(TriggerAdmissionTestData.Request(envelope: pendingEnvelope)));

        Assert.Equal(TriggerQueueAdmissionStatus.Queued, admitted.Status);
        Assert.NotNull(admittedQueue.Request!.Receipt);
        Assert.Equal(TriggerAdmissionStatus.Admitted, admittedQueue.Request.AdmissionStatus);
        Assert.Equal(TriggerQueueAdmissionStatus.Queued, pending.Status);
        Assert.Null(pendingQueue.Request!.Receipt);
        Assert.Equal(TriggerAdmissionStatus.NotYetEligible, pendingQueue.Request.AdmissionStatus);
    }

    [Theory]
    [InlineData(TriggerAdmissionStatus.Unavailable, TriggerAdmissionReason.HistoryUnavailable)]
    [InlineData(TriggerAdmissionStatus.Unavailable, TriggerAdmissionReason.AdapterUnavailable)]
    public async Task Unavailable_delivery_outcomes_never_create_queue_artifacts(TriggerAdmissionStatus status, TriggerAdmissionReason reason)
    {
        var queue = new QueueStub();
        var result = await new TriggerQueueAdmissionService(new AdmissionStub(new TriggerDeliveryAdmissionResult(status, reason, new string('a', 64))), queue).AdmitAsync(TriggerQueueAdmissionRequestFactory.Create(TriggerAdmissionTestData.Request()));

        Assert.Equal(TriggerQueueAdmissionStatus.Unavailable, result.Status);
        Assert.Equal(TriggerQueueAdmissionReason.AdmissionUnavailable, result.Reason);
        Assert.Equal(0, queue.Calls);
    }

    [Fact]
    public async Task Undefined_or_unreceiptable_outcomes_fail_closed_before_persistence()
    {
        var queue = new QueueStub();
        var result = await new TriggerQueueAdmissionService(new AdmissionStub(new TriggerDeliveryAdmissionResult(TriggerAdmissionStatus.Unknown, TriggerAdmissionReason.Unknown, new string('a', 64))), queue).AdmitAsync(TriggerQueueAdmissionRequestFactory.Create(TriggerAdmissionTestData.Request()));
        Assert.Equal(TriggerQueueAdmissionStatus.Unavailable, result.Status);
        Assert.Empty(queue.Requests);
    }

    [Fact]
    public void Request_quota_order_and_result_contracts_are_bounded_and_non_dispatching()
    {
        Assert.Throws<ArgumentNullException>(() => TriggerQueueAdmissionRequestFactory.Create(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerQueueAdmissionRequestFactory.Create(TriggerAdmissionTestData.Request(), (TriggerQueueAdmissionMode)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerQueueAdmissionRequestFactory.Create(TriggerAdmissionTestData.Request(), priority: (TriggerQueuePriority)99));
        Assert.Throws<ArgumentNullException>(() => TriggerQueueQuotaValidator.Validate(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerQueueQuotaValidator.Validate(new TriggerQueueQuota(0, 1, 1, 1, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerQueueQuotaValidator.Validate(new TriggerQueueQuota(2, 1, 1, 1, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerQueueQuotaValidator.Validate(new TriggerQueueQuota(1, 1, 0, 1, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerQueueQuotaValidator.Validate(new TriggerQueueQuota(1, 1, 1, 0, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerQueueQuotaValidator.Validate(new TriggerQueueQuota(1, 1, 1, 1, 0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerQueueQuotaValidator.Validate(new TriggerQueueQuota(1, 1, 1, 1, 1, 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerQueueQuotaValidator.Validate(new TriggerQueueQuota(1, 1, 1, 1, 1, 1, 0)));
        TriggerQueueQuotaValidator.Validate(TriggerQueueQuota.Default);

        var at = TriggerAdmissionTestData.CreatedAtUtc;
        var normal = new TriggerQueueOrderKey(at, TriggerQueuePriority.Normal, at, "b");
        var critical = new TriggerQueueOrderKey(at, TriggerQueuePriority.Critical, at, "z");
        var earlier = new TriggerQueueOrderKey(at.AddTicks(-1), TriggerQueuePriority.Background, at, "z");
        var ordinal = new TriggerQueueOrderKey(at, TriggerQueuePriority.Normal, at, "a");
        Assert.True(TriggerQueueOrdering.Compare(earlier, critical) < 0);
        Assert.True(TriggerQueueOrdering.Compare(critical, normal) < 0);
        Assert.True(TriggerQueueOrdering.Compare(ordinal, normal) < 0);
        Assert.Throws<ArgumentNullException>(() => TriggerQueueOrdering.Compare(null!, normal));
        Assert.Throws<ArgumentNullException>(() => TriggerQueueOrdering.Compare(normal, null!));
    }

    [Fact]
    public async Task Not_before_classification_checks_current_authority_before_reporting_future_eligibility()
    {
        var future = TriggerAdmissionTestData.CreatedAtUtc.AddSeconds(5);
        var envelope = TriggerAdmissionTestData.Envelope(temporal: TriggerAdmissionTestData.Temporal(notBeforeUtc: future));
        var port = new TriggerDeliveryAdmissionService(new TriggerDeliveryAdmissionHistoryStub());
        var futureResult = await port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: envelope));
        var staleLoop = await port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: envelope, currentLoop: TriggerAdmissionTestData.Loop(version: 99)));
        var unavailableAdapter = await port.AdmitAsync(TriggerAdmissionTestData.Request(envelope: envelope, isAdapterAvailable: false));

        Assert.Equal(TriggerAdmissionStatus.NotYetEligible, futureResult.Status);
        Assert.Equal(TriggerAdmissionStatus.Unauthorized, staleLoop.Status);
        Assert.Equal(TriggerAdmissionReason.StaleLoop, staleLoop.Reason);
        Assert.Equal(TriggerAdmissionStatus.Unavailable, unavailableAdapter.Status);
    }

    private static string AssertHash(TriggerDeliveryEnvelope envelope)
    {
        Assert.True(TriggerDeliveryHash.TryCompute(envelope, out var hash, out _));
        return hash!;
    }

    private sealed class AdmissionStub(TriggerDeliveryAdmissionResult result) : ITriggerDeliveryAdmissionPort
    {
        public int Calls { get; private set; }

        public Task<TriggerDeliveryAdmissionResult> AdmitAsync(TriggerDeliveryAdmissionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class QueueStub : ITriggerQueueMutationPort
    {
        public List<TriggerQueueCommitRequest> Requests { get; } = [];

        public int Calls => Requests.Count;

        public TriggerQueueCommitRequest? Request => Requests.LastOrDefault();

        public Task<TriggerQueueAdmissionResult> CommitAsync(TriggerQueueCommitRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var result = new TriggerQueueAdmissionResult(TriggerQueueAdmissionStatus.Queued, TriggerQueueAdmissionReason.Enqueued, request.Envelope.DeliveryId, request.Envelope.DeduplicationId, request.CanonicalEnvelopeHash, null, request.AdmissionStatus, request.AdmissionReason);
            return Task.FromResult(result);
        }
    }
}
