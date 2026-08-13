using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Startup.Triggers;

namespace EmbodySense.Core.Startup.Tests.Triggers;

public sealed class TriggerWorkerRuntimeFacadeTests
{
    private static readonly DateTimeOffset _createdAtUtc = TriggerWorkerTestData.CreatedAtUtc;
    private static readonly DateTimeOffset _workerAtUtc = _createdAtUtc.AddSeconds(4);

    [Fact]
    public void Inline_payload_prepares_exact_invocation_and_null_inputs_are_rejected()
    {
        var envelope = Envelope();
        var lease = new TriggerWorkerLease("worker-1", 1, _workerAtUtc, _workerAtUtc.AddSeconds(30), 0);
        var intent = Intent(envelope, lease, new string('a', 64));

        var preparation = TriggerCustomLoopDispatchProtocol.Prepare(envelope, intent);

        Assert.Equal(envelope.Loop.LoopId, preparation.Input!.LoopId);
        Assert.Equal(envelope.Loop.DefinitionVersion, preparation.Input.ExpectedDefinitionVersion);
        Assert.Equal(envelope.Loop.ContentHash, preparation.Input.ExpectedDefinitionHash);
        Assert.Equal(intent.OperationId, preparation.Input.OperationId);
        Assert.Equal("dispatch", preparation.Input.InvocationPrompt);
        Assert.Same(envelope.ActorContext, preparation.ActorContext);
        Assert.Null(preparation.Rejection);
        Assert.Throws<ArgumentNullException>(() => TriggerCustomLoopDispatchProtocol.Prepare(null!, intent));
        Assert.Throws<ArgumentNullException>(() => TriggerCustomLoopDispatchProtocol.Prepare(envelope, null!));
        Assert.Throws<ArgumentNullException>(() => TriggerCustomLoopDispatchProtocol.Map(null!, intent, new LoopRunInvocationResponse("Invalid", null, false, null, [], "invalid")));
        Assert.Throws<ArgumentNullException>(() => TriggerCustomLoopDispatchProtocol.Map(envelope, null!, new LoopRunInvocationResponse("Invalid", null, false, null, [], "invalid")));
        Assert.Throws<ArgumentNullException>(() => TriggerCustomLoopDispatchProtocol.Map(envelope, intent, null!));
    }

    [Fact]
    public void Malformed_trigger_operation_identity_stops_before_invocation()
    {
        var envelope = Envelope();
        var lease = new TriggerWorkerLease("worker-1", 1, _workerAtUtc, _workerAtUtc.AddSeconds(30), 0);
        var malformed = Intent(envelope, lease, new string('a', 64)) with { OperationId = "trigger-" + new string('A', 64) };

        var preparation = TriggerCustomLoopDispatchProtocol.Prepare(envelope, malformed);
        var response = new LoopRunInvocationResponse("Admitted", "Completed", true, Run(envelope, malformed.OperationId, "Completed"), [], "untrusted response");
        var mapped = TriggerCustomLoopDispatchProtocol.Map(envelope, malformed, response);

        Assert.Null(preparation.Input);
        Assert.Null(preparation.ActorContext);
        Assert.Equal(TriggerDispatchOutcome.NeedsReview, preparation.Rejection!.Outcome);
        Assert.Contains("malformed operation identity", preparation.Rejection.Detail, StringComparison.Ordinal);
        Assert.Equal(TriggerDispatchOutcome.NeedsReview, mapped.Outcome);
        Assert.Null(mapped.GovernedInvocation);
    }

    [Fact]
    public void Governed_reference_and_invalid_utf8_are_proved_rejected_before_runner_invocation()
    {
        var lease = new TriggerWorkerLease("worker-1", 1, _workerAtUtc, _workerAtUtc.AddSeconds(30), 0);
        var authorityHash = new string('a', 64);

        var referenced = Envelope(payload: ReferencedPayload());
        var referencedIntent = Intent(referenced, lease, authorityHash);
        var referencedResult = TriggerCustomLoopDispatchProtocol.Prepare(referenced, referencedIntent);
        var invalid = Envelope(payload: InlinePayload([0xff]));
        var invalidResult = TriggerCustomLoopDispatchProtocol.Prepare(invalid, Intent(invalid, lease, authorityHash));

        Assert.Equal(TriggerDispatchOutcome.Rejected, referencedResult.Rejection!.Outcome);
        Assert.Equal(TriggerDispatchOutcome.Rejected, invalidResult.Rejection!.Outcome);
        Assert.Null(referencedResult.Input);
        Assert.Null(invalidResult.Input);
        Assert.Null(referencedResult.ActorContext);
        Assert.Null(invalidResult.ActorContext);
    }

