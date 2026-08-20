using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Startup.Triggers;
using EmbodySense.Core.Startup.Triggers.Models;

namespace EmbodySense.Core.Startup.Tests.Triggers;

public sealed class TriggerGovernedLoopDispatcherTests
{
    private static readonly DateTimeOffset _workerAtUtc = TriggerWorkerTestData.CreatedAtUtc.AddSeconds(4);

    [Fact]
    public void Closed_target_protocols_accept_only_their_exact_arm_and_preserve_canonical_pins_and_context()
    {
        var legacyEnvelope = TriggerWorkerTestData.Envelope();
        var governedEnvelope = TriggerWorkerTestData.Envelope(loop: TriggerWorkerTestData.GovernedLoop());
        var lease = Lease();
        var legacyIntent = Intent(legacyEnvelope, lease);
        var governedIntent = Intent(governedEnvelope, lease);

        var legacy = TriggerCustomLoopDispatchProtocol.Prepare(legacyEnvelope, legacyIntent);
        var governed = TriggerGovernedLoopDispatchProtocol.Prepare(governedEnvelope, governedIntent);
        var governedThroughLegacy = TriggerCustomLoopDispatchProtocol.Prepare(governedEnvelope, governedIntent);
        var legacyThroughGoverned = TriggerGovernedLoopDispatchProtocol.Prepare(legacyEnvelope, legacyIntent);

        Assert.NotNull(legacy.Input);
        Assert.NotNull(governed.Input);
        Assert.Equal(governedEnvelope.Loop.GovernedPublication, governed.Input!.Publication);
        Assert.Equal(governedEnvelope.Loop.AuthorityGrant, governed.Input.AuthorityGrant);
        Assert.Equal(governedIntent.OperationId, governed.Input.OperationId);
        Assert.Same(governedEnvelope.ActorContext, governed.ActorContext);
        Assert.Equal(TriggerDispatchOutcome.NeedsReview, governedThroughLegacy.Rejection!.Outcome);
        Assert.Equal(TriggerDispatchOutcome.NeedsReview, legacyThroughGoverned.Rejection!.Outcome);
    }

    [Fact]
    public void Canonical_preparation_rejects_non_inline_and_invalid_utf8_without_invocation()
    {
        var target = TriggerWorkerTestData.GovernedLoop();
        var referenced = TriggerWorkerTestData.Envelope(payload: ReferencedPayload(), loop: target);
        var invalid = TriggerWorkerTestData.Envelope(payload: TriggerWorkerTestData.InlinePayload([0xff]), loop: target);

        var referencedResult = TriggerGovernedLoopDispatchProtocol.Prepare(referenced, Intent(referenced, Lease()));
        var invalidResult = TriggerGovernedLoopDispatchProtocol.Prepare(invalid, Intent(invalid, Lease()));

        Assert.Equal(TriggerDispatchOutcome.Rejected, referencedResult.Rejection!.Outcome);
        Assert.Equal(TriggerDispatchOutcome.Rejected, invalidResult.Rejection!.Outcome);
        Assert.Null(referencedResult.Input);
        Assert.Null(invalidResult.Input);
    }

    [Fact]
    public void Exact_canonical_rejection_is_provider_free_and_maps_from_terminal_evidence()
    {
        var envelope = TriggerWorkerTestData.Envelope(loop: TriggerWorkerTestData.GovernedLoop());
        var intent = Intent(envelope, Lease());
        var response = CanonicalResponse(envelope, intent, disposition: "Rejected");

        var result = TriggerGovernedLoopDispatchProtocol.Map(envelope, intent, response);

        Assert.False(response.WasDispatched);
        Assert.Null(response.Run);
        Assert.Equal(TriggerDispatchOutcome.Rejected, result.Outcome);
        Assert.Null(result.GovernedInvocation);
    }

