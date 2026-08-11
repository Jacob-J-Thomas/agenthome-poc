using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Application.Tests.Capabilities;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Governance.Authority.Grants;

public sealed class AuthorityGrantResolverTests
{
    [Fact]
    public async Task Exact_active_grant_returns_only_its_revalidated_ceiling_and_evidence()
    {
        var harness = new Harness(ActiveSnapshot());

        var result = await harness.Resolver.ResolveAsync(Reference(harness.Snapshot.CurrentGrant));

        Assert.Equal(AuthorityGrantResolutionStatus.Active, result.Status);
        Assert.Equal(harness.Snapshot.CurrentGrant, result.Grant);
        Assert.Equal(harness.Snapshot.CurrentGrant.RequestedCeiling, result.EffectiveCeiling);
        Assert.Equal(64, result.DependencyEvidenceHash.Length);
        Assert.Equal(AuthorityGrantApplicationTestFixture.Now, result.EvaluatedAtUtc);
        Assert.Equal(1, harness.Transaction.Executions);
    }

    [Theory]
    [InlineData(AuthorityGrantLifecycleStatus.Suspended, AuthorityGrantResolutionStatus.Suspended)]
    [InlineData(AuthorityGrantLifecycleStatus.Revoked, AuthorityGrantResolutionStatus.Revoked)]
    [InlineData(AuthorityGrantLifecycleStatus.Expired, AuthorityGrantResolutionStatus.Expired)]
    public async Task Closed_lifecycle_postures_never_expose_an_effective_ceiling(
        AuthorityGrantLifecycleStatus lifecycle,
        AuthorityGrantResolutionStatus expected)
    {
        var snapshot = LifecycleSnapshot(lifecycle);
        var harness = new Harness(snapshot);

        var result = await harness.Resolver.ResolveAsync(Reference(snapshot.CurrentGrant));

        Assert.Equal(expected, result.Status);
        Assert.Empty(result.EffectiveCeiling.Capabilities);
        Assert.Empty(result.EffectiveCeiling.DataClasses);
        Assert.Equal(0, harness.Profile.Calls);
    }

    [Fact]
    public async Task Time_boundaries_are_closed_at_exact_endpoints()
    {
        var futureGrant = AuthorityGrantApplicationTestFixture.Grant(
            boundary: AuthorityGrantApplicationTestFixture.Boundary(
                effective: AuthorityGrantApplicationTestFixture.Now.AddMinutes(1),
                expires: AuthorityGrantApplicationTestFixture.Now.AddHours(1)),
            recordedAtUtc: AuthorityGrantApplicationTestFixture.Now.AddMinutes(-1));
        var future = new Harness(SingleSnapshot(futureGrant));
        var expiringGrant = AuthorityGrantApplicationTestFixture.Grant(
            boundary: AuthorityGrantApplicationTestFixture.Boundary(
                effective: AuthorityGrantApplicationTestFixture.Now.AddHours(-1),
                expires: AuthorityGrantApplicationTestFixture.Now),
            recordedAtUtc: AuthorityGrantApplicationTestFixture.Now.AddHours(-1));
        var expiring = new Harness(SingleSnapshot(expiringGrant));

        var notEffective = await future.Resolver.ResolveAsync(Reference(futureGrant));
        var expired = await expiring.Resolver.ResolveAsync(Reference(expiringGrant));

        Assert.Equal(AuthorityGrantResolutionStatus.NotEffective, notEffective.Status);
        Assert.Equal(AuthorityGrantResolutionStatus.Expired, expired.Status);
        Assert.Equal(0, future.Profile.Calls);
        Assert.Equal(0, expiring.Profile.Calls);
    }

