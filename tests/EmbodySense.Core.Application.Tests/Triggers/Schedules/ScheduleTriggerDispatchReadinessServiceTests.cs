using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;

namespace EmbodySense.Core.Application.Tests.Triggers.Schedules;

public sealed class ScheduleTriggerDispatchReadinessServiceTests
{
    [Theory]
    [InlineData(ScheduleDeliveryProvenanceStatus.PendingFinalization, TriggerWorkerDispatchReadinessStatus.RetryAfterScheduleFinalization)]
    [InlineData(ScheduleDeliveryProvenanceStatus.Found, TriggerWorkerDispatchReadinessStatus.Ready)]
    [InlineData(ScheduleDeliveryProvenanceStatus.NotFound, TriggerWorkerDispatchReadinessStatus.Ready)]
    [InlineData(ScheduleDeliveryProvenanceStatus.Conflict, TriggerWorkerDispatchReadinessStatus.Ready)]
    [InlineData(ScheduleDeliveryProvenanceStatus.Unavailable, TriggerWorkerDispatchReadinessStatus.RequiresAttention)]
    [InlineData(ScheduleDeliveryProvenanceStatus.Corrupt, TriggerWorkerDispatchReadinessStatus.RequiresAttention)]
    [InlineData(ScheduleDeliveryProvenanceStatus.Backpressured, TriggerWorkerDispatchReadinessStatus.RequiresAttention)]
    [InlineData(ScheduleDeliveryProvenanceStatus.Ambiguous, TriggerWorkerDispatchReadinessStatus.RequiresAttention)]
    [InlineData(ScheduleDeliveryProvenanceStatus.Unknown, TriggerWorkerDispatchReadinessStatus.RequiresAttention)]
    [InlineData((ScheduleDeliveryProvenanceStatus)99, TriggerWorkerDispatchReadinessStatus.RequiresAttention)]
    public async Task Exact_schedule_provenance_maps_to_one_closed_pre_intent_disposition(
        ScheduleDeliveryProvenanceStatus provenance,
        TriggerWorkerDispatchReadinessStatus expected)
    {
        var source = new ProvenanceStub(provenance);
        var service = new ScheduleTriggerDispatchReadinessService(source);

        var result = await service.CheckAsync(ScheduleEnvelope());

        Assert.Equal(expected, result.Status);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Missing_provenance_result_requires_attention()
    {
        var service = new ScheduleTriggerDispatchReadinessService(new ProvenanceStub());

        var result = await service.CheckAsync(ScheduleEnvelope());

        Assert.Equal(TriggerWorkerDispatchReadinessStatus.RequiresAttention, result.Status);
    }

    [Fact]
    public async Task Non_schedule_delivery_bypasses_schedule_provenance_without_widening_other_triggers()
    {
        var source = new ProvenanceStub(new IOException("must not be read"));
        var service = new ScheduleTriggerDispatchReadinessService(source);

        var result = await service.CheckAsync(TriggerAdmissionTestData.Envelope());

        Assert.Equal(TriggerWorkerDispatchReadinessStatus.Ready, result.Status);
        Assert.Equal(0, source.Calls);
    }

    private static TriggerDeliveryEnvelope ScheduleEnvelope()
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/triggers/time", out var capabilityId, out _));
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var capabilityVersion, out _));
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + new string('a', 64), out var descriptorHash, out _));
        Assert.True(CapabilityProviderId.TryParse("org.embodysense", out var providerId, out _));
        var adapter = new TriggerAdapterReference(
            new CapabilityDescriptorIdentity(capabilityId!, capabilityVersion!, descriptorHash!),
            new CapabilityImplementationIdentity(providerId!, "triggers/time"));
        var definition = ScheduleEvaluatorTestData.Definition() with { TimeAdapter = adapter };
        Assert.True(ScheduleContractHash.TryComputeDefinition(definition, out var definitionHash, out _));
        var occurrence = ScheduleEvaluatorTestData.Occurrence(timeZone: definition.TimeZone);
        Assert.True(ScheduleIdentityDerivation.TryDerive(
            definition.ScheduleId,
            definition.Revision,
            definitionHash!,
            occurrence,
            out var identity,
            out _));
        Assert.True(TriggerDeliveryFactory.TryCreateActorContext(
            definition.ActorId,
            definition.SurfaceId,
            definition.WorkspaceId,
            definition.RoleId,
            out var actor,
            out _));
        Assert.True(TriggerDeliveryFactory.TryCreateTemporalEvidence(
            ScheduleEvaluatorTestData.Now,
            ScheduleEvaluatorTestData.Now,
            occurrence.ScheduledAtUtc,
            null,
            null,
            null,
            null,
            out var temporal,
            out _));
        Assert.True(TriggerDeliveryFactory.TryCreateInlinePayload(
            ScheduleEvaluatorTestData.Payload,
            out var payload,
            out _));
        Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(
            1,
            1,
            identity!.DeliveryId,
            out var redelivery,
            out _));
        var authority = TriggerAdmissionTestData.Authority(evaluatedAtUtc: ScheduleEvaluatorTestData.Now);
        Assert.Equal(definition.AuthorityProfile, authority.Profile);
        Assert.True(TriggerDeliveryFactory.TryCreateEnvelope(
            TriggerDeliveryEnvelope.CurrentSchemaVersion,
            identity.DeliveryId,
            identity.DeduplicationId,
            TriggerKind.Time,
            definition.TimeAdapter,
            definition.Target,
            actor,
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

    private sealed class ProvenanceStub : IScheduleDeliveryProvenancePort
    {
        private readonly ScheduleDeliveryProvenanceStatus _status;
        private readonly Exception? _exception;
        private readonly bool _returnNull;

        internal ProvenanceStub() => _returnNull = true;

        internal ProvenanceStub(ScheduleDeliveryProvenanceStatus status) => _status = status;

        internal ProvenanceStub(Exception exception) => _exception = exception;

        internal int Calls { get; private set; }

        public Task<ScheduleDeliveryProvenanceResult> ResolveAsync(
            TriggerDeliveryEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return _returnNull
                ? Task.FromResult<ScheduleDeliveryProvenanceResult>(null!)
                : _exception is null
                ? Task.FromResult(new ScheduleDeliveryProvenanceResult(_status, null))
                : Task.FromException<ScheduleDeliveryProvenanceResult>(_exception);
        }
    }
}