    [Theory]
    [InlineData("OverlapSkipped")]
    [InlineData("OverlapDeferred")]
    [InlineData("OverlapSerialized")]
    [InlineData("DeferredOneSuppressed")]
    [InlineData("Retired")]
    public void Atomic_schedule_overlap_dispositions_are_provider_free_and_closed(string disposition)
    {
        var envelope = ScheduledEnvelope();
        var intent = Intent(envelope, Lease());
        var response = CanonicalResponse(envelope, intent) with
        {
            Status = disposition,
            MaterializationStatus = disposition,
            ExecutionStatus = null,
            WasDispatched = false,
            Run = null,
        };

        var result = TriggerGovernedLoopDispatchProtocol.Map(envelope, intent, response);

        Assert.Equal(TriggerDispatchOutcome.Rejected, result.Outcome);
        Assert.Contains(disposition, result.Detail, StringComparison.Ordinal);
        Assert.Null(result.GovernedInvocation);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("execution")]
    [InlineData("dispatch")]
    [InlineData("failure")]
    public void Contradictory_schedule_overlap_projection_requires_review(string mismatch)
    {
        var envelope = ScheduledEnvelope();
        var intent = Intent(envelope, Lease());
        var response = CanonicalResponse(envelope, intent) with
        {
            Status = "OverlapSkipped",
            MaterializationStatus = "OverlapSkipped",
            ExecutionStatus = null,
            WasDispatched = false,
            Run = null,
        };
        response = mismatch switch
        {
            "status" => response with { Status = "Executed" },
            "execution" => response with { ExecutionStatus = "Completed" },
            "dispatch" => response with { WasDispatched = true },
            _ => response with { AdmissionFailureCode = "SubstitutedFailure" },
        };

        var result = TriggerGovernedLoopDispatchProtocol.Map(envelope, intent, response);

        Assert.Equal(TriggerDispatchOutcome.NeedsReview, result.Outcome);
        Assert.Null(result.GovernedInvocation);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("publication")]
    [InlineData("grant")]
    [InlineData("actor")]
    [InlineData("surface")]
    [InlineData("workspace")]
    [InlineData("role")]
    [InlineData("request")]
    [InlineData("run")]
    [InlineData("projected-status")]
    [InlineData("projected-failure")]
    [InlineData("coordination")]
    [InlineData("materialization")]
    public void Missing_malformed_substituted_or_contradictory_canonical_evidence_needs_review(string mismatch)
    {
        var envelope = TriggerWorkerTestData.Envelope(loop: TriggerWorkerTestData.GovernedLoop());
        var intent = Intent(envelope, Lease());
        var response = CanonicalResponse(envelope, intent);
        var admission = response.AdmissionOutcome!;
        response = mismatch switch
        {
            "missing" => response with { AdmissionOutcome = null },
            "malformed" => response with { AdmissionOutcome = admission with { OutcomeHash = "INVALID" } },
            "publication" => response with { AdmissionOutcome = admission with { Publication = TriggerWorkerTestData.GovernedLoop(revisionId: "revision-4").GovernedPublication! } },
            "grant" => response with { AdmissionOutcome = admission with { AuthorityGrant = TriggerWorkerTestData.GovernedLoop(grantRevision: 3).AuthorityGrant! } },
            "actor" => response with { AdmissionOutcome = admission with { ActorId = "other-actor" } },
            "surface" => response with { AdmissionOutcome = admission with { Surface = "other-surface" } },
            "workspace" => response with { AdmissionOutcome = admission with { WorkspaceId = "other-workspace" } },
            "role" => response with { AdmissionOutcome = admission with { Role = Role("other-role") } },
            "request" => response with { AdmissionOutcome = admission with { RequestHash = new string('f', 64) } },
            "run" => response with { Run = response.Run! with { Id = "run-other" } },
            "projected-status" => response with { AdmissionStatus = "Rejected" },
            "projected-failure" => response with { AdmissionFailureCode = "ContradictoryFailure" },
            "coordination" => response with { Status = "RecoveryRequired" },
            _ => response with { MaterializationStatus = "Conflict" },
        };

        var result = TriggerGovernedLoopDispatchProtocol.Map(envelope, intent, response);

        Assert.Equal(TriggerDispatchOutcome.NeedsReview, result.Outcome);
        Assert.Null(result.GovernedInvocation);
    }

    [Fact]
    public void Startup_snapshot_exposes_the_exact_closed_reference_hash()
    {
        var snapshot = new TriggerWorkerEntrySnapshot("delivery-1", "graph-1", "Dispatched", 4, "worker-1", 1, _workerAtUtc.AddMinutes(1), _workerAtUtc, "Accepted", "trigger-operation", "proved", "run-1", new string('d', 64), new string('e', 64));

        Assert.Equal(new string('e', 64), snapshot.GovernedLoopReferenceHash);
    }

