using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Admission;

public sealed class GovernedLoopAdmissionServiceRejectionTests
{
    [Fact]
    public async Task Exact_full_missing_role_source_commits_durable_role_source_mismatch()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        harness.RoleResolution = harness.RoleResolution with
        {
            Status = AuthorityGrantDependencyStatus.NotFound,
            SourceStatus = ContextualRoleInstructionSourceProbeStatus.Missing,
        };

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        var rejection = AssertDurableRejection(
            harness,
            result,
            GovernedLoopAdmissionFailureCode.RoleSourceMismatch);
        Assert.Null(rejection.AuthorityDenial);
        Assert.Null(rejection.CapabilityDenial);
        Assert.Equal(GovernedLoopAdmissionEvidenceKind.ContextualRoleRevision, Assert.Single(rejection.References).Kind);
        Assert.Equal(0, harness.GrantReadCount);
        Assert.Equal(0, harness.RunIdentityGenerationCount);
    }

    [Fact]
    public async Task Exact_absent_role_commits_durable_role_not_found()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        harness.RoleResolution = new AuthorityGrantRoleResolution(
            AuthorityGrantDependencyStatus.NotFound,
            harness.RolePin,
            null,
            null,
            AuthorityGrantApplicationTestFixture.WorkspaceId,
            ContextualRoleInstructionSourceProbeStatus.Unknown,
            string.Empty);

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        var rejection = AssertDurableRejection(
            harness,
            result,
            GovernedLoopAdmissionFailureCode.RoleNotFound);
        Assert.Null(rejection.AuthorityDenial);
        Assert.Null(rejection.CapabilityDenial);
        Assert.Equal(GovernedLoopAdmissionEvidenceKind.ContextualRoleRevision, Assert.Single(rejection.References).Kind);
        Assert.Equal(0, harness.GrantReadCount);
        Assert.Equal(0, harness.RunIdentityGenerationCount);
    }

    [Fact]
    public async Task Future_graph_artifact_never_becomes_permanent_role_rejection()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        var source = harness.Artifact.RevisionArtifact;
        var revisionArtifact = GovernedLoopRevisionArtifactFactory.Create(
            source.SchemaVersion,
            source.Revision,
            source.PredecessorRevision,
            source.RollbackSourcePublication,
            source.CreationOperationId,
            source.CreatedByActorId,
            AuthorityGrantApplicationTestFixture.Now.AddMinutes(2));
        var artifact = GovernedLoopGraphRevisionArtifactFactory.Create(
            harness.Artifact.SchemaVersion,
            revisionArtifact,
            harness.Artifact.Graph);
        harness.GraphReadResult = new EmbodySense.Core.Application.Loops.GraphAuthoring.Models.GovernedLoopGraphRevisionArtifactReadResult(
            EmbodySense.Core.Application.Loops.Revisions.Models.GovernedLoopRevisionStoreReadStatus.Ready,
            1,
            artifact);
        harness.BindingResolution = harness.BindingResolution with { Artifact = artifact };
        harness.RoleResolution = harness.RoleResolution with
        {
            Status = AuthorityGrantDependencyStatus.Disabled,
            Lifecycle = Assert.IsType<ContextualRoleLifecycleSnapshot>(harness.RoleResolution.Lifecycle) with
            {
                State = ContextualRoleLifecycleState.Disabled,
            },
            SourceStatus = ContextualRoleInstructionSourceProbeStatus.Ineligible,
        };

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
        Assert.Null(result.Outcome);
        Assert.Equal(0, harness.CommitCount);
    }

    [Fact]
    public async Task Exact_active_grant_bound_to_another_role_commits_durable_role_mismatch()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        var otherRole = AuthorityGrantApplicationTestFixture.Role(roleId: "other-role", capabilityIds: []);
        var otherRolePin = new ContextualRoleRevisionPin(otherRole.Identity, otherRole.ContentHash);
        var request = BindGrant(harness, harness.Grant.Binding with { Role = otherRolePin });

        var result = await harness.CreateService().AdmitAsync(request);

        var rejection = AssertDurableRejection(
            harness,
            result,
            GovernedLoopAdmissionFailureCode.RoleMismatch);
        Assert.Null(rejection.AuthorityDenial);
        Assert.Null(rejection.CapabilityDenial);
        Assert.Equal(
            new[]
            {
                GovernedLoopAdmissionEvidenceKind.ContextualRoleRevision,
                GovernedLoopAdmissionEvidenceKind.AuthorityGrant,
                GovernedLoopAdmissionEvidenceKind.GraphArtifact,
            },
            rejection.References.Select(reference => reference.Kind));
        Assert.Equal(0, harness.CapabilityAdmissionCount);
        Assert.Equal(0, harness.RunIdentityGenerationCount);
    }

    [Fact]
    public async Task Exact_active_grant_bound_to_another_publication_commits_durable_grant_mismatch()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        var otherPublication = GovernedLoopRevisionPublicationPinFactory.Create(
            1,
            harness.Publication.Revision,
            "publish-loop-other",
            AuthorityGrantApplicationTestFixture.Hash64('9'));
        var request = BindGrant(harness, harness.Grant.Binding with { Loop = otherPublication });

        var result = await harness.CreateService().AdmitAsync(request);

        var rejection = AssertDurableRejection(
            harness,
            result,
            GovernedLoopAdmissionFailureCode.GrantMismatch);
        Assert.Null(rejection.AuthorityDenial);
        Assert.Null(rejection.CapabilityDenial);
        Assert.Equal(GovernedLoopAdmissionEvidenceKind.AuthorityGrant, Assert.Single(rejection.References).Kind);
        Assert.Equal(0, harness.CapabilityAdmissionCount);
        Assert.Equal(0, harness.RunIdentityGenerationCount);
    }

    [Fact]
    public async Task Temporally_incoherent_active_binding_mismatch_never_becomes_a_permanent_rejection()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        var otherRole = AuthorityGrantApplicationTestFixture.Role(roleId: "other-role", capabilityIds: []);
        var otherRolePin = new ContextualRoleRevisionPin(otherRole.Identity, otherRole.ContentHash);
        var request = BindGrant(
            harness,
            harness.Grant.Binding with { Role = otherRolePin },
            recordedAtUtc: AuthorityGrantApplicationTestFixture.Now.AddTicks(1));

        var result = await harness.CreateService().AdmitAsync(request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
        Assert.Null(result.Outcome);
        Assert.Equal(0, harness.CommitCount);
    }

    [Fact]
    public async Task Graph_required_capability_absent_from_exact_grant_commits_structured_capability_denial()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create(includeCapability: true);
        var emptyAuthority = AuthorityCeilingIntersection.EmptyCeiling();
        var request = BindGrant(harness, harness.Grant.Binding, emptyAuthority);

        var result = await harness.CreateService().AdmitAsync(request);

        var rejection = AssertDurableRejection(
            harness,
            result,
            GovernedLoopAdmissionFailureCode.CapabilityResolutionDenied);
        Assert.Null(rejection.AuthorityDenial);
        var proof = Assert.IsType<GovernedLoopAdmissionCapabilityDenialProof>(rejection.CapabilityDenial);
        Assert.Equal(emptyAuthority, proof.EffectiveAuthority);
        Assert.Equal(Assert.Single(harness.Artifact.Graph.AuthorityCeiling.CapabilityIds), Assert.Single(proof.Requirements.Required).CapabilityId.Value);
        var violation = Assert.Single(proof.Violations);
        Assert.Equal(Assert.Single(proof.Requirements.Required).CapabilityId, violation.DependencyId);
        Assert.Equal(Assert.Single(proof.Requirements.Required).CompatibleVersionRange, violation.CompatibleVersionRange);
        Assert.Equal(GovernedLoopAdmissionCapabilityDenialReason.RequiredCapabilityOutsideEffectiveAuthority, violation.Reason);
        Assert.Equal(rejection.RejectedAtUtc, proof.EvaluatedAtUtc);
        Assert.Equal(
            new[]
            {
                GovernedLoopAdmissionEvidenceKind.GraphArtifact,
                GovernedLoopAdmissionEvidenceKind.EffectiveAuthority,
                GovernedLoopAdmissionEvidenceKind.CapabilityAdmission,
            },
            rejection.References.Select(reference => reference.Kind));
        Assert.Equal(0, harness.CapabilityAdmissionCount);
        Assert.Equal(0, harness.RunIdentityGenerationCount);
    }

    [Fact]
    public async Task Capability_denial_is_not_committed_after_the_exact_grant_expires()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create(includeCapability: true);
        var emptyAuthority = AuthorityCeilingIntersection.EmptyCeiling();
        var request = BindGrant(
            harness,
            harness.Grant.Binding,
            emptyAuthority,
            boundary: harness.Grant.Boundary with
            {
                ExpiresAtUtc = AuthorityGrantApplicationTestFixture.Now.AddMinutes(1),
            });

        var result = await harness.CreateService().AdmitAsync(request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
        Assert.Null(result.Outcome);
        Assert.Equal(0, harness.CommitCount);
    }

    [Fact]
    public async Task Ceiling_exceeded_without_structured_authority_proof_is_ambiguous_and_never_commits()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create(includeCapability: true);
        harness.GrantResolution = new AuthorityGrantResolution(
            AuthorityGrantResolutionStatus.CeilingExceeded,
            harness.GrantReference,
            harness.Grant,
            AuthorityCeilingIntersection.EmptyCeiling(),
            string.Empty,
            AuthorityGrantApplicationTestFixture.Now);

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
        Assert.Null(result.Outcome);
        Assert.Equal(0, harness.CommitCount);
        Assert.Equal(0, harness.CapabilityAdmissionCount);
        Assert.Equal(0, harness.RunIdentityGenerationCount);
    }

    [Theory]
    [InlineData(ContextualRoleInstructionSourceProbeStatus.Unsupported)]
    [InlineData(ContextualRoleInstructionSourceProbeStatus.Oversized)]
    [InlineData(ContextualRoleInstructionSourceProbeStatus.Substituted)]
    public async Task Ambiguous_source_postures_never_become_permanent_rejections(
        ContextualRoleInstructionSourceProbeStatus sourceStatus)
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        harness.RoleResolution = harness.RoleResolution with
        {
            Status = AuthorityGrantDependencyStatus.Ambiguous,
            SourceStatus = sourceStatus,
        };

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
        Assert.Null(result.Outcome);
        Assert.Equal(0, harness.CommitCount);
    }

    [Fact]
    public async Task Exact_replaced_and_ineligible_role_postures_commit_their_closed_rejections()
    {
        var replaced = GovernedLoopAdmissionTestHarness.Create();
        replaced.RoleResolution = replaced.RoleResolution with
        {
            Status = AuthorityGrantDependencyStatus.Stale,
            Lifecycle = Assert.IsType<ContextualRoleLifecycleSnapshot>(replaced.RoleResolution.Lifecycle) with
            {
                CurrentIdentity = new ContextualRoleRevisionIdentity(replaced.RolePin.Identity.RoleId, replaced.RolePin.Identity.Revision + 1),
            },
            SourceStatus = ContextualRoleInstructionSourceProbeStatus.Unknown,
        };
        var replacedResult = await replaced.CreateService().AdmitAsync(replaced.Request);
        _ = AssertDurableRejection(replaced, replacedResult, GovernedLoopAdmissionFailureCode.RoleReplaced);

        var inactive = GovernedLoopAdmissionTestHarness.Create();
        inactive.RoleResolution = inactive.RoleResolution with
        {
            Status = AuthorityGrantDependencyStatus.Disabled,
            Lifecycle = Assert.IsType<ContextualRoleLifecycleSnapshot>(inactive.RoleResolution.Lifecycle) with
            {
                State = ContextualRoleLifecycleState.Disabled,
            },
            SourceStatus = ContextualRoleInstructionSourceProbeStatus.Ineligible,
        };
        var inactiveResult = await inactive.CreateService().AdmitAsync(inactive.Request);
        _ = AssertDurableRejection(inactive, inactiveResult, GovernedLoopAdmissionFailureCode.RoleInactive);
    }

    [Fact]
    public async Task Exact_workspace_mismatch_role_posture_commits_its_closed_rejection()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        var otherWorkspace = "workspace-sha256:" + AuthorityGrantApplicationTestFixture.Hash64('b');
        var role = ContextualRoleRevisionContentHash.Apply(harness.Role with
        {
            WorkspaceApplicability = new ContextualRoleWorkspaceApplicability([otherWorkspace]),
            ContentHash = string.Empty,
        });
        var rolePin = new ContextualRoleRevisionPin(role.Identity, role.ContentHash);
        var artifact = AuthorityGrantApplicationTestFixture.GraphArtifact(rolePin, []);
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(
            1,
            artifact.RevisionArtifact.Revision,
            "publish-loop",
            AuthorityGrantApplicationTestFixture.Hash64('7'));
        harness.GraphReadResult = new EmbodySense.Core.Application.Loops.GraphAuthoring.Models.GovernedLoopGraphRevisionArtifactReadResult(
            EmbodySense.Core.Application.Loops.Revisions.Models.GovernedLoopRevisionStoreReadStatus.Ready,
            1,
            artifact);
        harness.BindingResolution = new GovernedLoopGrantBindingResolution(
            AuthorityGrantDependencyStatus.Active,
            publication,
            artifact,
            rolePin,
            artifact.Graph.AuthorityCeiling.CapabilityIds,
            AuthorityGrantApplicationTestFixture.Hash64('2'));
        harness.RoleResolution = new AuthorityGrantRoleResolution(
            AuthorityGrantDependencyStatus.Disabled,
            rolePin,
            role,
            AuthorityGrantApplicationTestFixture.RoleLifecycle(role),
            AuthorityGrantApplicationTestFixture.WorkspaceId,
            ContextualRoleInstructionSourceProbeStatus.WorkspaceMismatch,
            AuthorityGrantApplicationTestFixture.Hash64('3'));
        var request = GovernedLoopAdmissionRequestHash.Apply(harness.Request with { Publication = publication });

        var result = await harness.CreateService().AdmitAsync(request);

        _ = AssertDurableRejection(harness, result, GovernedLoopAdmissionFailureCode.RoleWorkspaceMismatch);
    }

    [Fact]
    public async Task Deterministic_role_labels_without_their_status_specific_shape_remain_ambiguous()
    {
        var mutations = new Action<GovernedLoopAdmissionTestHarness>[]
        {
            harness => harness.RoleResolution = harness.RoleResolution with
            {
                Status = AuthorityGrantDependencyStatus.NotFound,
                SourceStatus = ContextualRoleInstructionSourceProbeStatus.Missing,
                Revision = null,
                Lifecycle = null,
            },
            harness => harness.RoleResolution = harness.RoleResolution with
            {
                Status = AuthorityGrantDependencyStatus.Disabled,
                SourceStatus = ContextualRoleInstructionSourceProbeStatus.WorkspaceMismatch,
            },
            harness => harness.RoleResolution = harness.RoleResolution with
            {
                Status = AuthorityGrantDependencyStatus.Disabled,
                SourceStatus = ContextualRoleInstructionSourceProbeStatus.Ineligible,
            },
            harness => harness.RoleResolution = harness.RoleResolution with
            {
                Status = AuthorityGrantDependencyStatus.Stale,
                SourceStatus = ContextualRoleInstructionSourceProbeStatus.Unknown,
            },
        };

        foreach (var mutate in mutations)
        {
            var harness = GovernedLoopAdmissionTestHarness.Create();
            mutate(harness);

            var result = await harness.CreateService().AdmitAsync(harness.Request);

            Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
            Assert.Null(result.Outcome);
            Assert.Equal(0, harness.CommitCount);
        }
    }

    [Fact]
    public async Task Exact_missing_stale_and_inactive_grant_postures_commit_closed_rejections()
    {
        var cases = new[]
        {
            (Status: AuthorityGrantResolutionStatus.NotFound, Lifecycle: (AuthorityGrantLifecycleStatus?)null, Expected: GovernedLoopAdmissionFailureCode.GrantMismatch),
            (Status: AuthorityGrantResolutionStatus.Stale, Lifecycle: (AuthorityGrantLifecycleStatus?)AuthorityGrantLifecycleStatus.Active, Expected: GovernedLoopAdmissionFailureCode.GrantMismatch),
            (Status: AuthorityGrantResolutionStatus.NotEffective, Lifecycle: (AuthorityGrantLifecycleStatus?)AuthorityGrantLifecycleStatus.Active, Expected: GovernedLoopAdmissionFailureCode.GrantInactive),
            (Status: AuthorityGrantResolutionStatus.Suspended, Lifecycle: (AuthorityGrantLifecycleStatus?)AuthorityGrantLifecycleStatus.Suspended, Expected: GovernedLoopAdmissionFailureCode.GrantInactive),
            (Status: AuthorityGrantResolutionStatus.Revoked, Lifecycle: (AuthorityGrantLifecycleStatus?)AuthorityGrantLifecycleStatus.Revoked, Expected: GovernedLoopAdmissionFailureCode.GrantInactive),
            (Status: AuthorityGrantResolutionStatus.Expired, Lifecycle: (AuthorityGrantLifecycleStatus?)AuthorityGrantLifecycleStatus.Expired, Expected: GovernedLoopAdmissionFailureCode.GrantInactive),
        };

        foreach (var item in cases)
        {
            var harness = GovernedLoopAdmissionTestHarness.Create();
            var request = BindGrantStatus(harness, item.Status, item.Lifecycle);

            var result = await harness.CreateService().AdmitAsync(request);

            _ = AssertDurableRejection(harness, result, item.Expected);
        }
    }

    [Fact]
    public async Task Not_effective_grant_never_becomes_permanent_rejection_after_effective_time()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        var request = BindGrantStatus(
            harness,
            AuthorityGrantResolutionStatus.NotEffective,
            AuthorityGrantLifecycleStatus.Active,
            AuthorityGrantApplicationTestFixture.Now.AddTicks(1));

        var result = await harness.CreateService().AdmitAsync(request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
        Assert.Null(result.Outcome);
        Assert.Equal(0, harness.CommitCount);
    }

    [Fact]
    public async Task Deterministic_grant_label_with_contradictory_payload_remains_ambiguous()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        harness.GrantResolution = harness.GrantResolution with
        {
            Status = AuthorityGrantResolutionStatus.Suspended,
        };

        var result = await harness.CreateService().AdmitAsync(harness.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
        Assert.Null(result.Outcome);
        Assert.Equal(0, harness.CommitCount);
    }

    [Fact]
    public async Task Grant_rejection_never_commits_from_temporally_incoherent_active_role_evidence()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create(
            roleRecordedAtUtc: AuthorityGrantApplicationTestFixture.Now.AddMinutes(-5));
        var lifecycle = Assert.IsType<ContextualRoleLifecycleSnapshot>(harness.RoleResolution.Lifecycle);
        Assert.True(harness.Role.Provenance.RecordedAtUtc > lifecycle.UpdatedAtUtc);
        var request = BindGrantStatus(
            harness,
            AuthorityGrantResolutionStatus.Suspended,
            AuthorityGrantLifecycleStatus.Suspended);

        var result = await harness.CreateService().AdmitAsync(request);

        Assert.Equal(GovernedLoopAdmissionStatus.Ambiguous, result.Status);
        Assert.Null(result.Outcome);
        Assert.Equal(0, harness.GrantReadCount);
        Assert.Equal(0, harness.CommitCount);
    }

    private static GovernedLoopAdmissionRequest BindGrant(
        GovernedLoopAdmissionTestHarness harness,
        AuthorityGrantBinding binding,
        AuthorityCeiling? ceiling = null,
        DateTimeOffset? recordedAtUtc = null,
        AuthorityGrantBoundary? boundary = null)
    {
        var exactCeiling = ceiling ?? harness.Grant.RequestedCeiling;
        var grant = AuthorityGrantHash.Apply(harness.Grant with
        {
            Binding = binding,
            RequestedCeiling = exactCeiling,
            Boundary = boundary ?? harness.Grant.Boundary,
            RecordedAtUtc = recordedAtUtc ?? harness.Grant.RecordedAtUtc,
            ContentHash = string.Empty,
        });
        var reference = new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash);
        harness.GrantResolution = new AuthorityGrantResolution(
            AuthorityGrantResolutionStatus.Active,
            reference,
            grant,
            exactCeiling,
            AuthorityGrantApplicationTestFixture.Hash64('4'),
            AuthorityGrantApplicationTestFixture.Now);
        return GovernedLoopAdmissionRequestHash.Apply(harness.Request with { AuthorityGrant = reference });
    }

    private static GovernedLoopAdmissionRequest BindGrantStatus(
        GovernedLoopAdmissionTestHarness harness,
        AuthorityGrantResolutionStatus status,
        AuthorityGrantLifecycleStatus? lifecycle,
        DateTimeOffset? effectiveAtUtc = null)
    {
        if (status == AuthorityGrantResolutionStatus.NotFound)
        {
            harness.GrantResolution = new AuthorityGrantResolution(
                status,
                harness.GrantReference,
                null,
                AuthorityCeilingIntersection.EmptyCeiling(),
                string.Empty,
                default);
            return harness.Request;
        }

        var grant = AuthorityGrantHash.Apply(harness.Grant with
        {
            Status = lifecycle!.Value,
            Boundary = status == AuthorityGrantResolutionStatus.NotEffective
                ? harness.Grant.Boundary with
                {
                    EffectiveAtUtc = effectiveAtUtc ?? AuthorityGrantApplicationTestFixture.Now.AddMinutes(2),
                }
                : harness.Grant.Boundary,
            ContentHash = string.Empty,
        });
        var reference = new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash);
        harness.GrantResolution = new AuthorityGrantResolution(
            status,
            reference,
            grant,
            AuthorityCeilingIntersection.EmptyCeiling(),
            string.Empty,
            status == AuthorityGrantResolutionStatus.Stale ? default : AuthorityGrantApplicationTestFixture.Now);
        return GovernedLoopAdmissionRequestHash.Apply(harness.Request with { AuthorityGrant = reference });
    }

    private static GovernedLoopAdmissionRejection AssertDurableRejection(
        GovernedLoopAdmissionTestHarness harness,
        GovernedLoopAdmissionResult result,
        GovernedLoopAdmissionFailureCode expectedFailure)
    {
        Assert.Equal(GovernedLoopAdmissionStatus.Rejected, result.Status);
        var outcome = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>(result.Outcome);
        Assert.Equal(GovernedLoopAdmissionDisposition.Rejected, outcome.Disposition);
        Assert.Null(outcome.Receipt);
        Assert.True(GovernedLoopAdmissionValidator.Validate(outcome).IsValid);
        var rejection = Assert.IsType<GovernedLoopAdmissionRejection>(outcome.Rejection);
        Assert.Equal(expectedFailure, rejection.FailureCode);
        Assert.Equal(outcome.RecordedAtUtc, rejection.RejectedAtUtc);
        Assert.Equal(result.OperationId, outcome.Intent.OperationId);
        Assert.Equal(result.RequestHash, outcome.Intent.RequestHash);
        Assert.Equal(1, harness.CommitCount);
        Assert.True(harness.CommitObservedInsideFence);
        Assert.Same(outcome, harness.LastMutation?.Outcome);
        return rejection;
    }
}
