using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;

namespace EmbodySense.Core.Application.Tests.Triggers;

public sealed class TriggerClosedBoundaryTests
{
    [Fact]
    public async Task Queue_admission_without_a_canonical_hash_fails_before_durable_commit()
    {
        var queue = new CountingQueue();
        var admission = new FixedAdmission(new TriggerDeliveryAdmissionResult(
            TriggerAdmissionStatus.Admitted,
            TriggerAdmissionReason.EvidenceAccepted,
            null));
        var request = TriggerQueueAdmissionRequestFactory.Create(TriggerAdmissionTestData.Request());

        var result = await new TriggerQueueAdmissionService(admission, queue).AdmitAsync(request);

        Assert.Equal(TriggerQueueAdmissionStatus.Unavailable, result.Status);
        Assert.Equal(TriggerQueueAdmissionReason.AdmissionUnavailable, result.Reason);
        Assert.Equal(0, queue.Calls);
    }

    [Theory]
    [InlineData(TriggerAdmissionStatus.Replayed, TriggerAdmissionReason.ExactReplay)]
    [InlineData(TriggerAdmissionStatus.Invalid, TriggerAdmissionReason.InvalidEnvelope)]
    public void Terminal_receipts_support_the_remaining_closed_outcome_pairs(
        TriggerAdmissionStatus status,
        TriggerAdmissionReason reason)
    {
        var envelope = TriggerAdmissionTestData.Envelope();

        var created = TriggerDeliveryAdmissionReceiptFactory.TryCreate(
            envelope,
            status,
            reason,
            TriggerAdmissionTestData.CreatedAtUtc.AddSeconds(3),
            out var receipt,
            out var validation);

        Assert.True(created, string.Join(',', validation.Errors.Select(error => error.Code)));
        Assert.Equal(status, receipt!.Status);
        Assert.Equal(reason, receipt.Reason);
    }

    [Fact]
    public void Public_trigger_snapshots_and_schedule_create_requests_preserve_exact_evidence()
    {
        var definition = Schedules.ScheduleEvaluatorTestData.Definition();
        Assert.True(ScheduleContractHash.TryComputeDefinition(definition, out var definitionHash, out _));
        var state = Schedules.ScheduleEvaluatorTestData.State(definition);
        var create = new ScheduleStoreCreateRequest(definition, state, definitionHash!);
        var quota = TriggerQueueQuota.Default;
        var snapshot = new TriggerQueueSnapshot(1, 7, quota, 0, 0, 0, 0, 0, 0, 0, false, []);
        var cancellation = new TriggerQueueCancellationResult(TriggerQueueCancellationStatus.NotFound, null);

        Assert.Same(definition, create.Definition);
        Assert.Same(state, create.InitialState);
        Assert.Equal(definitionHash, create.CanonicalDefinitionHash);
        Assert.Equal(7, snapshot.Generation);
        Assert.Same(quota, snapshot.Quota);
        Assert.Empty(snapshot.Entries);
        Assert.Equal(TriggerQueueCancellationStatus.NotFound, cancellation.Status);
        Assert.Null(cancellation.Entry);
    }

    private sealed class FixedAdmission(TriggerDeliveryAdmissionResult result) : ITriggerDeliveryAdmissionPort
    {
        public Task<TriggerDeliveryAdmissionResult> AdmitAsync(
            TriggerDeliveryAdmissionRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class CountingQueue : ITriggerQueueMutationPort
    {
        internal int Calls { get; private set; }

        public Task<TriggerQueueAdmissionResult> CommitAsync(
            TriggerQueueCommitRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("The malformed admission must not reach durability.");
        }
    }
}