    [Fact]
    public async Task Historical_exact_reference_is_stale_without_following_current_revision()
    {
        var first = AuthorityGrantApplicationTestFixture.Grant(recordedAtUtc: AuthorityGrantApplicationTestFixture.Now.AddMinutes(-2));
        var second = AuthorityGrantApplicationTestFixture.Grant(
            revision: 2,
            predecessor: first,
            ceiling: AuthorityGrantApplicationTestFixture.Ceiling(maxTargets: 1),
            recordedAtUtc: AuthorityGrantApplicationTestFixture.Now.AddMinutes(-1));
        var firstEvidence = AuthorityGrantApplicationTestFixture.CommittedEvidence(first, "seed-create", AuthorityGrantApplicationTestFixture.Hash64('1'));
        var secondEvidence = TransitionEvidence(second, AuthorityGrantOperationKind.Narrow, AuthorityGrantApplicationTestFixture.Hash64('2'));
        var snapshot = AuthorityGrantApplicationTestFixture.Snapshot([first, second], [firstEvidence, secondEvidence]);
        var harness = new Harness(snapshot);

        var result = await harness.Resolver.ResolveAsync(Reference(first));

        Assert.Equal(AuthorityGrantResolutionStatus.Stale, result.Status);
        Assert.Equal(first, result.Grant);
        Assert.Equal(second, harness.Snapshot.CurrentGrant);
        Assert.Equal(0, harness.Profile.Calls);
    }

    [Theory]
    [InlineData("profile", AuthorityGrantResolutionStatus.ProfileUnavailable)]
    [InlineData("role", AuthorityGrantResolutionStatus.RoleUnavailable)]
    [InlineData("publication", AuthorityGrantResolutionStatus.LoopUnavailable)]
    [InlineData("owner", AuthorityGrantResolutionStatus.LoopUnavailable)]
    [InlineData("operation", AuthorityGrantResolutionStatus.LoopUnavailable)]
    [InlineData("ceiling", AuthorityGrantResolutionStatus.CeilingExceeded)]
    public async Task Dependency_substitution_status_and_ceiling_fail_closed(string failure, AuthorityGrantResolutionStatus expected)
    {
        var harness = new Harness(ActiveSnapshot());
        switch (failure)
        {
            case "profile":
                harness.Profile.Status = AuthorityGrantDependencyStatus.Expired;
                break;
            case "role":
                harness.Role.Status = AuthorityGrantDependencyStatus.Stale;
                break;
            case "publication":
                harness.Publication.Status = GovernedLoopPublishedRevisionResolutionStatus.Disabled;
                break;
            case "owner":
                harness.Binding.SubstituteOwner = true;
                break;
            case "operation":
                harness.Publication.ObservedOperationId = new string('x', 121);
                break;
            case "ceiling":
                harness.Binding.CapabilityIds = [];
                break;
        }

        var result = await harness.Resolver.ResolveAsync(Reference(harness.Snapshot.CurrentGrant));

        Assert.Equal(expected, result.Status);
        Assert.Empty(result.EffectiveCeiling.Capabilities);
        Assert.Empty(result.DependencyEvidenceHash);
    }

    [Theory]
    [InlineData("future")]
    [InlineData("default")]
    [InlineData("non-utc")]
    public async Task Invalid_or_future_role_lifecycle_time_fails_closed(string failure)
    {
        var harness = new Harness(ActiveSnapshot());
        var lifecycle = AuthorityGrantApplicationTestFixture.RoleLifecycle();
        harness.Role.Lifecycle = lifecycle with
        {
            UpdatedAtUtc = failure switch
            {
                "future" => AuthorityGrantApplicationTestFixture.Now.AddTicks(1),
                "default" => default,
                "non-utc" => lifecycle.UpdatedAtUtc.ToOffset(TimeSpan.FromHours(1)),
                _ => throw new ArgumentOutOfRangeException(nameof(failure)),
            },
        };

        var result = await harness.Resolver.ResolveAsync(Reference(harness.Snapshot.CurrentGrant));

        Assert.Equal(AuthorityGrantResolutionStatus.RoleUnavailable, result.Status);
        Assert.Empty(result.EffectiveCeiling.Capabilities);
        Assert.Empty(result.DependencyEvidenceHash);
    }

