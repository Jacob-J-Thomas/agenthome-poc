using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Application.Tests.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Reconciliation;

public sealed class GovernedLoopEffectReconciliationModelCopyTests
{
    [Fact]
    public void Case_results_deep_copy_legal_open_and_assessed_without_disposition_states()
    {
        var (open, assessed, attempt) = GovernedLoopEffectReconciliationApplicationTestFixture.AssessedCase();

        var read = new GovernedLoopEffectReconciliationCaseReadResult(GovernedLoopEffectReconciliationCaseReadStatus.Found, open);
        var mutation = new GovernedLoopEffectReconciliationCaseMutationRequest("mutation-1", GovernedLoopEffectAttemptTestFixture.Hash('1'), "assess", open.CaseVersion, open.ContentHash, assessed.Binding, assessed);
        var result = new GovernedLoopEffectReconciliationCaseMutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, assessed, attempt);

        Assert.Null(read.Case!.CurrentAssessmentHash);
        Assert.Equal(GovernedLoopEffectReconciliationCaseReadStatus.Found, read.Status);
        Assert.Null(read.Case.Disposition);
        Assert.Null(read.Case.Resolution);
        Assert.NotSame(open, read.Case);
        Assert.NotSame(open.Binding, read.Case.Binding);
        Assert.NotSame(open.Binding.Execution, read.Case.Binding.Execution);
        Assert.NotSame(open.ContractMetadata, read.Case.ContractMetadata);
        Assert.NotSame(open.ContractMetadata.Capability, read.Case.ContractMetadata.Capability);
        Assert.NotSame(open.ContractMetadata.Implementation, read.Case.ContractMetadata.Implementation);
        Assert.Equal(assessed.AssessmentHistory.Single().ContentHash, mutation.Replacement.CurrentAssessmentHash);
        Assert.Equal("mutation-1", mutation.OperationId);
        Assert.Equal(GovernedLoopEffectAttemptTestFixture.Hash('1'), mutation.RequestHash);
        Assert.Equal("assess", mutation.Purpose);
        Assert.Equal(open.CaseVersion, mutation.ExpectedCaseVersion);
        Assert.Equal(open.ContentHash, mutation.ExpectedCaseContentHash);
        Assert.Null(mutation.Replacement.Disposition);
        Assert.Null(mutation.Replacement.Resolution);
        Assert.Null(mutation.ReconciledEffectSuccessor);
        Assert.NotSame(assessed, mutation.Replacement);
        Assert.NotSame(assessed.Binding, mutation.Binding);
        Assert.NotSame(assessed.AssessmentHistory, mutation.Replacement.AssessmentHistory);
        Assert.NotSame(assessed.AssessmentHistory.Single(), mutation.Replacement.AssessmentHistory.Single());