    [Fact]
    public void Exact_terminal_replay_maps_without_requiring_a_repeated_execution_result()
    {
        var envelope = TriggerWorkerTestData.Envelope(loop: TriggerWorkerTestData.GovernedLoop());
        var intent = Intent(envelope, Lease());
        var response = CanonicalResponse(envelope, intent) with
        {
            Status = "Terminal",
            MaterializationStatus = "Replayed",
            ExecutionStatus = null,
            WasDispatched = false,
        };

        var result = TriggerGovernedLoopDispatchProtocol.Map(envelope, intent, response);

        Assert.Equal(TriggerDispatchOutcome.Terminal, result.Outcome);
        Assert.NotNull(result.GovernedInvocation);
    }

    private static GovernedLoopRunInvocationResponse CanonicalResponse(TriggerDeliveryEnvelope envelope, TriggerDispatchEvidence intent, string disposition = "Admitted")
    {
        var admitted = disposition == "Admitted";
        var status = admitted ? "Admitted" : "Rejected";
        var requestHash = new string('d', 64);
        var admission = new GovernedLoopAdmissionOutcomeSnapshot(
            status,
            disposition,
            intent.OperationId,
            requestHash,
            "workspace-sha256:" + envelope.ActorContext.WorkspaceId,
            envelope.Loop.GovernedPublication!,
            envelope.Loop.AuthorityGrant!,
            Role(envelope.ActorContext.RoleId),
            envelope.ActorContext.ActorId.Value,
            envelope.ActorContext.SurfaceId,
            admitted ? "run-1" : null,
            admitted ? null : "GrantInactive",
            new string('e', 64));
        return new GovernedLoopRunInvocationResponse(
            admitted ? "Executed" : "Rejected",
            status,
            admitted ? null : admission.FailureCode,
            admitted ? "Ready" : null,
            admitted ? "Completed" : null,
            admitted,
            admission,
            admitted ? CanonicalRun(envelope, intent.OperationId, requestHash) : null,
            admitted ? "exact canonical evidence" : "proved canonical rejection");
    }

    private static ContextualRoleRevisionPin Role(string roleId)
        => new(new ContextualRoleRevisionIdentity(roleId, 1), new string('c', 64));

    private static LoopRunSnapshot CanonicalRun(TriggerDeliveryEnvelope envelope, string operationId, string requestHash)
    {
        var definition = new LoopDefinitionSnapshot(1, envelope.Loop.LoopId, 1, new string('b', 64), TriggerWorkerTestData.CreatedAtUtc, TriggerWorkerTestData.CreatedAtUtc, "Graph", "Canonical trigger test", envelope.ActorContext.RoleId, null!, null!, [], [], null!, "project-1");
        return new LoopRunSnapshot(1, "run-1", envelope.Loop.LoopId, 1, "Completed", _workerAtUtc, _workerAtUtc, _workerAtUtc, envelope.ActorContext.SurfaceId, null!, operationId, envelope.ActorContext.ActorId.Value, new string('9', 64), definition, "dispatch", null, null!, null!, null!, [], "completed", null, null)
        {
            GovernedAdmissionRequestHash = requestHash,
        };
    }

    private static TriggerWorkerLease Lease() => new("worker-1", 1, _workerAtUtc, _workerAtUtc.AddSeconds(30), 0);

    private static TriggerDeliveryEnvelope ScheduledEnvelope()
    {
        var loop = TriggerWorkerTestData.GovernedLoop();
        var actor = TriggerWorkerTestData.Envelope(loop: loop).ActorContext;
        return TriggerWorkerTestData.ScheduleEnvelope(loop, actor);
    }

    private static TriggerDispatchEvidence Intent(TriggerDeliveryEnvelope envelope, TriggerWorkerLease lease)
    {
        var authorityHash = new string('a', 64);
        return new TriggerDispatchEvidence(TriggerWorkerRequestHash.ComputeOperationId(envelope.DeliveryId, lease.Generation), TriggerWorkerRequestHash.Compute(envelope, lease, authorityHash), authorityHash, _workerAtUtc, TriggerDispatchOutcome.IntentRecorded, null, "intent");
    }

    private static TriggerPayloadEvidence ReferencedPayload()
    {
        var content = "dispatch"u8.ToArray();
        Assert.True(TriggerDeliveryFactory.TryCreateReferencedPayload("payload/artifact-1", EmbodySense.Core.Common.Capabilities.CapabilityIntegrityDigest.Compute(content), out var payload, out _));
        return payload!;
    }

}
