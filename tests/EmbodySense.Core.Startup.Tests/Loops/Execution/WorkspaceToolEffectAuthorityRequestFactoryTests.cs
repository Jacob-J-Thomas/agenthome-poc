using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Startup.Loops.Execution;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

public sealed class WorkspaceToolEffectAuthorityRequestFactoryTests
{
    [Fact]
    public void Exact_admission_and_inference_node_derive_one_read_only_target_without_widening_data_classes()
    {
        var fixture = WorkspaceToolAuthorityTestFixture.Create();

        var request = WorkspaceToolEffectAuthorityRequestFactory.Create(
            fixture.Receipt,
            fixture.Binding,
            fixture.Artifact,
            WorkspaceToolAuthorityTestFixture.NodeId,
            WorkspaceToolAuthorityTestFixture.NodeAttempt,
            WorkspaceToolAuthorityTestFixture.ServerCorrelationId,
            fixture.ToolRequest,
            ResolvedTarget(fixture.ToolRequest),
            GovernedLoopEffectBoundaryKind.WorkspaceActuation);

        Assert.Same(fixture.Receipt, request.AdmissionReceipt);
        Assert.Same(fixture.Binding, request.ExecutionBinding);
        Assert.Same(fixture.Artifact, request.GraphArtifact);
        Assert.Equal(fixture.Binding.RunId, request.ExecutionBinding.RunId);
        Assert.Equal(fixture.Binding.ExecutionGeneration, request.ExecutionBinding.ExecutionGeneration);
        Assert.Equal(WorkspaceToolAuthorityTestFixture.NodeId, request.NodeId);
        Assert.Equal(WorkspaceToolAuthorityTestFixture.NodeAttempt, request.NodeAttempt);
        Assert.Equal(WorkspaceToolAuthorityTestFixture.ServerCorrelationId, request.CorrelationId);
        Assert.Equal(GovernedLoopEffectBoundaryKind.WorkspaceActuation, request.BoundaryKind);
        var expectedPin = Assert.Single(
            fixture.Receipt.Evidence.CapabilityAdmission.Pins,
            pin => pin.DescriptorIdentity.Id.Value == WorkspaceToolAuthorityTestFixture.WorkspaceCommandCapabilityId);
        Assert.Equal(expectedPin, Assert.Single(request.RequiredCapabilityPins));
        Assert.Equal(expectedPin.DescriptorIdentity, Assert.Single(request.RequiredAuthority.Capabilities));
        Assert.Equal(fixture.Receipt.Evidence.EffectiveAuthority.DataClasses, request.RequiredAuthority.DataClasses);
        Assert.Equal(1, request.RequiredAuthority.MaxTargetCount);
        Assert.Equal(EmbodySense.Core.Common.Capabilities.Models.CapabilitySideEffectClass.ReadOnly, request.RequiredAuthority.MaxSideEffectClass);
        Assert.False(request.RequiredAuthority.AllowsRecurrence);
        Assert.False(request.RequiredAuthority.AllowsExternalPublication);
        Assert.False(request.RequiredAuthority.AllowsIrreversibleAction);
        Assert.Matches("^[0-9a-f]{64}$", request.TargetFingerprint);
    }

    [Fact]
    public void Canonical_request_fingerprint_is_stable_complete_and_distinguishes_intake_from_actuation()
    {
        var fixture = WorkspaceToolAuthorityTestFixture.Create();
        var intake = Create(fixture, fixture.ToolRequest, GovernedLoopEffectBoundaryKind.WorkspaceToolIntake);
        var repeatedIntake = Create(fixture, fixture.ToolRequest, GovernedLoopEffectBoundaryKind.WorkspaceToolIntake);
        var actuation = Create(fixture, fixture.ToolRequest, GovernedLoopEffectBoundaryKind.WorkspaceActuation);

        Assert.Equal(intake.EffectOperationId, repeatedIntake.EffectOperationId);
        Assert.StartsWith("workspace-tool-intake-", intake.EffectOperationId, StringComparison.Ordinal);
        Assert.StartsWith("workspace-tool-actuation-", actuation.EffectOperationId, StringComparison.Ordinal);
        Assert.NotEqual(intake.EffectOperationId, actuation.EffectOperationId);
        Assert.Equal(intake.TargetFingerprint, actuation.TargetFingerprint);
        var audit = fixture.ToolRequest.AuditCorrelation!;
        var variations = new[]
        {
            fixture.ToolRequest with { Command = ToolCommand.Search },
            fixture.ToolRequest with { TargetPath = "shared/other.txt" },
            fixture.ToolRequest with { Content = "content" },
            fixture.ToolRequest with { Pattern = "needle" },
            fixture.ToolRequest with { CorrelationId = "tool-call-2" },
            fixture.ToolRequest with { AuditCorrelation = audit with { CatalogHash = new string('d', 64) } },
        };
        Assert.All(variations, variation => Assert.NotEqual(actuation.EffectOperationId, Create(fixture, variation, GovernedLoopEffectBoundaryKind.WorkspaceActuation).EffectOperationId));

        var nextCorrelation = "attempt-correlation-2";
        var nextToolRequest = fixture.ToolRequest with { AuditCorrelation = audit with { AttemptCorrelationId = nextCorrelation } };
        var nextOperation = WorkspaceToolEffectAuthorityRequestFactory.Create(
            fixture.Receipt,
            fixture.Binding,
            fixture.Artifact,
            WorkspaceToolAuthorityTestFixture.NodeId,
            WorkspaceToolAuthorityTestFixture.NodeAttempt,
            nextCorrelation,
            nextToolRequest,
            ResolvedTarget(nextToolRequest),
            GovernedLoopEffectBoundaryKind.WorkspaceActuation);
        Assert.NotEqual(actuation.EffectOperationId, nextOperation.EffectOperationId);
    }