    [Theory]
    [InlineData(33, AuthorityGrantResolutionStatus.Active)]
    [InlineData(129, AuthorityGrantResolutionStatus.LoopUnavailable)]
    public async Task Loop_binding_uses_the_loop_contract_bound_without_widening_the_requested_ceiling(
        int capabilityCount,
        AuthorityGrantResolutionStatus expected)
    {
        var harness = new Harness(ActiveSnapshot());
        harness.Binding.CapabilityIds = Enumerable.Range(1, capabilityCount - 1)
            .Select(index => $"org.embodysense/workspace/read-{index}")
            .Prepend(AuthorityGrantApplicationTestFixture.Capability().Id.Value)
            .ToArray();

        var result = await harness.Resolver.ResolveAsync(Reference(harness.Snapshot.CurrentGrant));

        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == AuthorityGrantResolutionStatus.Active, result.EffectiveCeiling.Capabilities.Count > 0);
    }

    [Fact]
    public async Task Invalid_missing_unavailable_and_malformed_store_results_are_distinct_and_fail_closed()
    {
        var snapshot = ActiveSnapshot();
        var invalid = new Harness(snapshot);
        var invalidResult = await invalid.Resolver.ResolveAsync(null);
        var missing = new Harness(snapshot);
        missing.Store.Result = new(AuthorityGrantStoreReadStatus.NotFound, 0, null, null);
        var missingResult = await missing.Resolver.ResolveAsync(Reference(snapshot.CurrentGrant));
        var unavailable = new Harness(snapshot);
        unavailable.Store.Result = new(AuthorityGrantStoreReadStatus.Unavailable, 0, null, null);
        var unavailableResult = await unavailable.Resolver.ResolveAsync(Reference(snapshot.CurrentGrant));
        var malformed = new Harness(snapshot);
        malformed.Store.Result = new(
            AuthorityGrantStoreReadStatus.Ready,
            1,
            new AuthorityGrantStoreSnapshot(snapshot.CurrentGrant, snapshot.Revisions, []),
            null);
        var malformedResult = await malformed.Resolver.ResolveAsync(Reference(snapshot.CurrentGrant));
        var splicedAttribution = new Harness(snapshot);
        var splicedEvidence = snapshot.Operations[0] with { ActorId = AuthorityGrantApplicationTestFixture.Actor("other-actor") };
        splicedAttribution.Store.Result = new(
            AuthorityGrantStoreReadStatus.Ready,
            1,
            new AuthorityGrantStoreSnapshot(snapshot.CurrentGrant, snapshot.Revisions, [splicedEvidence]),
            null);
        var splicedAttributionResult = await splicedAttribution.Resolver.ResolveAsync(Reference(snapshot.CurrentGrant));
        var impossibleReceipt = new Harness(snapshot);
        var deniedReceipt = snapshot.Operations[0] with
        {
            OperationId = "impossible-denied",
            RequestHash = AuthorityGrantApplicationTestFixture.Hash64('9'),
            Outcome = AuthorityGrantOperationOutcome.Denied,
            FailureCode = AuthorityGrantOperationFailureCode.AuthorityDenied,
            ResultingGrant = null,
            DependencyEvidenceHash = null,
            RecordedAtUtc = AuthorityGrantApplicationTestFixture.Now,
        };
        impossibleReceipt.Store.Result = new(
            AuthorityGrantStoreReadStatus.Ready,
            2,
            new AuthorityGrantStoreSnapshot(snapshot.CurrentGrant, snapshot.Revisions, [snapshot.Operations[0], deniedReceipt]),
            null);
        var impossibleReceiptResult = await impossibleReceipt.Resolver.ResolveAsync(Reference(snapshot.CurrentGrant));

        Assert.Equal(AuthorityGrantResolutionStatus.Invalid, invalidResult.Status);
        Assert.Null(invalidResult.RequestedReference);
        Assert.Equal(AuthorityGrantResolutionStatus.NotFound, missingResult.Status);
        Assert.Equal(AuthorityGrantResolutionStatus.Unavailable, unavailableResult.Status);
        Assert.Equal(AuthorityGrantResolutionStatus.Ambiguous, malformedResult.Status);
        Assert.Equal(AuthorityGrantResolutionStatus.Ambiguous, splicedAttributionResult.Status);
        Assert.Equal(AuthorityGrantResolutionStatus.Ambiguous, impossibleReceiptResult.Status);
    }

    [Fact]
    public async Task Trusted_clock_failure_and_pre_record_time_fail_closed()
    {
        var snapshot = ActiveSnapshot();
        var clock = new MutableTimeProvider { Value = default };
        var harness = new Harness(snapshot, timeProvider: clock);
        var defaultTime = await harness.Resolver.ResolveAsync(Reference(snapshot.CurrentGrant));
        clock.Value = snapshot.CurrentGrant.RecordedAtUtc.AddTicks(-1);
        var reversedTime = await harness.Resolver.ResolveAsync(Reference(snapshot.CurrentGrant));
        clock.Exception = new InvalidOperationException("clock unavailable");
        var exception = await harness.Resolver.ResolveAsync(Reference(snapshot.CurrentGrant));

        Assert.Equal(AuthorityGrantResolutionStatus.Unavailable, defaultTime.Status);
        Assert.Equal(AuthorityGrantResolutionStatus.Unavailable, reversedTime.Status);
        Assert.Equal(AuthorityGrantResolutionStatus.Unavailable, exception.Status);
    }

    [Fact]
    public async Task Cancellation_before_resolution_completes_is_propagated()
    {
        var harness = new Harness(ActiveSnapshot());
        harness.Store.Handler = async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return harness.Store.Result;
        };
        using var cancellation = new CancellationTokenSource();
        var pending = harness.Resolver.ResolveAsync(Reference(harness.Snapshot.CurrentGrant), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Store_and_fence_exceptions_fail_closed_without_leaking_a_ceiling()
    {
        var storeFailure = new Harness(ActiveSnapshot());
        storeFailure.Store.Exception = new IOException("store unavailable");
        var unavailable = await storeFailure.Resolver.ResolveAsync(Reference(storeFailure.Snapshot.CurrentGrant));
        var fenceFailure = new Harness(ActiveSnapshot(), new ThrowBeforeTransaction());
        var failedFence = await fenceFailure.Resolver.ResolveAsync(Reference(fenceFailure.Snapshot.CurrentGrant));

        Assert.Equal(AuthorityGrantResolutionStatus.Unavailable, unavailable.Status);
        Assert.Empty(unavailable.EffectiveCeiling.Capabilities);
        Assert.Equal(AuthorityGrantResolutionStatus.Unavailable, failedFence.Status);
        Assert.Empty(failedFence.EffectiveCeiling.Capabilities);
    }

    [Fact]
    public async Task Exact_active_proof_survives_authority_fence_disposal_failure()
    {
        var transaction = new ThrowAfterTransaction();
        var harness = new Harness(ActiveSnapshot(), transaction);

        var result = await harness.Resolver.ResolveAsync(Reference(harness.Snapshot.CurrentGrant));

        Assert.Equal(AuthorityGrantResolutionStatus.Active, result.Status);
        Assert.Equal(64, result.DependencyEvidenceHash.Length);
    }

    private static AuthorityGrantStoreSnapshot ActiveSnapshot()
    {
        var grant = AuthorityGrantApplicationTestFixture.Grant(recordedAtUtc: AuthorityGrantApplicationTestFixture.Now.AddMinutes(-1));
        return SingleSnapshot(grant);
    }

    private static AuthorityGrantStoreSnapshot SingleSnapshot(AuthorityGrant grant)
    {
        var evidence = AuthorityGrantApplicationTestFixture.CommittedEvidence(grant, "seed-create", AuthorityGrantApplicationTestFixture.Hash64('1'));
        return new AuthorityGrantStoreSnapshot(grant, [grant], [evidence]);
    }

    private static AuthorityGrantStoreSnapshot LifecycleSnapshot(AuthorityGrantLifecycleStatus status)
    {
        var expiry = status == AuthorityGrantLifecycleStatus.Expired ? AuthorityGrantApplicationTestFixture.Now.AddMinutes(-1) : AuthorityGrantApplicationTestFixture.Now.AddHours(1);
        var first = AuthorityGrantApplicationTestFixture.Grant(
            boundary: AuthorityGrantApplicationTestFixture.Boundary(
                effective: AuthorityGrantApplicationTestFixture.Now.AddHours(-2),
                expires: expiry),
            recordedAtUtc: AuthorityGrantApplicationTestFixture.Now.AddHours(-2));
        var second = AuthorityGrantApplicationTestFixture.Grant(
            status,
            2,
            first,
            recordedAtUtc: AuthorityGrantApplicationTestFixture.Now.AddMinutes(-1));
        var kind = status switch
        {
            AuthorityGrantLifecycleStatus.Suspended => AuthorityGrantOperationKind.Suspend,
            AuthorityGrantLifecycleStatus.Revoked => AuthorityGrantOperationKind.Revoke,
            AuthorityGrantLifecycleStatus.Expired => AuthorityGrantOperationKind.Expire,
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        return new AuthorityGrantStoreSnapshot(
            second,
            [first, second],
            [
                AuthorityGrantApplicationTestFixture.CommittedEvidence(first, "seed-create", AuthorityGrantApplicationTestFixture.Hash64('1')),
                TransitionEvidence(second, kind, AuthorityGrantApplicationTestFixture.Hash64('2')),
            ]);
    }

    private static AuthorityGrantOperationEvidence TransitionEvidence(AuthorityGrant grant, AuthorityGrantOperationKind kind, string requestHash)
        => new(
            AuthorityGrantContractLimits.CurrentSchemaVersion,
            $"seed-{kind.ToString().ToLowerInvariant()}",
            requestHash,
            kind,
            AuthorityGrantOperationOutcome.Committed,
            AuthorityGrantOperationFailureCode.None,
            grant.GrantId,
            grant.Revision.Value - 1L,
            Reference(grant),
            grant.ChangedByActorId,
            grant.Reason,
            AuthorityGrantApplicationTestFixture.Hash64('3'),
            kind is AuthorityGrantOperationKind.Narrow or AuthorityGrantOperationKind.Replace ? AuthorityGrantApplicationTestFixture.Hash64('4') : null,
            grant.RecordedAtUtc);

    private static AuthorityGrantReference Reference(AuthorityGrant grant) => new(grant.GrantId, grant.Revision, grant.ContentHash);

    private sealed class Harness
    {
        internal Harness(AuthorityGrantStoreSnapshot snapshot, ICapabilityAuthorityTransaction? transaction = null, TimeProvider? timeProvider = null)
        {
            Snapshot = snapshot;
            Store.Result = new(AuthorityGrantStoreReadStatus.Ready, snapshot.Operations.Count, snapshot, null);
            Transaction = transaction as StubCapabilityAuthorityTransaction ?? new StubCapabilityAuthorityTransaction();
            var selectedTransaction = transaction ?? Transaction;
            Resolver = new AuthorityGrantResolver(Store, Profile, Role, Publication, Binding, selectedTransaction, timeProvider ?? new MutableTimeProvider { Value = AuthorityGrantApplicationTestFixture.Now });
        }

        internal AuthorityGrantStoreSnapshot Snapshot { get; }
        internal ResolverStore Store { get; } = new();
        internal ProfileSource Profile { get; } = new();
        internal RoleSource Role { get; } = new();
        internal PublicationSource Publication { get; } = new();
        internal BindingSource Binding { get; } = new();
        internal StubCapabilityAuthorityTransaction Transaction { get; }
        internal AuthorityGrantResolver Resolver { get; }
    }

    private sealed class ResolverStore : IAuthorityGrantStore
    {
        internal AuthorityGrantStoreReadResult Result { get; set; } = null!;
        internal Exception? Exception { get; set; }
        internal Func<CancellationToken, Task<AuthorityGrantStoreReadResult>>? Handler { get; set; }

        public Task<AuthorityGrantStoreReadResult> ReadAsync(AuthorityGrantId grantId, CancellationToken cancellationToken = default)
            => Exception is not null
                ? Task.FromException<AuthorityGrantStoreReadResult>(Exception)
                : Handler?.Invoke(cancellationToken) ?? Task.FromResult(Result);

        public Task<AuthorityGrantStoreReadResult> ReadForMutationAsync(AuthorityGrantId grantId, string operationId, string requestHash, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AuthorityGrantStoreCommitResult> CommitAsync(AuthorityGrantStoreMutation mutation, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ProfileSource : IAuthorityGrantProfileSource
    {
        internal int Calls { get; private set; }
        internal AuthorityGrantDependencyStatus Status { get; set; } = AuthorityGrantDependencyStatus.Active;

        public Task<AuthorityGrantProfileResolution> ResolveAsync(AuthorityGrantProfilePin? pin, DateTimeOffset evaluatedAtUtc, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new AuthorityGrantProfileResolution(Status, pin, AuthorityGrantApplicationTestFixture.Profile(), AuthorityGrantApplicationTestFixture.Hash64('5')));
        }
    }

    private sealed class RoleSource : IAuthorityGrantRoleSource
    {
        internal AuthorityGrantDependencyStatus Status { get; set; } = AuthorityGrantDependencyStatus.Active;
        internal EmbodySense.Core.Application.ContextualRoles.Models.ContextualRoleLifecycleSnapshot? Lifecycle { get; set; }

        public Task<AuthorityGrantRoleResolution> ResolveAsync(AuthorityGrantRolePin? pin, CancellationToken cancellationToken = default)
        {
            var role = AuthorityGrantApplicationTestFixture.Role();
            return Task.FromResult(new AuthorityGrantRoleResolution(Status, pin, role, Lifecycle ?? AuthorityGrantApplicationTestFixture.RoleLifecycle(role), AuthorityGrantApplicationTestFixture.Hash64('6')));
        }
    }

    private sealed class PublicationSource : IGovernedLoopPublishedRevisionSource
    {
        internal GovernedLoopPublishedRevisionResolutionStatus Status { get; set; } = GovernedLoopPublishedRevisionResolutionStatus.Active;
        internal string ObservedOperationId { get; set; } = "publish-loop";

        public Task<GovernedLoopPublishedRevisionResolution> ResolveAsync(GovernedLoopRevisionPublicationPin? pin, CancellationToken cancellationToken = default)
            => Task.FromResult(AuthorityGrantApplicationTestFixture.PublishedLoop(pin) with { Status = Status, ObservedLifecycleHeadOperationId = ObservedOperationId });
    }

    private sealed class BindingSource : IGovernedLoopGrantBindingSource
    {
        internal bool SubstituteOwner { get; set; }
        internal IReadOnlyList<string>? CapabilityIds { get; set; }

        public Task<GovernedLoopGrantBindingResolution> ResolveAsync(GovernedLoopRevisionPublicationPin? pin, CancellationToken cancellationToken = default)
        {
            var owner = SubstituteOwner
                ? new Common.ContextualRoles.Models.ContextualRoleRevisionIdentity("other-role", 1)
                : AuthorityGrantApplicationTestFixture.Role().Identity;
            return Task.FromResult(new GovernedLoopGrantBindingResolution(
                AuthorityGrantDependencyStatus.Active,
                pin,
                owner,
                CapabilityIds ?? [AuthorityGrantApplicationTestFixture.Capability().Id.Value],
                AuthorityGrantApplicationTestFixture.Hash64('7')));
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        internal DateTimeOffset Value { get; set; }
        internal Exception? Exception { get; set; }

        public override DateTimeOffset GetUtcNow() => Exception is null ? Value : throw Exception;
    }

    private sealed class ThrowAfterTransaction : ICapabilityAuthorityTransaction
    {
        public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        {
            _ = await operation(cancellationToken);
            throw new IOException("fence disposal failed");
        }

        public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowBeforeTransaction : ICapabilityAuthorityTransaction
    {
        public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
            => Task.FromException<TResult>(new IOException("fence unavailable"));

        public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