        Assert.NotSame(assessed, result.Case);
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, result.Status);
        Assert.NotSame(attempt, result.EffectHead);
        Assert.NotSame(attempt.Binding, result.EffectHead!.Binding);
        Assert.NotSame(attempt.Payload, result.EffectHead.Payload);
        Assert.NotSame(attempt.Capability, result.EffectHead.Capability);
        Assert.NotSame(attempt.Implementation, result.EffectHead.Implementation);
    }

    [Fact]
    public void Case_mutation_supports_create_preconditions_without_an_effect_successor()
    {
        var (open, _, _) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();

        var request = new GovernedLoopEffectReconciliationCaseMutationRequest("mutation-open", GovernedLoopEffectAttemptTestFixture.Hash('2'), "open", null, null, open.Binding, open);

        Assert.Null(request.ExpectedCaseVersion);
        Assert.Null(request.ExpectedCaseContentHash);
        Assert.Null(request.Replacement.CurrentAssessmentHash);
        Assert.Null(request.Replacement.Disposition);
        Assert.Null(request.ReconciledEffectSuccessor);
    }

    [Fact]
    public void Registry_page_enforces_bounds_and_deep_copies_registered_metadata()
    {
        var (open, _, _) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var source = new[] { open.ContractMetadata };

        var page = new GovernedLoopEffectReconciliationProbeRegistryPage(GovernedLoopEffectReconciliationProbeRegistryListStatus.Ready, source, new string('c', 1024));
        source[0] = GovernedLoopEffectReconciliationContractCopy.Copy(open.ContractMetadata);

        var retained = Assert.Single(page.Contracts);
        Assert.Equal(GovernedLoopEffectReconciliationProbeRegistryListStatus.Ready, page.Status);
        Assert.Equal(open.ContractMetadata, retained);
        Assert.NotSame(open.ContractMetadata, retained);
        Assert.NotSame(open.ContractMetadata.Capability, retained.Capability);
        Assert.NotSame(open.ContractMetadata.Implementation, retained.Implementation);
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedLoopEffectReconciliationContractMetadata>)page.Contracts)[0] = open.ContractMetadata);
        Assert.Equal(1024, page.NextCursor!.Length);
        Assert.Equal(100, new GovernedLoopEffectReconciliationProbeRegistryPage(GovernedLoopEffectReconciliationProbeRegistryListStatus.Ready, Enumerable.Repeat(open.ContractMetadata, 100).ToArray(), "c").Contracts.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationProbeRegistryPage(GovernedLoopEffectReconciliationProbeRegistryListStatus.Ready, Enumerable.Repeat(open.ContractMetadata, 101).ToArray(), null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectReconciliationProbeRegistryPage(GovernedLoopEffectReconciliationProbeRegistryListStatus.Ready, [], new string('c', 1025)));
    }

    [Fact]
    public void Probe_and_resolution_models_retain_detached_exact_case_binding_and_evidence()
    {
        var (open, _, input) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var reference = Reference(open);
        var observation = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationObservation(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            open.CaseId,
            open.Binding.ContentHash,
            "observation-1",
            "source-1",
            GovernedLoopEffectAttemptTestFixture.Hash('3'),
            GovernedLoopEffectReconciliationObservationKind.Evidence,
            GovernedLoopEffectReconciliationReliabilityPosture.Authoritative,
            GovernedLoopEffectReconciliationObservedOutcome.NotApplied,
            "evidence-1",
            GovernedLoopEffectAttemptTestFixture.Hash('4'),
            open.UpdatedAtUtc.AddSeconds(1),
            open.UpdatedAtUtc.AddSeconds(2),
            "No matching external effect exists.",
            string.Empty));
        var resolution = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationResolution(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            open.CaseId,
            open.Binding.ContentHash,
            "resolution-1",
            GovernedLoopEffectAttemptTestFixture.Hash('5'),
            GovernedLoopEffectAttemptTestFixture.Hash('6'),
            GovernedLoopEffectOutcome.NotApplied,
            null,
            null,
            GovernedLoopEffectAttemptTestFixture.Hash('7'),
            open.UpdatedAtUtc.AddSeconds(3),
            "Accepted exact absence proof.",
            string.Empty));

        var invocation = new GovernedLoopEffectReconciliationProbeInvocationRequest(reference, open.Binding, open.ContractMetadata, input);
        var probeResult = new GovernedLoopEffectReconciliationProbeInvocationResult(GovernedLoopEffectReconciliationProbeInvocationStatus.Ready, observation);
        var resolutionResult = new GovernedLoopEffectReconciliationResolutionReadResult(GovernedLoopEffectReconciliationResolutionReadStatus.Found, resolution);

        Assert.NotSame(reference, invocation.Case);
        Assert.NotSame(open.Binding, invocation.Binding);
        Assert.NotSame(open.ContractMetadata, invocation.Contract);
        Assert.NotSame(input, invocation.Input);
        Assert.Equal(invocation.Case.CaseId, probeResult.Observation!.CaseId);
        Assert.Equal(GovernedLoopEffectReconciliationProbeInvocationStatus.Ready, probeResult.Status);
        Assert.Equal(invocation.Binding.ContentHash, probeResult.Observation.BindingHash);
        Assert.NotSame(observation, probeResult.Observation);
        Assert.Equal(open.CaseId, resolutionResult.Resolution!.CaseId);
        Assert.Equal(GovernedLoopEffectReconciliationResolutionReadStatus.Found, resolutionResult.Status);
        Assert.Equal(open.Binding.ContentHash, resolutionResult.Resolution.BindingHash);
        Assert.NotSame(resolution, resolutionResult.Resolution);
    }

    [Fact]
    public void Exact_read_and_authorization_models_detach_every_common_and_application_reference()
    {
        var (open, _, _) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var reference = Reference(open);
        var caseRead = new GovernedLoopEffectReconciliationCaseReadRequest(reference);
        var authorizationRequest = new GovernedLoopEffectReconciliationAuthorizationRequest("assess", reference, open.Binding);
        var authorizationResult = new GovernedLoopEffectReconciliationAuthorizationResult(GovernedLoopEffectReconciliationAuthorizationStatus.Ready, "assess", reference, open.Binding, GovernedLoopEffectAttemptTestFixture.Hash('e'));
        var registryRead = new GovernedLoopEffectReconciliationProbeRegistryReadRequest(open.ContractMetadata);
        var probe = new RecordingGovernedLoopEffectReconciliationPorts();
        var registryResult = new GovernedLoopEffectReconciliationProbeRegistryReadResult(GovernedLoopEffectReconciliationProbeRegistryReadStatus.Found, open.ContractMetadata, probe);
        var inputRead = new GovernedLoopEffectReconciliationInputReadRequest(reference, open.Binding);
        var resolutionRead = new GovernedLoopEffectReconciliationResolutionReadRequest(reference, open.Binding);

        Assert.NotSame(reference, caseRead.Reference);
        Assert.Equal("assess", authorizationRequest.Purpose);
        Assert.NotSame(reference, authorizationRequest.Case);
        Assert.NotSame(open.Binding, authorizationRequest.Binding);
        Assert.NotSame(reference, authorizationResult.Case);
        Assert.Equal(GovernedLoopEffectReconciliationAuthorizationStatus.Ready, authorizationResult.Status);
        Assert.Equal("assess", authorizationResult.Purpose);
        Assert.Equal(GovernedLoopEffectAttemptTestFixture.Hash('e'), authorizationResult.AuthorityEvidenceHash);
        Assert.NotSame(open.Binding, authorizationResult.Binding);
        Assert.NotSame(open.ContractMetadata, registryRead.Contract);
        Assert.NotSame(open.ContractMetadata, registryResult.Contract);
        Assert.Equal(GovernedLoopEffectReconciliationProbeRegistryReadStatus.Found, registryResult.Status);
        Assert.Same(probe, registryResult.Probe);
        Assert.NotSame(reference, inputRead.Case);
        Assert.NotSame(open.Binding, inputRead.Binding);
        Assert.NotSame(reference, resolutionRead.Case);
        Assert.NotSame(open.Binding, resolutionRead.Binding);
    }

    [Fact]
    public void Input_result_deep_copies_current_effect_frontier_binding_and_input()
    {
        var (open, attempt, input) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var frontier = GovernedLoopEffectReconciliationApplicationTestFixture.ReviewBlockedFrontier(open, attempt);
        var reference = Reference(open);

        var result = new GovernedLoopEffectReconciliationInputReadResult(GovernedLoopEffectReconciliationInputReadStatus.Found, reference, open.Binding, attempt, frontier, input);

        Assert.NotSame(open.Binding, result.Binding);
        Assert.Equal(GovernedLoopEffectReconciliationInputReadStatus.Found, result.Status);
        Assert.NotSame(reference, result.Case);
        Assert.NotSame(attempt, result.EffectHead);
        Assert.NotSame(frontier, result.Frontier);
        Assert.NotSame(frontier.Binding, result.Frontier!.Binding);
        Assert.NotSame(frontier.Payload, result.Frontier.Payload);
        Assert.NotSame(frontier.Payload.Nodes, result.Frontier.Payload.Nodes);
        Assert.NotSame(input, result.Input);
        Assert.Equal(GovernedLoopEffectPhase.ReconciliationRequired, result.EffectHead!.Payload.Phase);
        Assert.Equal(open.Binding.CurrentAttemptHash, result.EffectHead.ContentHash);
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, result.Frontier.Payload.Status);
        Assert.Contains(result.Frontier.Payload.Nodes, node => node.ActivationOrdinal == open.Binding.ActivationOrdinal && node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked);
    }

    private static GovernedLoopEffectReconciliationCaseReference Reference(GovernedLoopEffectReconciliationCase value)
        => new(value.CaseId, value.CaseVersion, value.ContentHash, value.Binding.ContentHash);

}
