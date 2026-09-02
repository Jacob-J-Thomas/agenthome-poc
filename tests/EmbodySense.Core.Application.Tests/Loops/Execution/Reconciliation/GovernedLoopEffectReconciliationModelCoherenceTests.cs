using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Application.Tests.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Reconciliation;

public sealed class GovernedLoopEffectReconciliationModelCoherenceTests
{
    [Fact]
    public void Mutation_request_rejects_invalid_identity_hash_and_expected_case_shapes()
    {
        var (open, attempt, _) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();

        Assert.Throws<ArgumentException>(() => MutationRequest("INVALID", Hash('1'), "open", null, null, open.Binding, open));
        Assert.Throws<ArgumentException>(() => MutationRequest("mutation-1", Hash('a').ToUpperInvariant(), "open", null, null, open.Binding, open));
        Assert.Throws<ArgumentException>(() => MutationRequest("mutation-1", Hash('1'), "INVALID", null, null, open.Binding, open));
        Assert.Throws<ArgumentException>(() => MutationRequest("mutation-1", Hash('1'), "open", 1, null, open.Binding, open));
        Assert.Throws<ArgumentException>(() => MutationRequest("mutation-1", Hash('1'), "open", null, open.ContentHash, open.Binding, open));
        Assert.Throws<ArgumentOutOfRangeException>(() => MutationRequest("mutation-1", Hash('1'), "open", 0, open.ContentHash, open.Binding, open));

        var (_, assessed, _) = GovernedLoopEffectReconciliationApplicationTestFixture.AssessedCase();
        Assert.Throws<ArgumentException>(() => MutationRequest("mutation-1", Hash('1'), "open", null, null, assessed.Binding, assessed));
        Assert.Throws<ArgumentException>(() => MutationRequest("mutation-1", Hash('1'), "assess", open.CaseVersion, Hash('b'), assessed.Binding, assessed));
        Assert.Throws<ArgumentException>(() => MutationRequest("mutation-1", Hash('1'), "assess", open.CaseVersion, open.ContentHash, open.Binding, open));

        var otherBinding = GovernedLoopEffectReconciliationContract.CreateBinding(open.Binding.WorkspaceId, open.Binding.ActivationOrdinal + 1, open.Binding.VisitOrdinal, attempt);
        Assert.Throws<ArgumentException>(() => MutationRequest("mutation-1", Hash('1'), "open", null, null, otherBinding, open));
    }

    [Fact]
    public void Mutation_request_accepts_only_an_exact_resolution_bound_successor()
    {
        var (_, resolved, _, successor, _) = GovernedLoopEffectReconciliationApplicationTestFixture.ResolvedCase();
        var request = MutationRequest("mutation-resolve", Hash('6'), "resolve", resolved.CaseVersion - 1, resolved.PreviousContentHash, resolved.Binding, resolved, successor);

        Assert.NotSame(successor, request.ReconciledEffectSuccessor);
        Assert.Equal(successor.ContentHash, request.ReconciledEffectSuccessor!.ContentHash);

        var mismatchedPayload = GovernedLoopEffectPayload.Create(
            successor.Payload.SchemaVersion,
            successor.Payload.EffectId,
            successor.Payload.OperationId,
            successor.Payload.EffectGeneration,
            successor.Payload.Origin,
            successor.Payload.OriginNodeId,
            successor.Payload.IntentHash,
            successor.Payload.Phase,
            successor.Payload.Outcome,
            successor.Payload.EvidenceStatus,
            successor.Payload.OutcomeEvidenceId,
            "other-resolution",
            successor.Payload.UpdatedAtUtc);
        var unhashed = new GovernedLoopEffectAttempt(
            successor.SchemaVersion,
            successor.Binding,
            successor.NodeId,
            successor.NodeAttempt,
            successor.Capability,
            successor.Implementation,
            successor.ActuatorOperationId,
            successor.OperationDescriptorHash,
            successor.InputFingerprint,
            successor.TargetFingerprint,
            successor.PreconditionEvidenceHash,
            successor.AdmissionAuthorityEvidenceHash,
            successor.DispatchAuthorityEvidenceHash,
            successor.BeforeEvidenceId,
            successor.AfterEvidenceId,
            mismatchedPayload,
            successor.PreviousContentHash,
            string.Empty);
        var mismatched = unhashed with { ContentHash = GovernedLoopEffectAttemptContract.Compute(unhashed) };

        Assert.Null(GovernedLoopEffectAttemptContract.Validate(mismatched));
        Assert.Throws<ArgumentException>(() => MutationRequest("mutation-resolve", Hash('6'), "resolve", resolved.CaseVersion - 1, resolved.PreviousContentHash, resolved.Binding, resolved, mismatched));
    }

