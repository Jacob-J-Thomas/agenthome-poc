using EmbodySense.Core.Application.Governance.Authority.Delegation.Models;
using EmbodySense.Core.Common.Authority.Delegation;
using EmbodySense.Core.Common.Authority.Delegation.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Application.Tests.Governance.Authority.Delegation;

public sealed class AuthorityDelegationEnvelopeServiceTests
{
    [Fact]
    public async Task CreateAsync_CreatesHashValidExactEnvelopeInsideOneFence()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Created, result.Status);
        Assert.Equal("created", result.ReasonCode);
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>(result.Envelope);
        Assert.True(AuthorityDelegationContractValidator.Validate(envelope).IsValid);
        Assert.Equal(harness.Receipt.ContentHash, envelope.ParentEvidence.ParentAdmissionReceiptHash);
        Assert.Equal(harness.Receipt.Evidence.Binding, envelope.ParentEvidence.ParentExecution);
        Assert.Equal(harness.Request.Target, envelope.Target);
        Assert.Equal(harness.Request.Purpose, envelope.Purpose);
        Assert.Equal(harness.Time.UtcNow, envelope.IssuedAtUtc);
        Assert.Equal(1, harness.TransactionCount);
        Assert.Equal(["transaction", "grant", "origin", "target", "completion"], harness.Calls);
    }

    [Fact]
    public async Task CreateAsync_ReplaysTheFirstExactEnvelopeWithoutReadingAuthoritySourcesAgain()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var service = harness.CreateService();

        var created = await service.CreateAsync(harness.Request);
        harness.Time.UtcNow = harness.Time.UtcNow.AddMinutes(1);
        var replayed = await service.CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Created, created.Status);
        Assert.Equal(AuthorityDelegationServiceStatus.Replayed, replayed.Status);
        Assert.Equal("replayed", replayed.ReasonCode);
        Assert.Equal(created.Envelope, replayed.Envelope);
        Assert.Equal(1, harness.TransactionCount);
        Assert.Equal(["transaction", "grant", "origin", "target", "completion"], harness.Calls);
    }

    [Fact]
    public async Task CreateAsync_ConcurrentExactRequestsCreateOnceAndReplayTheFirstEnvelope()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var service = harness.CreateService();
        var grantStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGrant = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.GrantCallback = async (_, cancellationToken) =>
        {
            grantStarted.TrySetResult();
            await releaseGrant.Task.WaitAsync(cancellationToken);
            return harness.GrantResolution;
        };

        var creation = service.CreateAsync(harness.Request);
        await grantStarted.Task;
        var replay = service.CreateAsync(harness.Request);
        releaseGrant.TrySetResult();
        var created = await creation;
        var replayed = await replay;

        Assert.Equal(AuthorityDelegationServiceStatus.Created, created.Status);
        Assert.Equal(AuthorityDelegationServiceStatus.Replayed, replayed.Status);
        Assert.Equal(created.Envelope, replayed.Envelope);
        Assert.Equal(1, harness.TransactionCount);
        Assert.Equal(1, harness.GrantCount);
    }

    [Fact]
    public async Task CreateAsync_CancellationOfOneCallerDoesNotCancelSharedCreationForAnotherCaller()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var service = harness.CreateService();
        var grantStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGrant = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.GrantCallback = async (_, cancellationToken) =>
        {
            grantStarted.TrySetResult();
            await releaseGrant.Task.WaitAsync(cancellationToken);
            return harness.GrantResolution;
        };

        using var firstCancellation = new CancellationTokenSource();
        var first = service.CreateAsync(harness.Request, firstCancellation.Token);
        await grantStarted.Task;
        var second = service.CreateAsync(harness.Request);
        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        releaseGrant.TrySetResult();
        var replayed = await second;

        Assert.Equal(AuthorityDelegationServiceStatus.Replayed, replayed.Status);
        Assert.Equal(1, harness.TransactionCount);
        Assert.Equal(1, harness.GrantCount);
    }

    [Fact]
    public async Task CreateAsync_RejectsDifferentRequestThatReusesCommittedEnvelopeIdentity()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var service = harness.CreateService();
        var created = await service.CreateAsync(harness.Request);
        harness.Request = harness.Request with { OperationClass = "different-bounded-operation" };

        var conflicting = await service.CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Created, created.Status);
        Assert.Equal(AuthorityDelegationServiceStatus.EnvelopeIdConflict, conflicting.Status);
        Assert.Equal("envelope-id-conflict", conflicting.ReasonCode);
        Assert.Null(conflicting.Envelope);
        Assert.Equal(1, harness.TransactionCount);
        Assert.Equal(["transaction", "grant", "origin", "target", "completion"], harness.Calls);
    }

    [Fact]
    public async Task CreateAsync_AcceptsEqualAuthorityCeilingBecauseExactBindingNarrowsEnvelope()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();

        var result = await harness.CreateService().CreateAsync(harness.Request);

        var envelope = Assert.IsType<AuthorityDelegationEnvelope>(result.Envelope);
        Assert.Empty(envelope.SubsetProof.NarrowingDimensions);
        Assert.Equal(
            AuthorityDelegationContractHash.ComputeAuthorityScopeHash(harness.Receipt.Evidence.EffectiveAuthority, []),
            envelope.SubsetProof.ParentAuthorityScopeHash);
    }

    [Fact]
    public async Task CreateAsync_ReturnsNoEnvelopeForMalformedRequestAndInvokesNoSource()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.Request = harness.Request with { TargetClass = "*" };

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.InvalidContract, result.Status);
        Assert.Null(result.Envelope);
        Assert.Empty(harness.Calls);
    }

    [Fact]
    public async Task CreateAsync_ClampsImmediateEffectivityToTrustedIssueTimeWhenClockAdvances()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var requestedEffectiveAtUtc = harness.Time.UtcNow;
        harness.Request = harness.Request with
        {
            Boundary = new AuthorityDelegationBoundary(
                requestedEffectiveAtUtc,
                requestedEffectiveAtUtc.AddMinutes(1),
                AuthorityDelegationCompletionConstraintKind.None),
        };
        harness.Time.UtcNow = requestedEffectiveAtUtc.AddTicks(17);
        harness.GrantResolution = harness.GrantResolution with { EvaluatedAtUtc = harness.Time.UtcNow };

        var result = await harness.CreateService().CreateAsync(harness.Request);

        var envelope = Assert.IsType<AuthorityDelegationEnvelope>(result.Envelope);
        Assert.Equal(AuthorityDelegationServiceStatus.Created, result.Status);
        Assert.Equal(harness.Time.UtcNow, envelope.IssuedAtUtc);
        Assert.Equal(harness.Time.UtcNow, envelope.Boundary.EffectiveAtUtc);
        Assert.True(envelope.Boundary.EffectiveAtUtc > requestedEffectiveAtUtc);
    }

    [Fact]
    public async Task CreateAsync_AcceptsGrantResolverEvaluationLaterThanEntryTime()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var entryAtUtc = harness.Time.UtcNow;
        harness.GrantCallback = (_, _) =>
        {
            harness.Time.UtcNow = entryAtUtc.AddTicks(11);
            return Task.FromResult(harness.GrantResolution with { EvaluatedAtUtc = harness.Time.UtcNow });
        };

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Created, result.Status);
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>(result.Envelope);
        Assert.Equal(entryAtUtc.AddTicks(11), envelope.ParentEvidence.EvaluatedAtUtc);
        Assert.Equal(envelope.ParentEvidence.EvaluatedAtUtc, envelope.IssuedAtUtc);
        Assert.Equal(envelope.IssuedAtUtc, envelope.Boundary.EffectiveAtUtc);
    }

    [Fact]
    public async Task CreateAsync_RejectsParentThatExpiresWhileSourcesAreResolving()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var parentExpiry = Assert.IsType<DateTimeOffset>(harness.Grant.Boundary.ExpiresAtUtc);
        harness.CompletionCallback = _ =>
        {
            harness.Time.UtcNow = parentExpiry;
            return Task.FromResult(new AuthorityDelegationCompletionResolution(AuthorityDelegationCompletionStatus.Active));
        };

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.ParentExpired, result.Status);
        Assert.Null(result.Envelope);
        Assert.Equal(["transaction", "grant", "origin", "target", "completion"], harness.Calls);
    }

    [Fact]
    public async Task CreateAsync_FailsClosedWhenTrustedTimeMovesBackwardDuringResolution()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.CreateOriginCallback = (_, _) =>
        {
            harness.Time.UtcNow = harness.Time.UtcNow.AddTicks(-1);
            return Task.FromResult(harness.OriginResolution);
        };

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Unavailable, result.Status);
        Assert.Null(result.Envelope);
        Assert.Equal(["transaction", "grant", "origin"], harness.Calls);
    }

    [Fact]
    public async Task CreateAsync_RejectsClampedEffectivityAtExpiredBoundary()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var requestedEffectiveAtUtc = harness.Time.UtcNow;
        harness.Request = harness.Request with
        {
            Boundary = new AuthorityDelegationBoundary(
                requestedEffectiveAtUtc,
                requestedEffectiveAtUtc.AddTicks(1),
                AuthorityDelegationCompletionConstraintKind.None),
        };
        harness.Time.UtcNow = requestedEffectiveAtUtc.AddTicks(1);

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.InvalidContract, result.Status);
        Assert.Null(result.Envelope);
        Assert.Equal(["transaction"], harness.Calls);
    }

    [Fact]
    public async Task CreateAsync_AllowsTargetCompletionWithoutLocalExpiryUnderFiniteParentGrant()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.Request = harness.Request with
        {
            Boundary = new AuthorityDelegationBoundary(
                harness.Time.UtcNow,
                null,
                AuthorityDelegationCompletionConstraintKind.TargetCompletion),
        };
        harness.OriginResolution = harness.CreateOriginResolution(AuthorityDelegationOriginResolutionStatus.Current);

        var result = await harness.CreateService().CreateAsync(harness.Request);

        var envelope = Assert.IsType<AuthorityDelegationEnvelope>(result.Envelope);
        Assert.Null(envelope.Boundary.ExpiresAtUtc);
        Assert.Equal(AuthorityDelegationCompletionConstraintKind.TargetCompletion, envelope.Boundary.CompletionConstraint);
    }

    [Fact]
    public async Task CreateAsync_RejectsTargetCompletionBoundaryThatCannotBecomeEffectiveBeforeParentExpiry()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var parentExpiry = Assert.IsType<DateTimeOffset>(harness.Grant.Boundary.ExpiresAtUtc);
        harness.Request = harness.Request with
        {
            Boundary = new AuthorityDelegationBoundary(
                parentExpiry,
                null,
                AuthorityDelegationCompletionConstraintKind.TargetCompletion),
        };
        harness.OriginResolution = harness.CreateOriginResolution(AuthorityDelegationOriginResolutionStatus.Current);

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.InvalidContract, result.Status);
        Assert.Null(result.Envelope);
        Assert.Equal(["transaction"], harness.Calls);
    }

    [Fact]
    public async Task CreateAsync_RejectsAdmissionEvidenceRecordedAfterTrustedNow()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var futureReceipt = GovernedLoopAdmissionContractHash.Apply(harness.Receipt with
        {
            RecordedAtUtc = harness.Time.UtcNow.AddMinutes(1),
            ContentHash = string.Empty,
        });
        harness.Request = harness.Request with { ParentAdmission = futureReceipt };

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Unavailable, result.Status);
        Assert.Equal(["transaction"], harness.Calls);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task CreateAsync_BindsServerResolvedTargetMaximumEvidenceHash()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.TargetResolution = harness.CreateTargetResolution(
            AuthorityDelegationTargetResolutionStatus.Active,
            AuthorityDelegationServiceTestHarness.Hash('9'));

        var result = await harness.CreateService().CreateAsync(harness.Request);

        var envelope = Assert.IsType<AuthorityDelegationEnvelope>(result.Envelope);
        Assert.Equal(AuthorityDelegationServiceTestHarness.Hash('9'), envelope.SubsetProof.TargetMaximumEvidenceHash);
    }

    [Fact]
    public async Task CreateAsync_UsesEntrySnapshotWhenCallerMutatesPinsDuringPortAwait()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var callerPins = new List<EmbodySense.Core.Common.Capabilities.Models.CapabilityAdmissionPin>();
        harness.Request = harness.Request with { DelegatedCapabilityPins = callerPins };
        var foreignPin = TestCapabilityAdmissionFactory.Create(LoopCapabilityRequirements.CreateDefaultConversationManifest()).Pins[0];
        harness.CreateOriginCallback = (_, _) =>
        {
            callerPins.Add(foreignPin);
            return Task.FromResult(harness.OriginResolution);
        };

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Single(callerPins);
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>(result.Envelope);
        Assert.Empty(envelope.DelegatedCapabilityPins);
        Assert.True(AuthorityDelegationContractValidator.Validate(envelope).IsValid);
    }
}