    [Theory]
    [InlineData("Conflict")]
    [InlineData("LimitExceeded")]
    [InlineData("NonterminalRunExists")]
    [InlineData("NotFound")]
    [InlineData("WorkspaceExecutionBusy")]
    public async Task Only_real_statuses_that_prove_pre_dispatch_rejection_are_rejected(string admissionStatus)
    {
        var envelope = Envelope();
        var lease = new TriggerWorkerLease("worker-1", 1, _workerAtUtc, _workerAtUtc.AddSeconds(30), 0);
        var intent = Intent(envelope, lease, new string('a', 64));
        var response = new LoopRunInvocationResponse(admissionStatus, null, false, null, [], "proved before provider dispatch");

        var result = TriggerCustomLoopDispatchProtocol.Map(envelope, intent, response);

        Assert.Equal(TriggerDispatchOutcome.Rejected, result.Outcome);
        Assert.Null(result.GovernedInvocation);
    }

    [Theory]
    [InlineData("OperationInProgress", false)]
    [InlineData("ReceiptUnavailable", false)]
    [InlineData("AuditUnavailable", false)]
    [InlineData("Failed", false)]
    [InlineData("Unknown", false)]
    [InlineData("UnknownStatus", false)]
    [InlineData("WorkspaceHostUnavailable", false)]
    [InlineData("Invalid", false)]
    [InlineData("Invalid", true)]
    public async Task Ambiguous_or_contradictory_real_runtime_postures_need_review(string admissionStatus, bool wasDispatched)
    {
        var envelope = Envelope();
        var lease = new TriggerWorkerLease("worker-1", 1, _workerAtUtc, _workerAtUtc.AddSeconds(30), 0);
        var intent = Intent(envelope, lease, new string('a', 64));
        var response = new LoopRunInvocationResponse(admissionStatus, null, wasDispatched, null, [], "runtime posture");

        var result = TriggerCustomLoopDispatchProtocol.Map(envelope, intent, response);

        Assert.Equal(TriggerDispatchOutcome.NeedsReview, result.Outcome);
        Assert.Null(result.GovernedInvocation);
    }

    [Theory]
    [InlineData("Admitted", true, "Completed", TriggerDispatchOutcome.Terminal)]
    [InlineData("Admitted", false, "Completed", TriggerDispatchOutcome.Terminal)]
    [InlineData("Replayed", false, "Paused", TriggerDispatchOutcome.Accepted)]
    public async Task Exact_fresh_or_recovered_admitted_receipt_maps_without_another_fabricated_outcome(string admissionStatus, bool wasDispatched, string runStatus, TriggerDispatchOutcome expected)
    {
        var envelope = Envelope();
        var lease = new TriggerWorkerLease("worker-1", 1, _workerAtUtc, _workerAtUtc.AddSeconds(30), 0);
        var intent = Intent(envelope, lease, new string('a', 64));
        var response = new LoopRunInvocationResponse(admissionStatus, runStatus, wasDispatched, Run(envelope, intent.OperationId, runStatus), [], "exact receipt");

        var result = TriggerCustomLoopDispatchProtocol.Map(envelope, intent, response);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(intent.OperationId, result.GovernedInvocation!.OperationId);
        Assert.Equal("run-1", result.GovernedInvocation.RunId);
        Assert.Equal(new string('d', 64), result.GovernedInvocation.AdmissionRequestHash);
    }

