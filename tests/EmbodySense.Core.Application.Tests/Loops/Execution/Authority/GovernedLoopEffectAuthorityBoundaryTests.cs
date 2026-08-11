using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
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
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create(includeUnrelatedCapability: true);
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
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create(includeUnrelatedCapability: true);
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
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create(includeUnrelatedCapability: true);
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
    public async Task Provider_effect_ignores_unrelated_authority_dimensions_but_retains_exact_data_class_requirements()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create(includeUnrelatedAuthorityDimensions: true);
        var currentCeiling = fixture.Resolution.EffectiveCeiling with
        {
            MaxTargetCount = 1,
            MaxSideEffectClass = EmbodySense.Core.Common.Capabilities.Models.CapabilitySideEffectClass.None,
            AllowsRecurrence = false,
            AllowsExternalPublication = false,
            AllowsIrreversibleAction = false,
        };
        var capabilities = new StubEffectCapabilityAdmissionService
        {
            Result = new CapabilityRevalidationResult(
                true,
                fixture.Request.AdmissionReceipt.Evidence.CapabilityAdmission.Pins,
                "Every exact provider pin remains current.",
                CapabilityRevalidationStatus.Active),
        };

        var direct = await Boundary(
            new StubEffectAuthorityGrantResolver { Resolution = fixture.Resolution with { EffectiveCeiling = currentCeiling } },
            capabilities,
            new RecordingEffectAuthorityEvidenceStore()).ExecuteAsync(fixture.Request, _ => Task.FromResult("committed"));
        var withoutRequiredDataClasses = new AuthorityCeiling(
            currentCeiling.Capabilities,
            [],
            currentCeiling.MaxTargetCount,
            currentCeiling.MaxSideEffectClass,
            currentCeiling.AllowsRecurrence,
            currentCeiling.AllowsExternalPublication,
            currentCeiling.AllowsIrreversibleAction);
        var dataClassNarrowed = await Boundary(
            new StubEffectAuthorityGrantResolver
            {
                Resolution = fixture.Resolution with { EffectiveCeiling = withoutRequiredDataClasses },
            },
            capabilities,
            new RecordingEffectAuthorityEvidenceStore()).ExecuteAsync(fixture.Request with { EffectOperationId = "provider-data-class-check" }, _ => Task.FromResult("must-not-commit"));

        Assert.True(direct.CommitInvoked);
        Assert.Equal(GovernedLoopEffectAuthorityDisposition.Direct, direct.Decision?.Disposition);
        Assert.Equal(GovernedLoopEffectAuthorityReason.ActiveNarrowed, direct.Decision?.Reason);
        Assert.Equal(1, fixture.Request.RequiredAuthority.MaxTargetCount);
        Assert.False(fixture.Request.RequiredAuthority.AllowsRecurrence);
        Assert.False(fixture.Request.RequiredAuthority.AllowsExternalPublication);
        Assert.False(fixture.Request.RequiredAuthority.AllowsIrreversibleAction);
        Assert.False(dataClassNarrowed.CommitInvoked);
        Assert.Equal(GovernedLoopEffectAuthorityDisposition.Deny, dataClassNarrowed.Decision?.Disposition);
        Assert.Equal(GovernedLoopEffectAuthorityReason.EffectOutsideCeiling, dataClassNarrowed.Decision?.Reason);
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

    [Theory]
    [InlineData(GovernedLoopEffectAuthorityUsageStoreStatus.TargetLimitExceeded, GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.EffectOutsideCeiling)]
    [InlineData(GovernedLoopEffectAuthorityUsageStoreStatus.GrantCompleted, GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.GrantCompleted)]
    [InlineData(GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable, GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.EvidenceUnavailable)]
    [InlineData(GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous, GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.EvidenceAmbiguous)]
    [InlineData(GovernedLoopEffectAuthorityUsageStoreStatus.Conflict, GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.EvidenceConflict)]
    public async Task Nonrenewable_usage_posture_is_durable_and_stops_workspace_actuation(
        GovernedLoopEffectAuthorityUsageStoreStatus usageStatus,
        GovernedLoopEffectAuthorityDisposition expectedDisposition,
        GovernedLoopEffectAuthorityReason expectedReason)
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create(
            toolEnabledProvider: true,
            completionConstraint: AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion);
        var request = WorkspaceRequest(fixture.Request);
        var capabilities = new StubEffectCapabilityAdmissionService
        {
            Result = new CapabilityRevalidationResult(
                true,
                fixture.Request.AdmissionReceipt.Evidence.CapabilityAdmission.Pins,
                "Every admitted pin is current.",
                CapabilityRevalidationStatus.Active),
        };
        var usage = new RecordingEffectAuthorityUsageStore { ReserveStatus = usageStatus };
        var commits = 0;

        var result = await Boundary(
            new StubEffectAuthorityGrantResolver { Resolution = fixture.Resolution },
            capabilities,
            new RecordingEffectAuthorityEvidenceStore(),
            usage: usage).ExecuteAsync(request, _ => Task.FromResult(++commits));

        Assert.False(result.CommitInvoked);
        Assert.Equal(0, commits);
        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.Decided, result.Status);
        Assert.Equal(expectedDisposition, result.Decision?.Disposition);
        Assert.Equal(expectedReason, result.Decision?.Reason);
        Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(result.Decision).IsValid);
        var reservation = Assert.Single(usage.Reservations);
        Assert.Equal(request.AdmissionReceipt.ContentHash, reservation.AdmissionReceiptHash);
        Assert.Equal(request.ExecutionBinding.RunId, reservation.RunId);
        Assert.Equal(request.TargetFingerprint, reservation.TargetFingerprint);
        Assert.Equal(request.AdmissionReceipt.Intent.AuthorityGrant, reservation.Grant);
        if (usageStatus == GovernedLoopEffectAuthorityUsageStoreStatus.TargetLimitExceeded)
        {
            Assert.Equal(0, result.Decision?.CurrentAuthority?.Ceiling.MaxTargetCount);
        }

        if (usageStatus == GovernedLoopEffectAuthorityUsageStoreStatus.GrantCompleted)
        {
            Assert.Equal(GovernedLoopEffectAuthorityGrantPosture.Completed, result.Decision?.CurrentAuthority?.GrantPosture);
        }
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

    [Fact]
    public async Task Tool_enabled_provider_transport_requires_both_model_and_workspace_capability_pins()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create(toolEnabledProvider: true);
        var capabilities = new StubEffectCapabilityAdmissionService
        {
            Result = new CapabilityRevalidationResult(true, fixture.Request.RequiredCapabilityPins, "Both provider requirements are current.", CapabilityRevalidationStatus.Active),
        };
        var direct = await Boundary(
            new StubEffectAuthorityGrantResolver { Resolution = fixture.Resolution },
            capabilities,
            new RecordingEffectAuthorityEvidenceStore()).ExecuteAsync(fixture.Request, _ => Task.FromResult(true));

        Assert.True(direct.CommitInvoked);
        Assert.Equal(
            [
                GovernedLoopSequentialApplicationTestFixture.ModelInferenceCapabilityId,
                GovernedLoopSequentialApplicationTestFixture.WorkspaceCommandCapabilityId,
            ],
            fixture.Request.RequiredCapabilityPins.Select(pin => pin.DescriptorIdentity.Id.Value));

        var admitted = fixture.Request.RequiredAuthority;
        var modelOnlyPin = fixture.Request.RequiredCapabilityPins[0];
        var modelOnly = new GovernedLoopEffectAuthorityRequest(
            fixture.Request.AdmissionReceipt,
            fixture.Request.ExecutionBinding,
            fixture.Request.GraphArtifact,
            fixture.Request.NodeId,
            fixture.Request.NodeAttempt,
            fixture.Request.EffectOperationId,
            fixture.Request.CorrelationId,
            fixture.Request.BoundaryKind,
            new AuthorityCeiling(
                [modelOnlyPin.DescriptorIdentity],
                admitted.DataClasses,
                admitted.MaxTargetCount,
                admitted.MaxSideEffectClass,
                admitted.AllowsRecurrence,
                admitted.AllowsExternalPublication,
                admitted.AllowsIrreversibleAction),
            [modelOnlyPin]);
        var invalid = await Boundary(
            new StubEffectAuthorityGrantResolver { Resolution = fixture.Resolution },
            new StubEffectCapabilityAdmissionService(),
            new RecordingEffectAuthorityEvidenceStore()).ExecuteAsync(modelOnly, _ => Task.FromResult(true));

        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.InvalidRequest, invalid.Status);
        Assert.False(invalid.CommitInvoked);
    }

    private static GovernedLoopEffectAuthorityBoundary Boundary(
        StubEffectAuthorityGrantResolver grant,
        StubEffectCapabilityAdmissionService capabilities,
        RecordingEffectAuthorityEvidenceStore evidence,
        RecordingEffectAuthorityTransaction? transaction = null,
        TimeProvider? timeProvider = null,
        RecordingEffectAuthorityUsageStore? usage = null)
    {
        var authorityTransaction = transaction ?? new RecordingEffectAuthorityTransaction();
        return new(
            grant,
            capabilities,
            evidence,
            usage ?? new RecordingEffectAuthorityUsageStore(),
            authorityTransaction,
            timeProvider ?? new FixedEffectAuthorityTimeProvider(GovernedLoopEffectAuthorityTestFixture.Now));
    }

    private static GovernedLoopEffectAuthorityRequest WorkspaceRequest(GovernedLoopEffectAuthorityRequest providerRequest)
    {
        var workspacePin = providerRequest.AdmissionReceipt.Evidence.CapabilityAdmission.Pins.Single(pin =>
            string.Equals(
                pin.DescriptorIdentity.Id.Value,
                GovernedLoopSequentialApplicationTestFixture.WorkspaceCommandCapabilityId,
                StringComparison.Ordinal));
        var admitted = providerRequest.AdmissionReceipt.Evidence.EffectiveAuthority;
        return new GovernedLoopEffectAuthorityRequest(
            providerRequest.AdmissionReceipt,
            providerRequest.ExecutionBinding,
            providerRequest.GraphArtifact,
            providerRequest.NodeId,
            providerRequest.NodeAttempt,
            "workspace-effect-1",
            "workspace-correlation-1",
            GovernedLoopEffectBoundaryKind.WorkspaceActuation,
            new AuthorityCeiling(
                [workspacePin.DescriptorIdentity],
                admitted.DataClasses,
                1,
                EmbodySense.Core.Common.Capabilities.Models.CapabilitySideEffectClass.ReadOnly,
                false,
                false,
                false),
            [workspacePin],
            new string('f', 64));
    }
}