    [Fact]
    public void Mismatched_or_widening_evidence_and_mutating_requests_fail_before_producing_an_operation_identity()
    {
        var fixture = WorkspaceToolAuthorityTestFixture.Create();
        var replacementBinding = GovernedLoopExecutionBinding.Create(
            fixture.Binding.SchemaVersion,
            fixture.Binding.RunId,
            fixture.Binding.Revision,
            fixture.Binding.ExecutionGeneration + 1);
        Assert.Throws<ArgumentException>(() => WorkspaceToolEffectAuthorityRequestFactory.Create(
            fixture.Receipt,
            replacementBinding,
            fixture.Artifact,
            WorkspaceToolAuthorityTestFixture.NodeId,
            WorkspaceToolAuthorityTestFixture.NodeAttempt,
            WorkspaceToolAuthorityTestFixture.ServerCorrelationId,
            fixture.ToolRequest,
            ResolvedTarget(fixture.ToolRequest),
            GovernedLoopEffectBoundaryKind.WorkspaceActuation));
        Assert.Throws<ArgumentException>(() => Create(
            fixture,
            fixture.ToolRequest with { Command = ToolCommand.Write, Content = "mutation" },
            GovernedLoopEffectBoundaryKind.WorkspaceActuation));
        Assert.Throws<ArgumentException>(() => Create(
            fixture,
            fixture.ToolRequest with { AuditCorrelation = fixture.ToolRequest.AuditCorrelation! with { Attempt = WorkspaceToolAuthorityTestFixture.NodeAttempt + 1 } },
            GovernedLoopEffectBoundaryKind.WorkspaceActuation));

        var zeroTargetAuthority = fixture.Receipt.Evidence.EffectiveAuthority with { MaxTargetCount = 0 };
        var narrowedEvidence = EmbodySense.Core.Application.Tests.GovernedModelProfileApplicationTestFixture.EmptyRoutingEvidence(
            fixture.Receipt.Intent,
            fixture.Receipt.Evidence.Binding,
            fixture.Receipt.Evidence.GrantProfile,
            fixture.Receipt.Evidence.GrantBoundary,
            fixture.Receipt.Evidence.GrantDependencyEvidenceHash,
            zeroTargetAuthority,
            fixture.Receipt.Evidence.CapabilityAdmission,
            fixture.Receipt.Evidence.EvaluatedAtUtc);
        var narrowedReceipt = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionReceipt(
            fixture.Receipt.SchemaVersion,
            fixture.Receipt.Intent,
            narrowedEvidence,
            fixture.Receipt.RecordedAtUtc,
            string.Empty));
        Assert.True(GovernedLoopAdmissionValidator.Validate(narrowedReceipt).IsValid);
        Assert.Throws<ArgumentException>(() => WorkspaceToolEffectAuthorityRequestFactory.Create(
            narrowedReceipt,
            fixture.Binding,
            fixture.Artifact,
            WorkspaceToolAuthorityTestFixture.NodeId,
            WorkspaceToolAuthorityTestFixture.NodeAttempt,
            WorkspaceToolAuthorityTestFixture.ServerCorrelationId,
            fixture.ToolRequest,
            ResolvedTarget(fixture.ToolRequest),
            GovernedLoopEffectBoundaryKind.WorkspaceActuation));
    }

    [Fact]
    public void Unsupported_boundaries_attempts_nodes_and_unbounded_request_shapes_fail_closed()
    {
        var fixture = WorkspaceToolAuthorityTestFixture.Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(fixture, fixture.ToolRequest, GovernedLoopEffectBoundaryKind.ProviderTransport));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkspaceToolEffectAuthorityRequestFactory.Create(
            fixture.Receipt,
            fixture.Binding,
            fixture.Artifact,
            WorkspaceToolAuthorityTestFixture.NodeId,
            0,
            WorkspaceToolAuthorityTestFixture.ServerCorrelationId,
            fixture.ToolRequest,
            ResolvedTarget(fixture.ToolRequest),
            GovernedLoopEffectBoundaryKind.WorkspaceActuation));
        Assert.Throws<ArgumentException>(() => WorkspaceToolEffectAuthorityRequestFactory.Create(
            fixture.Receipt,
            fixture.Binding,
            fixture.Artifact,
            "exit",
            WorkspaceToolAuthorityTestFixture.NodeAttempt,
            WorkspaceToolAuthorityTestFixture.ServerCorrelationId,
            fixture.ToolRequest,
            ResolvedTarget(fixture.ToolRequest),
            GovernedLoopEffectBoundaryKind.WorkspaceActuation));
        Assert.Throws<ArgumentException>(() => Create(fixture, fixture.ToolRequest with { TargetPath = " " }, GovernedLoopEffectBoundaryKind.WorkspaceActuation));
        Assert.Throws<ArgumentException>(() => Create(fixture, fixture.ToolRequest with { CorrelationId = null }, GovernedLoopEffectBoundaryKind.WorkspaceActuation));
        Assert.Throws<ArgumentException>(() => Create(fixture, fixture.ToolRequest with { Pattern = "bad\0pattern" }, GovernedLoopEffectBoundaryKind.WorkspaceActuation));
        Assert.Throws<ArgumentException>(() => Create(fixture, fixture.ToolRequest with { Pattern = "\ud800" }, GovernedLoopEffectBoundaryKind.WorkspaceActuation));
        Assert.Throws<ArgumentException>(() => Create(
            fixture,
            fixture.ToolRequest with { AuditCorrelation = fixture.ToolRequest.AuditCorrelation! with { AdmittedCommands = new string('a', 19_000) } },
            GovernedLoopEffectBoundaryKind.WorkspaceActuation));
    }

    [Fact]
    public void Target_identity_uses_only_the_exact_server_resolved_normalized_absolute_path()
    {
        var fixture = WorkspaceToolAuthorityTestFixture.Create();
        var canonical = ResolvedTarget(fixture.ToolRequest);
        var aliasRequest = fixture.ToolRequest with { TargetPath = "shared/alias-note.txt" };

        var original = WorkspaceToolEffectAuthorityRequestFactory.Create(
            fixture.Receipt,
            fixture.Binding,
            fixture.Artifact,
            WorkspaceToolAuthorityTestFixture.NodeId,
            WorkspaceToolAuthorityTestFixture.NodeAttempt,
            WorkspaceToolAuthorityTestFixture.ServerCorrelationId,
            fixture.ToolRequest,
            canonical,
            GovernedLoopEffectBoundaryKind.WorkspaceToolIntake);
        var alias = WorkspaceToolEffectAuthorityRequestFactory.Create(
            fixture.Receipt,
            fixture.Binding,
            fixture.Artifact,
            WorkspaceToolAuthorityTestFixture.NodeId,
            WorkspaceToolAuthorityTestFixture.NodeAttempt,
            WorkspaceToolAuthorityTestFixture.ServerCorrelationId,
            aliasRequest,
            canonical,
            GovernedLoopEffectBoundaryKind.WorkspaceToolIntake);

        Assert.Equal(original.TargetFingerprint, alias.TargetFingerprint);
        Assert.Equal(original.EffectOperationId, alias.EffectOperationId);
        Assert.Throws<ArgumentException>(() => WorkspaceToolEffectAuthorityRequestFactory.Create(
            fixture.Receipt,
            fixture.Binding,
            fixture.Artifact,
            WorkspaceToolAuthorityTestFixture.NodeId,
            WorkspaceToolAuthorityTestFixture.NodeAttempt,
            WorkspaceToolAuthorityTestFixture.ServerCorrelationId,
            fixture.ToolRequest,
            fixture.ToolRequest.TargetPath,
            GovernedLoopEffectBoundaryKind.WorkspaceToolIntake));
    }

    private static EmbodySense.Core.Application.Loops.Execution.Authority.Models.GovernedLoopEffectAuthorityRequest Create(
        (
            GovernedLoopAdmissionReceipt Receipt,
            GovernedLoopExecutionBinding Binding,
            EmbodySense.Core.Common.Loops.Revisions.Models.GovernedLoopGraphRevisionArtifact Artifact,
            ToolRequest ToolRequest) fixture,
        ToolRequest request,
        GovernedLoopEffectBoundaryKind boundaryKind)
        => WorkspaceToolEffectAuthorityRequestFactory.Create(
            fixture.Receipt,
            fixture.Binding,
            fixture.Artifact,
            WorkspaceToolAuthorityTestFixture.NodeId,
            WorkspaceToolAuthorityTestFixture.NodeAttempt,
            WorkspaceToolAuthorityTestFixture.ServerCorrelationId,
            request,
            ResolvedTarget(request),
            boundaryKind);

    private static string ResolvedTarget(ToolRequest request)
        => Path.GetFullPath(request.TargetPath);
}