    [Theory]
    [InlineData("missing-run")]
    [InlineData("operation")]
    [InlineData("request-hash")]
    [InlineData("loop")]
    [InlineData("definition-id")]
    [InlineData("definition-version")]
    [InlineData("definition-hash")]
    [InlineData("execution-status")]
    [InlineData("run-id")]
    [InlineData("needs-review")]
    public async Task Missing_stale_or_fabricated_admitted_receipt_needs_review(string mismatch)
    {
        var envelope = Envelope();
        var lease = new TriggerWorkerLease("worker-1", 1, _workerAtUtc, _workerAtUtc.AddSeconds(30), 0);
        var intent = Intent(envelope, lease, new string('a', 64));
        var runStatus = mismatch == "needs-review" ? "NeedsReview" : "Completed";
        var run = mismatch == "missing-run" ? null : Run(envelope, intent.OperationId, runStatus);
        if (run is not null)
        {
            run = mismatch switch
            {
                "operation" => run with { AdmissionOperationId = "other-operation" },
                "request-hash" => run with { AdmissionRequestHash = "invalid" },
                "loop" => run with { LoopId = "other-loop" },
                "definition-id" => run with { AdmittedDefinition = run.AdmittedDefinition with { Id = "other-loop" } },
                "definition-version" => run with { AdmittedDefinition = run.AdmittedDefinition with { DefinitionVersion = 2 } },
                "definition-hash" => run with { AdmittedDefinition = run.AdmittedDefinition with { ContentHash = new string('f', 64) } },
                "run-id" => run with { Id = "INVALID" },
                _ => run
            };
        }

        var executionStatus = mismatch == "execution-status" ? "Running" : runStatus;
        var response = new LoopRunInvocationResponse("Admitted", executionStatus, true, run, [], "untrusted response");

        var result = TriggerCustomLoopDispatchProtocol.Map(envelope, intent, response);

        Assert.Equal(TriggerDispatchOutcome.NeedsReview, result.Outcome);
        Assert.Null(result.GovernedInvocation);
    }

    private static TriggerDeliveryEnvelope Envelope(TriggerPayloadEvidence? payload = null)
    {
        return TriggerWorkerTestData.Envelope(payload);
    }

    private static TriggerPayloadEvidence InlinePayload(byte[] bytes)
    {
        return TriggerWorkerTestData.InlinePayload(bytes);
    }

    private static TriggerPayloadEvidence ReferencedPayload()
    {
        var content = "dispatch"u8.ToArray();
        Assert.True(TriggerDeliveryFactory.TryCreateReferencedPayload("payload/artifact-1", CapabilityIntegrityDigest.Compute(content), out var payload, out _));
        return payload!;
    }

    private static TriggerDispatchEvidence Intent(TriggerDeliveryEnvelope envelope, TriggerWorkerLease lease, string authorityHash)
    {
        var requestHash = TriggerWorkerRequestHash.Compute(envelope, lease, authorityHash);
        return new TriggerDispatchEvidence(TriggerWorkerRequestHash.ComputeOperationId(envelope.DeliveryId, lease.Generation), requestHash, authorityHash, _workerAtUtc, TriggerDispatchOutcome.IntentRecorded, null, "intent");
    }

    private static LoopRunSnapshot Run(TriggerDeliveryEnvelope envelope, string operationId, string status)
    {
        DateTimeOffset? terminalAtUtc = status is "Completed" or "Failed" or "Cancelled" or "NeedsReview" ? _workerAtUtc : null;
        var legacy = Assert.IsType<TriggerLegacyLoopDefinitionReference>(envelope.Loop.LegacyDefinition);
        var definition = new LoopDefinitionSnapshot(1, legacy.LoopId, legacy.DefinitionVersion, legacy.ContentHash, _createdAtUtc, _createdAtUtc, "Loop", "Trigger test", "operator", null!, null!, [], [], null!, "mutation-1");
        return new LoopRunSnapshot(1, "run-1", envelope.Loop.LoopId, 1, status, _workerAtUtc, _workerAtUtc, terminalAtUtc, "trigger", null!, operationId, "worker", new string('d', 64), definition, "dispatch", null, null!, null!, null!, [], status == "Completed" ? "completed" : null, status == "Failed" ? "failed" : null, null);
    }

}