    [Fact]
    public void Exact_requests_reject_missing_or_cross_bound_contracts()
    {
        var (open, attempt, input) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var reference = Reference(open);
        var otherBinding = GovernedLoopEffectReconciliationContract.CreateBinding(open.Binding.WorkspaceId, open.Binding.ActivationOrdinal + 1, open.Binding.VisitOrdinal, attempt);
        var corruptInput = new GovernedActuatorInputEvidence(input.CanonicalJson, Hash('7'), input.Utf8ByteCount, input.ElementCount);
        var source = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationEvidenceSource(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            open.CaseId,
            open.Binding.ContentHash,
            "source-1",
            GovernedLoopEffectReconciliationEvidenceSourceKind.Authoritative,
            GovernedLoopEffectReconciliationReliabilityPosture.Authoritative,
            open.ContractMetadata.ContractId,
            open.ContractMetadata.ContractVersion,
            open.ContractMetadata.ContentHash,
            Hash('8'),
            open.OpenedAtUtc,
            null,
            string.Empty));

        Assert.Throws<ArgumentNullException>(() => new GovernedLoopEffectReconciliationCaseReadRequest(null!));
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopEffectReconciliationProbeRegistryReadRequest(null!));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationAuthorizationRequest("assess", reference, otherBinding));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationInputReadRequest(reference, otherBinding));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationResolutionReadRequest(reference, otherBinding));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationProbeInvocationRequest(reference, otherBinding, open.ContractMetadata, input, attempt, source));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationProbeInvocationRequest(reference, open.Binding, open.ContractMetadata, corruptInput, attempt, source));
    }

    [Fact]
    public void List_and_exact_read_results_enforce_status_payload_coherence()
    {
        var (open, _, _) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var summary = new GovernedLoopEffectReconciliationCaseSummary(open.CaseId, open.CaseVersion, open.ContentHash, open.Binding.ContentHash, GovernedLoopEffectReconciliationCaseSummaryStatus.Open);

        _ = new GovernedLoopEffectReconciliationCaseListPage(GovernedLoopEffectReconciliationCaseListStatus.Unavailable, [], null);
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationCaseListPage(GovernedLoopEffectReconciliationCaseListStatus.Unavailable, [summary], null));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationCaseListPage(GovernedLoopEffectReconciliationCaseListStatus.Invalid, [], "cursor"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationCaseListPage((GovernedLoopEffectReconciliationCaseListStatus)99, [], null));

        _ = new GovernedLoopEffectReconciliationCaseReadResult(GovernedLoopEffectReconciliationCaseReadStatus.Found, open);
        _ = new GovernedLoopEffectReconciliationCaseReadResult(GovernedLoopEffectReconciliationCaseReadStatus.NotFound, null);
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationCaseReadResult(GovernedLoopEffectReconciliationCaseReadStatus.Found, null));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationCaseReadResult(GovernedLoopEffectReconciliationCaseReadStatus.Unavailable, open));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationCaseReadResult((GovernedLoopEffectReconciliationCaseReadStatus)99, null));
    }

    [Fact]
    public void Mutation_results_preserve_required_conflict_state_and_reject_incoherent_payloads()
    {
        var (open, attempt, _) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var stateStatuses = new[]
        {
            GovernedLoopEffectReconciliationCaseMutationStatus.Applied,
            GovernedLoopEffectReconciliationCaseMutationStatus.Replayed,
            GovernedLoopEffectReconciliationCaseMutationStatus.Conflict,
        };
        foreach (var status in stateStatuses)
        {
            var result = new GovernedLoopEffectReconciliationCaseMutationResult(status, open, attempt);
            Assert.Equal(open.ContentHash, result.Case!.ContentHash);
            Assert.Equal(attempt.ContentHash, result.EffectHead!.ContentHash);
            Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationCaseMutationResult(status, open, null));
        }

        var emptyStatuses = new[]
        {
            GovernedLoopEffectReconciliationCaseMutationStatus.Unknown,
            GovernedLoopEffectReconciliationCaseMutationStatus.Invalid,
            GovernedLoopEffectReconciliationCaseMutationStatus.Corrupt,
            GovernedLoopEffectReconciliationCaseMutationStatus.Unavailable,
            GovernedLoopEffectReconciliationCaseMutationStatus.CapacityExceeded,
            GovernedLoopEffectReconciliationCaseMutationStatus.RepairRequired,
        };
        foreach (var status in emptyStatuses)
        {
            _ = new GovernedLoopEffectReconciliationCaseMutationResult(status, null, null);
            Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationCaseMutationResult(status, open, attempt));
        }
    }

    [Fact]
    public void Authorization_and_registry_results_preserve_their_intentional_exact_echoes()
    {
        var (open, _, _) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var reference = Reference(open);
        var probe = new RecordingGovernedLoopEffectReconciliationPorts();

        _ = new GovernedLoopEffectReconciliationAuthorizationResult(GovernedLoopEffectReconciliationAuthorizationStatus.Ready, "assess", reference, open.Binding, Hash('8'));
        var denied = new GovernedLoopEffectReconciliationAuthorizationResult(GovernedLoopEffectReconciliationAuthorizationStatus.Denied, "assess", reference, open.Binding, null);
        Assert.Equal(reference.ContentHash, denied.Case.ContentHash);
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationAuthorizationResult(GovernedLoopEffectReconciliationAuthorizationStatus.Ready, "assess", reference, open.Binding, null));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationAuthorizationResult(GovernedLoopEffectReconciliationAuthorizationStatus.Denied, "assess", reference, open.Binding, Hash('8')));

        _ = new GovernedLoopEffectReconciliationProbeRegistryPage(GovernedLoopEffectReconciliationProbeRegistryListStatus.Unavailable, [], null);
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationProbeRegistryPage(GovernedLoopEffectReconciliationProbeRegistryListStatus.Unavailable, [open.ContractMetadata], null));
        _ = new GovernedLoopEffectReconciliationProbeRegistryReadResult(GovernedLoopEffectReconciliationProbeRegistryReadStatus.Found, open.ContractMetadata, probe);
        var conflict = new GovernedLoopEffectReconciliationProbeRegistryReadResult(GovernedLoopEffectReconciliationProbeRegistryReadStatus.Conflict, open.ContractMetadata, null);
        Assert.NotNull(conflict.Contract);
        Assert.Null(conflict.Probe);
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationProbeRegistryReadResult(GovernedLoopEffectReconciliationProbeRegistryReadStatus.Found, open.ContractMetadata, null));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationProbeRegistryReadResult(GovernedLoopEffectReconciliationProbeRegistryReadStatus.Conflict, open.ContractMetadata, probe));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationProbeRegistryReadResult(GovernedLoopEffectReconciliationProbeRegistryReadStatus.Unavailable, open.ContractMetadata, null));
    }

    [Fact]
    public void Probe_input_and_resolution_results_require_only_their_documented_success_payloads()
    {
        var (_, resolved, current, successor, input) = GovernedLoopEffectReconciliationApplicationTestFixture.ResolvedCase();
        var reference = Reference(resolved);
        var observation = Assert.Single(resolved.ObservationHistory);
        var frontier = GovernedLoopEffectReconciliationApplicationTestFixture.ReviewBlockedFrontier(resolved, current);

        _ = new GovernedLoopEffectReconciliationProbeInvocationResult(GovernedLoopEffectReconciliationProbeInvocationStatus.Ready, observation);
        _ = new GovernedLoopEffectReconciliationProbeInvocationResult(GovernedLoopEffectReconciliationProbeInvocationStatus.NotFound, null);
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationProbeInvocationResult(GovernedLoopEffectReconciliationProbeInvocationStatus.Ready, null));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationProbeInvocationResult(GovernedLoopEffectReconciliationProbeInvocationStatus.Unavailable, observation));

        _ = new GovernedLoopEffectReconciliationInputReadResult(GovernedLoopEffectReconciliationInputReadStatus.Found, reference, resolved.Binding, current, frontier, input);
        _ = new GovernedLoopEffectReconciliationInputReadResult(GovernedLoopEffectReconciliationInputReadStatus.Conflict, null, null, null, null, null);
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationInputReadResult(GovernedLoopEffectReconciliationInputReadStatus.Found, reference, resolved.Binding, null, frontier, input));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationInputReadResult(GovernedLoopEffectReconciliationInputReadStatus.Conflict, reference, resolved.Binding, current, frontier, input));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationInputReadResult(GovernedLoopEffectReconciliationInputReadStatus.Found, reference, resolved.Binding, successor, frontier, input));

        _ = new GovernedLoopEffectReconciliationResolutionReadResult(GovernedLoopEffectReconciliationResolutionReadStatus.Found, resolved.Resolution);
        _ = new GovernedLoopEffectReconciliationResolutionReadResult(GovernedLoopEffectReconciliationResolutionReadStatus.NotFound, null);
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationResolutionReadResult(GovernedLoopEffectReconciliationResolutionReadStatus.Found, null));
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectReconciliationResolutionReadResult(GovernedLoopEffectReconciliationResolutionReadStatus.Unavailable, resolved.Resolution));
    }

    private static GovernedLoopEffectReconciliationCaseMutationRequest MutationRequest(
        string operationId,
        string requestHash,
        string purpose,
        long? expectedVersion,
        string? expectedHash,
        GovernedLoopEffectReconciliationBinding binding,
        GovernedLoopEffectReconciliationCase replacement,
        GovernedLoopEffectAttempt? successor = null)
        => new(operationId, requestHash, purpose, expectedVersion, expectedHash, binding, replacement, successor);

    private static GovernedLoopEffectReconciliationCaseReference Reference(GovernedLoopEffectReconciliationCase value)
        => new(value.CaseId, value.CaseVersion, value.ContentHash, value.Binding.ContentHash);

    private static string Hash(char value) => GovernedLoopEffectAttemptTestFixture.Hash(value);
}
