using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Authority;

public sealed class GovernedLoopEffectAuthorityBoundaryTests
{
    [Fact]
    public async Task Direct_decision_is_appended_before_the_continuation_runs_once_inside_the_shared_fence()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create();
        var grant = new StubEffectAuthorityGrantResolver { Resolution = fixture.Resolution };
        var capabilities = new StubEffectCapabilityAdmissionService
        {
            Result = new CapabilityRevalidationResult(true, [fixture.RequiredPin], "The required pin is current.", CapabilityRevalidationStatus.Active),
        };
        var evidence = new RecordingEffectAuthorityEvidenceStore();
        var transaction = new RecordingEffectAuthorityTransaction();
        var boundary = Boundary(grant, capabilities, evidence, transaction);
        var commits = 0;

        var result = await boundary.ExecuteAsync(
            fixture.Request,
            _ =>
            {
                Assert.True(transaction.IsInside);
                Assert.Single(evidence.Decisions);
                commits++;
                return Task.FromResult("committed");
            });

        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.Decided, result.Status);
        Assert.True(result.CommitInvoked);
        Assert.Equal("committed", result.Result);
        Assert.Equal(1, commits);
        Assert.Equal(1, transaction.Executions);
        Assert.Equal(fixture.Request.AdmissionReceipt.Intent.AuthorityGrant, grant.LastReference);
        var decision = Assert.IsType<GovernedLoopEffectAuthorityDecision>(result.Decision);
        Assert.Equal(GovernedLoopEffectAuthorityDisposition.Direct, decision.Disposition);
        Assert.Equal(GovernedLoopEffectAuthorityReason.ActiveExact, decision.Reason);
        Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid);
    }

    [Fact]
    public async Task Replayed_exact_direct_decision_never_invokes_the_continuation_again()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create();
        var grant = new StubEffectAuthorityGrantResolver { Resolution = fixture.Resolution };
        var capabilities = new StubEffectCapabilityAdmissionService
        {
            Result = new CapabilityRevalidationResult(true, [fixture.RequiredPin], "The required pin is current.", CapabilityRevalidationStatus.Active),
        };
        var evidence = new RecordingEffectAuthorityEvidenceStore();
        evidence.Statuses.Enqueue(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended);
        evidence.Statuses.Enqueue(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent);
        var boundary = Boundary(grant, capabilities, evidence);
        var commits = 0;

        var first = await boundary.ExecuteAsync(fixture.Request, _ => Task.FromResult(++commits));
        var replay = await boundary.ExecuteAsync(fixture.Request, _ => Task.FromResult(++commits));

        Assert.True(first.CommitInvoked);
        Assert.Equal(1, first.Result);
        Assert.False(replay.CommitInvoked);
        Assert.Equal(1, commits);
        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected, replay.Status);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, replay.EvidenceStatus);
        Assert.Equal(GovernedLoopEffectAuthorityDisposition.Pause, replay.Decision?.Disposition);
        Assert.Equal(GovernedLoopEffectAuthorityReason.EvidenceAmbiguous, replay.Decision?.Reason);
        Assert.Equal(evidence.Decisions[0].ContentHash, evidence.Decisions[1].ContentHash);
    }

    [Theory]
    [InlineData(AuthorityGrantResolutionStatus.Suspended, GovernedLoopEffectAuthorityReason.GrantSuspended)]
    [InlineData(AuthorityGrantResolutionStatus.Revoked, GovernedLoopEffectAuthorityReason.GrantRevoked)]
    [InlineData(AuthorityGrantResolutionStatus.Expired, GovernedLoopEffectAuthorityReason.GrantExpired)]
    [InlineData(AuthorityGrantResolutionStatus.ProfileUnavailable, GovernedLoopEffectAuthorityReason.ProfileUnavailable)]
    [InlineData(AuthorityGrantResolutionStatus.RoleUnavailable, GovernedLoopEffectAuthorityReason.RoleUnavailable)]
    [InlineData(AuthorityGrantResolutionStatus.LoopUnavailable, GovernedLoopEffectAuthorityReason.LoopUnavailable)]
    [InlineData(AuthorityGrantResolutionStatus.CeilingExceeded, GovernedLoopEffectAuthorityReason.CeilingExceeded)]
    public async Task Definitive_grant_posture_is_durably_denied_without_invoking_the_continuation(
        AuthorityGrantResolutionStatus status,
        GovernedLoopEffectAuthorityReason expectedReason)
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create();
        var lifecycle = status switch
        {
            AuthorityGrantResolutionStatus.Suspended => AuthorityGrantLifecycleStatus.Suspended,
            AuthorityGrantResolutionStatus.Revoked => AuthorityGrantLifecycleStatus.Revoked,
            AuthorityGrantResolutionStatus.Expired => AuthorityGrantLifecycleStatus.Expired,
            _ => AuthorityGrantLifecycleStatus.Active,
        };
        var currentGrant = EmbodySense.Core.Application.Tests.Governance.Authority.Grants.AuthorityGrantApplicationTestFixture.Grant(
            status: lifecycle,
            binding: fixture.Grant.Binding,
            ceiling: fixture.Grant.RequestedCeiling,
            boundary: fixture.Grant.Boundary,
            recordedAtUtc: fixture.Grant.RecordedAtUtc);
        var resolution = fixture.Resolution with
        {
            Status = status,
            Grant = currentGrant,
            CurrentGrant = currentGrant,
            EffectiveCeiling = EmbodySense.Core.Common.Authority.AuthorityCeilingIntersection.EmptyCeiling(),
            DependencyEvidenceHash = string.Empty,
        };
        var grant = new StubEffectAuthorityGrantResolver { Resolution = resolution };
        var capabilities = new StubEffectCapabilityAdmissionService();
        var evidence = new RecordingEffectAuthorityEvidenceStore();
        var result = await Boundary(grant, capabilities, evidence).ExecuteAsync(fixture.Request, _ => Task.FromResult(true));

        Assert.False(result.CommitInvoked);
        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.Decided, result.Status);
        Assert.Equal(expectedReason, result.Decision?.Reason);
        Assert.Equal(GovernedLoopEffectAuthorityDisposition.Deny, result.Decision?.Disposition);
        Assert.Single(evidence.Decisions);
        Assert.Equal(0, capabilities.Calls);
    }

    [Fact]
    public async Task Stale_resolution_records_the_actual_current_revision_without_following_it_for_authorization()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create();
        var replacement = EmbodySense.Core.Application.Tests.Governance.Authority.Grants.AuthorityGrantApplicationTestFixture.Grant(
            revision: 2,
            predecessor: fixture.Grant,
            ceiling: fixture.Grant.RequestedCeiling,
            recordedAtUtc: GovernedLoopEffectAuthorityTestFixture.Now.AddMinutes(-1));
        var resolution = fixture.Resolution with
        {
            Status = AuthorityGrantResolutionStatus.Stale,
            Grant = fixture.Grant,
            CurrentGrant = replacement,
            EffectiveCeiling = EmbodySense.Core.Common.Authority.AuthorityCeilingIntersection.EmptyCeiling(),
            DependencyEvidenceHash = string.Empty,
        };
        var grant = new StubEffectAuthorityGrantResolver { Resolution = resolution };
        var result = await Boundary(grant, new StubEffectCapabilityAdmissionService(), new RecordingEffectAuthorityEvidenceStore()).ExecuteAsync(fixture.Request, _ => Task.FromResult(true));

        Assert.False(result.CommitInvoked);
        Assert.Equal(GovernedLoopEffectAuthorityReason.GrantStale, result.Decision?.Reason);
        Assert.Equal(GovernedLoopEffectAuthorityTestFixture.Reference(replacement), result.Decision?.CurrentAuthority?.Grant);
        Assert.NotEqual(fixture.Request.AdmissionReceipt.Intent.AuthorityGrant, result.Decision?.CurrentAuthority?.Grant);
    }

    [Fact]
    public async Task Required_pin_drift_is_durably_denied()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create();
        var driftedIdentity = fixture.RequiredPin.DescriptorIdentity with
        {
            Hash = EmbodySense.Core.Application.Tests.Governance.Authority.Grants.AuthorityGrantApplicationTestFixture.Capability(
                fixture.RequiredPin.DescriptorIdentity.Id.Value,
                hash: '9').Hash,
        };
        var driftedPin = fixture.RequiredPin with { DescriptorIdentity = driftedIdentity };
        var driftedCapabilities = new StubEffectCapabilityAdmissionService
        {
            Result = new CapabilityRevalidationResult(false, [], "The required pin drifted.", CapabilityRevalidationStatus.PinDrifted, [driftedPin]),
        };
        var drifted = await Boundary(
            new StubEffectAuthorityGrantResolver { Resolution = fixture.Resolution },
            driftedCapabilities,
            new RecordingEffectAuthorityEvidenceStore()).ExecuteAsync(fixture.Request, _ => Task.FromResult(true));

        Assert.False(drifted.CommitInvoked);
        Assert.Equal(GovernedLoopEffectAuthorityReason.CapabilityDrifted, drifted.Decision?.Reason);
        Assert.Equal(driftedPin, Assert.Single(drifted.Decision!.CurrentAuthority!.ObservedCapabilityPins));
    }

    [Theory]
    [InlineData(CapabilityRevalidationStatus.PinMissing)]
    [InlineData(CapabilityRevalidationStatus.PinInactive)]
    public async Task Unrelated_missing_or_inactive_pin_narrows_current_proof_without_stopping_the_required_effect(
        CapabilityRevalidationStatus status)
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create(includeUnrelatedWorkspaceCapability: true);
        var capabilities = new StubEffectCapabilityAdmissionService
        {
            Result = new CapabilityRevalidationResult(
                false,
                [fixture.RequiredPin],
                "An unrelated admitted workspace capability is inactive.",
                status),
        };

        var result = await Boundary(
            new StubEffectAuthorityGrantResolver { Resolution = fixture.Resolution },
            capabilities,
            new RecordingEffectAuthorityEvidenceStore()).ExecuteAsync(fixture.Request, _ => Task.FromResult("committed"));

        Assert.True(result.CommitInvoked);
        Assert.Equal("committed", result.Result);
        Assert.Equal(GovernedLoopEffectAuthorityDisposition.Direct, result.Decision?.Disposition);
        Assert.Equal(GovernedLoopEffectAuthorityReason.ActiveNarrowed, result.Decision?.Reason);
        Assert.True(EmbodySense.Core.Common.Authority.Grants.AuthorityCeilingSubset.IsEqual(
            fixture.Request.RequiredAuthority,
            result.Decision!.EffectiveAuthority));
        Assert.Equal(fixture.RequiredPin, Assert.Single(result.Decision!.CurrentAuthority!.CapabilityPins));
        Assert.Empty(result.Decision.CurrentAuthority.ObservedCapabilityPins);
    }

    [Fact]
    public async Task Unrelated_drift_is_retained_as_evidence_without_stopping_the_required_effect()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create(includeUnrelatedWorkspaceCapability: true);
        var unrelated = Assert.IsType<EmbodySense.Core.Common.Capabilities.Models.CapabilityAdmissionPin>(fixture.UnrelatedPin);
        var driftedIdentity = unrelated.DescriptorIdentity with
        {
            Hash = EmbodySense.Core.Application.Tests.Governance.Authority.Grants.AuthorityGrantApplicationTestFixture.Capability(
                unrelated.DescriptorIdentity.Id.Value,
                hash: '9').Hash,
        };
        var driftedPin = unrelated with { DescriptorIdentity = driftedIdentity };
        Assert.NotEqual(unrelated, driftedPin);
        var capabilities = new StubEffectCapabilityAdmissionService
        {
            Result = new CapabilityRevalidationResult(
                false,
                [fixture.RequiredPin],
                "An unrelated admitted workspace capability drifted.",
                CapabilityRevalidationStatus.PinDrifted,
                [driftedPin]),
        };

        var result = await Boundary(
            new StubEffectAuthorityGrantResolver { Resolution = fixture.Resolution },
            capabilities,
            new RecordingEffectAuthorityEvidenceStore()).ExecuteAsync(fixture.Request, _ => Task.FromResult("committed"));

        Assert.True(result.CommitInvoked);
        Assert.Equal(GovernedLoopEffectAuthorityDisposition.Direct, result.Decision?.Disposition);
        Assert.Equal(GovernedLoopEffectAuthorityReason.ActiveNarrowed, result.Decision?.Reason);
        Assert.Equal(driftedPin, Assert.Single(result.Decision!.CurrentAuthority!.ObservedCapabilityPins));
    }

    [Fact]
    public async Task Current_grant_narrowing_of_an_unrelated_capability_does_not_stop_the_required_effect()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create(includeUnrelatedWorkspaceCapability: true);
        var resolution = fixture.Resolution with { EffectiveCeiling = fixture.Request.RequiredAuthority };
        var capabilities = new StubEffectCapabilityAdmissionService
        {
            Result = new CapabilityRevalidationResult(
                true,
                fixture.Request.AdmissionReceipt.Evidence.CapabilityAdmission.Pins,
                "Every admitted pin remains current.",
                CapabilityRevalidationStatus.Active),
        };

        var result = await Boundary(
            new StubEffectAuthorityGrantResolver { Resolution = resolution },
            capabilities,
            new RecordingEffectAuthorityEvidenceStore()).ExecuteAsync(fixture.Request, _ => Task.FromResult("committed"));

        Assert.True(result.CommitInvoked);
        Assert.Equal(GovernedLoopEffectAuthorityDisposition.Direct, result.Decision?.Disposition);
        Assert.Equal(GovernedLoopEffectAuthorityReason.ActiveNarrowed, result.Decision?.Reason);
        Assert.Equal(fixture.RequiredPin, Assert.Single(result.Decision!.CurrentAuthority!.CapabilityPins));
    }

    [Fact]
    public async Task Current_grant_narrowing_below_the_effect_dimensions_is_durably_denied()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create();
        var resolution = fixture.Resolution with
        {
            EffectiveCeiling = fixture.Request.RequiredAuthority with { MaxTargetCount = 0 },
        };
        var capabilities = new StubEffectCapabilityAdmissionService
        {
            Result = new CapabilityRevalidationResult(
                true,
                [fixture.RequiredPin],
                "The required capability pin remains current, but target authority was narrowed.",
                CapabilityRevalidationStatus.Active),
        };
        var commits = 0;

        var result = await Boundary(
            new StubEffectAuthorityGrantResolver { Resolution = resolution },
            capabilities,
            new RecordingEffectAuthorityEvidenceStore()).ExecuteAsync(
                fixture.Request,
                _ => Task.FromResult(++commits));

        Assert.False(result.CommitInvoked);
        Assert.Equal(0, commits);
        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.Decided, result.Status);
        Assert.Equal(GovernedLoopEffectAuthorityDisposition.Deny, result.Decision?.Disposition);
        Assert.Equal(GovernedLoopEffectAuthorityReason.EffectOutsideCeiling, result.Decision?.Reason);
    }

    [Fact]
    public async Task Expiry_while_direct_evidence_is_appending_stops_before_the_effect_callback()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create();
        var clock = new FixedEffectAuthorityTimeProvider(GovernedLoopEffectAuthorityTestFixture.Now);
        var evidence = new RecordingEffectAuthorityEvidenceStore
        {
            BeforeReturn = _ => clock.Value = GovernedLoopEffectAuthorityTestFixture.Now.AddHours(2),
        };
        var capabilities = new StubEffectCapabilityAdmissionService
        {
            Result = new CapabilityRevalidationResult(true, [fixture.RequiredPin], "Current.", CapabilityRevalidationStatus.Active),
        };
        var commits = 0;

        var result = await Boundary(
            new StubEffectAuthorityGrantResolver { Resolution = fixture.Resolution },
            capabilities,
            evidence,
            timeProvider: clock).ExecuteAsync(fixture.Request, _ => Task.FromResult(++commits));

        Assert.Equal(0, commits);
        Assert.False(result.CommitInvoked);
        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected, result.Status);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, result.EvidenceStatus);
        Assert.Equal(GovernedLoopEffectAuthorityDisposition.Pause, result.Decision?.Disposition);
        Assert.Equal(GovernedLoopEffectAuthorityReason.EvidenceAmbiguous, result.Decision?.Reason);
        Assert.Single(evidence.Decisions);
    }

    [Fact]
    public async Task Cancellation_while_direct_evidence_is_appending_stops_before_the_effect_callback()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create();
        using var cancellation = new CancellationTokenSource();
        var evidence = new RecordingEffectAuthorityEvidenceStore
        {
            BeforeReturn = _ => cancellation.Cancel(),
        };
        var capabilities = new StubEffectCapabilityAdmissionService
        {
            Result = new CapabilityRevalidationResult(true, [fixture.RequiredPin], "Current.", CapabilityRevalidationStatus.Active),
        };
        var commits = 0;

        var result = await Boundary(
            new StubEffectAuthorityGrantResolver { Resolution = fixture.Resolution },
            capabilities,
            evidence).ExecuteAsync(
                fixture.Request,
                _ => Task.FromResult(++commits),
                cancellation.Token);

        Assert.Equal(0, commits);
        Assert.False(result.CommitInvoked);
        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected, result.Status);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, result.EvidenceStatus);
        Assert.Equal(GovernedLoopEffectAuthorityDisposition.Pause, result.Decision?.Disposition);
        Assert.Equal(GovernedLoopEffectAuthorityReason.EvidenceAmbiguous, result.Decision?.Reason);
        Assert.Single(evidence.Decisions);
    }

    [Fact]
    public void Stopped_exception_preserves_the_exact_boundary_posture()
    {
        var exception = new GovernedLoopEffectAuthorityStoppedException(
            "The governed effect was stopped.",
            GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected,
            GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent,
            null);

        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected, exception.ExecutionStatus);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, exception.EvidenceStatus);
        Assert.Null(exception.Decision);
        Assert.Equal("The governed effect was stopped.", exception.Message);
        Assert.Equal(typeof(Exception), typeof(GovernedLoopEffectAuthorityStoppedException).BaseType);
        Assert.Throws<ArgumentException>(() => new GovernedLoopEffectAuthorityStoppedException(
            " ",
            GovernedLoopEffectAuthorityExecutionStatus.InvalidRequest,
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown,
            null));
    }

    [Fact]
    public async Task Unavailable_capability_posture_pauses_and_evidence_failure_never_invokes_the_continuation()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create();
        var unavailableCapabilities = new StubEffectCapabilityAdmissionService
        {
            Result = new CapabilityRevalidationResult(false, [], "Catalog unavailable.", CapabilityRevalidationStatus.CatalogUnavailable),
        };
        var durablePause = await Boundary(
            new StubEffectAuthorityGrantResolver { Resolution = fixture.Resolution },
            unavailableCapabilities,
            new RecordingEffectAuthorityEvidenceStore()).ExecuteAsync(fixture.Request, _ => Task.FromResult(true));
        var unavailableEvidence = new RecordingEffectAuthorityEvidenceStore { Status = GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable };
        var directCapabilities = new StubEffectCapabilityAdmissionService
        {
            Result = new CapabilityRevalidationResult(true, [fixture.RequiredPin], "Current.", CapabilityRevalidationStatus.Active),
        };
        var rejectedEvidence = await Boundary(
            new StubEffectAuthorityGrantResolver { Resolution = fixture.Resolution },
            directCapabilities,
            unavailableEvidence).ExecuteAsync(fixture.Request, _ => Task.FromResult(true));

        Assert.False(durablePause.CommitInvoked);
        Assert.Equal(GovernedLoopEffectAuthorityDisposition.Pause, durablePause.Decision?.Disposition);
        Assert.Equal(GovernedLoopEffectAuthorityReason.CapabilityUnavailable, durablePause.Decision?.Reason);
        Assert.False(rejectedEvidence.CommitInvoked);
        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected, rejectedEvidence.Status);
        Assert.Equal(GovernedLoopEffectAuthorityReason.EvidenceUnavailable, rejectedEvidence.Decision?.Reason);
    }

    [Fact]
    public async Task Contradictory_capability_posture_pauses_without_invoking_the_continuation()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create();
        var capabilities = new StubEffectCapabilityAdmissionService
        {
            Result = new CapabilityRevalidationResult(false, [fixture.RequiredPin], "Contradictory test posture.", CapabilityRevalidationStatus.Active),
        };

        var result = await Boundary(
            new StubEffectAuthorityGrantResolver { Resolution = fixture.Resolution },
            capabilities,
            new RecordingEffectAuthorityEvidenceStore()).ExecuteAsync(fixture.Request, _ => Task.FromResult(true));

        Assert.False(result.CommitInvoked);
        Assert.Equal(GovernedLoopEffectAuthorityDisposition.Pause, result.Decision?.Disposition);
        Assert.Equal(GovernedLoopEffectAuthorityReason.CapabilityAmbiguous, result.Decision?.Reason);
    }

    [Fact]
    public async Task Mismatched_node_fails_before_current_reads_persistence_or_continuation()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create();
        var grant = new StubEffectAuthorityGrantResolver { Resolution = fixture.Resolution };
        var capabilities = new StubEffectCapabilityAdmissionService();
        var evidence = new RecordingEffectAuthorityEvidenceStore();
        var commits = 0;

        var result = await Boundary(grant, capabilities, evidence).ExecuteAsync(
            fixture.Request with { NodeId = "missing-node" },
            _ => Task.FromResult(++commits));

        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.InvalidRequest, result.Status);
        Assert.False(result.CommitInvoked);
        Assert.Null(result.Decision);
        Assert.Equal(0, commits);
        Assert.Equal(0, grant.Calls);
        Assert.Equal(0, capabilities.Calls);
        Assert.Empty(evidence.Decisions);
    }

    [Fact]
    public async Task Boundary_kind_must_match_the_exact_canonical_node_and_effect_capability()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create();
        var grant = new StubEffectAuthorityGrantResolver { Resolution = fixture.Resolution };
        var capabilities = new StubEffectCapabilityAdmissionService();
        var evidence = new RecordingEffectAuthorityEvidenceStore();

        var result = await Boundary(grant, capabilities, evidence).ExecuteAsync(
            fixture.Request with { BoundaryKind = GovernedLoopEffectBoundaryKind.ConversationPublication },
            _ => Task.FromResult(true));

        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.InvalidRequest, result.Status);
        Assert.False(result.CommitInvoked);
        Assert.Equal(0, grant.Calls);
        Assert.Equal(0, capabilities.Calls);
        Assert.Empty(evidence.Decisions);
    }

    private static GovernedLoopEffectAuthorityBoundary Boundary(
        StubEffectAuthorityGrantResolver grant,
        StubEffectCapabilityAdmissionService capabilities,
        RecordingEffectAuthorityEvidenceStore evidence,
        RecordingEffectAuthorityTransaction? transaction = null,
        TimeProvider? timeProvider = null)
        => new(grant, capabilities, evidence, transaction ?? new RecordingEffectAuthorityTransaction(), timeProvider ?? new FixedEffectAuthorityTimeProvider(GovernedLoopEffectAuthorityTestFixture.Now));
}
