using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Application.Tests.Capabilities;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Governance.Authority.Grants;

public sealed class AuthorityGrantLifecycleServiceTests
{
    [Theory]
    [InlineData(AuthorityGrantOperationKind.Create, AuthorityGrantLifecycleStatus.Active)]
    [InlineData(AuthorityGrantOperationKind.Narrow, AuthorityGrantLifecycleStatus.Active)]
    [InlineData(AuthorityGrantOperationKind.Suspend, AuthorityGrantLifecycleStatus.Suspended)]
    [InlineData(AuthorityGrantOperationKind.Replace, AuthorityGrantLifecycleStatus.Active)]
    [InlineData(AuthorityGrantOperationKind.Revoke, AuthorityGrantLifecycleStatus.Revoked)]
    [InlineData(AuthorityGrantOperationKind.Expire, AuthorityGrantLifecycleStatus.Expired)]
    public async Task Every_supported_transition_commits_immutable_evidence(
        AuthorityGrantOperationKind kind,
        AuthorityGrantLifecycleStatus expectedStatus)
    {
        var harness = new Harness();
        AuthorityGrant? current = null;
        if (kind != AuthorityGrantOperationKind.Create)
        {
            var boundary = kind == AuthorityGrantOperationKind.Expire
                ? AuthorityGrantApplicationTestFixture.Boundary(expires: AuthorityGrantApplicationTestFixture.Now)
                : AuthorityGrantApplicationTestFixture.Boundary();
            current = AuthorityGrantApplicationTestFixture.Grant(boundary: boundary, recordedAtUtc: AuthorityGrantApplicationTestFixture.Now.AddMinutes(-1));
            harness.Store.Seed(current);
        }

        var ceiling = kind == AuthorityGrantOperationKind.Narrow
            ? AuthorityGrantApplicationTestFixture.Ceiling(maxTargets: 1)
            : null;
        var request = AuthorityGrantApplicationTestFixture.Request(kind, current, ceiling: ceiling);

        var result = await harness.Service.MutateAsync(request);

        Assert.Equal(AuthorityGrantMutationStatus.Committed, result.Status);
        Assert.Equal(expectedStatus, result.Grant!.Status);
        Assert.NotNull(result.Evidence);
        Assert.Equal(request.RequestHash, result.Evidence!.RequestHash);
        Assert.Equal(1, harness.Store.CommitCalls);
        Assert.False(harness.Store.LastCommitToken.CanBeCanceled);
    }

    [Fact]
    public async Task Exact_replay_precedes_changed_authority_and_dependency_sources()
    {
        var harness = new Harness();
        var request = AuthorityGrantApplicationTestFixture.Request();
        var committed = await harness.Service.MutateAsync(request);
        var authorizationCalls = harness.Authorizer.Calls;
        var profileCalls = harness.Profile.Calls;
        harness.Authorizer.Status = AuthorityGrantActorAuthorizationStatus.Denied;
        harness.Profile.Status = AuthorityGrantDependencyStatus.Unavailable;

        var replay = await harness.Service.MutateAsync(request);

        Assert.Equal(AuthorityGrantMutationStatus.Committed, committed.Status);
        Assert.Equal(AuthorityGrantMutationStatus.Replayed, replay.Status);
        Assert.Equal(committed.Evidence, replay.Evidence);
        Assert.Equal(committed.Grant, replay.Grant);
        Assert.Equal(authorizationCalls, harness.Authorizer.Calls);
        Assert.Equal(profileCalls, harness.Profile.Calls);
        Assert.Equal(1, harness.Store.CommitCalls);
    }

    [Fact]
    public async Task Changed_intent_collision_fails_before_authorization()
    {
        var harness = new Harness();
        var request = AuthorityGrantApplicationTestFixture.Request();
        Assert.Equal(AuthorityGrantMutationStatus.Committed, (await harness.Service.MutateAsync(request)).Status);
        var authorizationCalls = harness.Authorizer.Calls;
        var changed = AuthorityGrantMutationRequestHash.Apply(request with
        {
            Reason = AuthorityGrantApplicationTestFixture.Purpose("A different bounded lifecycle purpose."),
            RequestHash = string.Empty,
        });

        var result = await harness.Service.MutateAsync(changed);

        Assert.Equal(AuthorityGrantMutationStatus.Conflict, result.Status);
        Assert.Null(result.Evidence);
        Assert.Null(result.Grant);
        Assert.Equal(authorizationCalls, harness.Authorizer.Calls);
        Assert.Equal(1, harness.Store.CommitCalls);
    }

    [Fact]
    public async Task Receipt_only_terminal_outcome_is_durable_and_replayed_without_reauthorization()
    {
        var harness = new Harness();
        var request = AuthorityGrantApplicationTestFixture.Request(
            AuthorityGrantOperationKind.Suspend,
            expectedRevision: 1,
            expectedStatus: AuthorityGrantLifecycleStatus.Active);

        var first = await harness.Service.MutateAsync(request);
        harness.Authorizer.Status = AuthorityGrantActorAuthorizationStatus.Denied;
        var replay = await harness.Service.MutateAsync(request);

        Assert.Equal(AuthorityGrantMutationStatus.NotFound, first.Status);
        Assert.NotNull(first.Evidence);
        Assert.Null(first.Evidence!.ResultingGrant);
        Assert.Equal(AuthorityGrantMutationStatus.Replayed, replay.Status);
        Assert.Equal(first.Evidence, replay.Evidence);
        Assert.Equal(1, harness.Authorizer.Calls);
        Assert.Equal(1, harness.Store.CommitCalls);
    }

    [Theory]
    [InlineData("wider-ceiling")]
    [InlineData("different-pin")]
    [InlineData("different-boundary")]
    public async Task Committed_replay_rejects_spliced_candidate_results(string splice)
    {
        var harness = new Harness();
        var request = AuthorityGrantApplicationTestFixture.Request();
        var binding = splice == "different-pin"
            ? request.CandidateBinding! with
            {
                Role = new AuthorityGrantRolePin(new("other-role", 1), AuthorityGrantApplicationTestFixture.Hash64('9')),
            }
            : request.CandidateBinding;
        var ceiling = splice == "wider-ceiling"
            ? AuthorityGrantApplicationTestFixture.Ceiling(maxTargets: request.CandidateCeiling!.MaxTargetCount + 1)
            : request.CandidateCeiling;
        var boundary = splice == "different-boundary"
            ? AuthorityGrantApplicationTestFixture.Boundary(
                effective: request.CandidateBoundary!.EffectiveAtUtc.AddMinutes(-1),
                expires: request.CandidateBoundary.ExpiresAtUtc)
            : request.CandidateBoundary;
        var storedGrant = AuthorityGrantApplicationTestFixture.Grant(binding: binding, ceiling: ceiling, boundary: boundary);
        var evidence = AuthorityGrantApplicationTestFixture.CommittedEvidence(storedGrant, request.OperationId, request.RequestHash);
        var snapshot = new AuthorityGrantStoreSnapshot(storedGrant, [storedGrant], [evidence]);
        harness.Store.ReadOverride = new(
            AuthorityGrantStoreReadStatus.Ready,
            1,
            snapshot,
            new AuthorityGrantStoredOperation(storedGrant.GrantId, evidence));

        var result = await harness.Service.MutateAsync(request);

        Assert.Equal(AuthorityGrantMutationStatus.Ambiguous, result.Status);
        Assert.Null(result.Grant);
        Assert.Null(result.Evidence);
        Assert.Equal(0, harness.Authorizer.Calls);
    }

    [Fact]
    public async Task Committed_replay_rejects_wrong_predecessor_status_for_exact_request()
    {
        var harness = new Harness();
        var first = AuthorityGrantApplicationTestFixture.Grant(recordedAtUtc: AuthorityGrantApplicationTestFixture.Now.AddMinutes(-2));
        var narrowedCeiling = AuthorityGrantApplicationTestFixture.Ceiling(maxTargets: 1);
        var second = AuthorityGrantApplicationTestFixture.Grant(
            revision: 2,
            predecessor: first,
            ceiling: narrowedCeiling,
            recordedAtUtc: AuthorityGrantApplicationTestFixture.Now.AddMinutes(-1));
        var request = AuthorityGrantApplicationTestFixture.Request(
            AuthorityGrantOperationKind.Narrow,
            first,
            ceiling: narrowedCeiling,
            expectedStatus: AuthorityGrantLifecycleStatus.Suspended);
        var create = AuthorityGrantApplicationTestFixture.CommittedEvidence(first, "seed-create", AuthorityGrantApplicationTestFixture.Hash64('1'));
        var narrow = AuthorityGrantApplicationTestFixture.CommittedEvidence(second, request.OperationId, request.RequestHash);
        var snapshot = new AuthorityGrantStoreSnapshot(second, [first, second], [create, narrow]);
        harness.Store.ReadOverride = new(
            AuthorityGrantStoreReadStatus.Ready,
            2,
            snapshot,
            new AuthorityGrantStoredOperation(second.GrantId, narrow));

        var result = await harness.Service.MutateAsync(request);

        Assert.Equal(AuthorityGrantMutationStatus.Ambiguous, result.Status);
        Assert.Equal(0, harness.Authorizer.Calls);
    }

    [Theory]
    [InlineData("not-found-with-later-grant")]
    [InlineData("unsupported-denied-receipt")]
    public async Task Receipt_replay_rejects_non_historical_or_non_service_evidence(string posture)
    {
        var harness = new Harness();
        var current = AuthorityGrantApplicationTestFixture.Grant(recordedAtUtc: AuthorityGrantApplicationTestFixture.Now.AddMinutes(-1));
        var request = posture == "not-found-with-later-grant"
            ? AuthorityGrantApplicationTestFixture.Request(AuthorityGrantOperationKind.Suspend, current)
            : AuthorityGrantApplicationTestFixture.Request();
        var evidence = new AuthorityGrantOperationEvidence(
            AuthorityGrantContractLimits.CurrentSchemaVersion,
            request.OperationId,
            request.RequestHash,
            request.Kind,
            posture == "not-found-with-later-grant" ? AuthorityGrantOperationOutcome.NotFound : AuthorityGrantOperationOutcome.Denied,
            posture == "not-found-with-later-grant" ? AuthorityGrantOperationFailureCode.LifecycleConflict : AuthorityGrantOperationFailureCode.AuthorityDenied,
            request.GrantId,
            request.ExpectedRevision,
            null,
            request.ActorId,
            request.Reason,
            AuthorityGrantApplicationTestFixture.Hash64('b'),
            null,
            AuthorityGrantApplicationTestFixture.Now);
        AuthorityGrantStoreSnapshot? snapshot = null;
        if (posture == "not-found-with-later-grant")
        {
            var create = AuthorityGrantApplicationTestFixture.CommittedEvidence(current, "seed-create", AuthorityGrantApplicationTestFixture.Hash64('1'));
            snapshot = new AuthorityGrantStoreSnapshot(current, [current], [create, evidence]);
        }

        harness.Store.ReadOverride = new(
            snapshot is null ? AuthorityGrantStoreReadStatus.NotFound : AuthorityGrantStoreReadStatus.Ready,
            snapshot?.Operations.Count ?? 1,
            snapshot,
            new AuthorityGrantStoredOperation(request.GrantId, evidence));

        var result = await harness.Service.MutateAsync(request);

        Assert.Equal(AuthorityGrantMutationStatus.Ambiguous, result.Status);
        Assert.Null(result.Grant);
        Assert.Equal(0, harness.Authorizer.Calls);
    }

    [Theory]
    [InlineData("valid-suspend-lifecycle-conflict")]
    [InlineData("boundary-before-ceiling")]
    public async Task Receipt_replay_enforces_deterministic_plan_precedence(string posture)
    {
        var harness = new Harness();
        var current = AuthorityGrantApplicationTestFixture.Grant(recordedAtUtc: AuthorityGrantApplicationTestFixture.Now.AddMinutes(-1));
        var request = posture == "valid-suspend-lifecycle-conflict"
            ? AuthorityGrantApplicationTestFixture.Request(AuthorityGrantOperationKind.Suspend, current)
            : AuthorityGrantApplicationTestFixture.Request(
                boundary: AuthorityGrantApplicationTestFixture.Boundary(
                    effective: AuthorityGrantApplicationTestFixture.Now.AddHours(-1),
                    expires: AuthorityGrantApplicationTestFixture.Now));
        var ceilingConflict = posture == "boundary-before-ceiling";
        var evidence = new AuthorityGrantOperationEvidence(
            AuthorityGrantContractLimits.CurrentSchemaVersion,
            request.OperationId,
            request.RequestHash,
            request.Kind,
            AuthorityGrantOperationOutcome.Conflict,
            ceilingConflict ? AuthorityGrantOperationFailureCode.CeilingExceeded : AuthorityGrantOperationFailureCode.LifecycleConflict,
            request.GrantId,
            request.ExpectedRevision,
            null,
            request.ActorId,
            request.Reason,
            AuthorityGrantApplicationTestFixture.Hash64('b'),
            ceilingConflict ? AuthorityGrantApplicationTestFixture.Hash64('c') : null,
            AuthorityGrantApplicationTestFixture.Now);
        AuthorityGrantStoreSnapshot? snapshot = null;
        if (!ceilingConflict)
        {
            var create = AuthorityGrantApplicationTestFixture.CommittedEvidence(current, "seed-create", AuthorityGrantApplicationTestFixture.Hash64('1'));
            snapshot = new AuthorityGrantStoreSnapshot(current, [current], [create, evidence]);
        }

        harness.Store.ReadOverride = new(
            snapshot is null ? AuthorityGrantStoreReadStatus.NotFound : AuthorityGrantStoreReadStatus.Ready,
            snapshot?.Operations.Count ?? 1,
            snapshot,
            new AuthorityGrantStoredOperation(request.GrantId, evidence));

        var result = await harness.Service.MutateAsync(request);

        Assert.Equal(AuthorityGrantMutationStatus.Ambiguous, result.Status);
        Assert.Null(result.Evidence);
        Assert.Equal(0, harness.Authorizer.Calls);
    }

    [Fact]
    public async Task Authorization_denial_and_malformed_echo_fail_closed_without_persistence()
    {
        var deniedHarness = new Harness();
        var current = AuthorityGrantApplicationTestFixture.Grant(recordedAtUtc: AuthorityGrantApplicationTestFixture.Now.AddMinutes(-1));
        deniedHarness.Store.Seed(current);
        deniedHarness.Authorizer.Status = AuthorityGrantActorAuthorizationStatus.Denied;
        var denied = await deniedHarness.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request(AuthorityGrantOperationKind.Suspend, current));
        var malformedHarness = new Harness();
        malformedHarness.Store.Seed(current);
        malformedHarness.Authorizer.EchoOperationId = "substituted-operation";
        var unavailable = await malformedHarness.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request(AuthorityGrantOperationKind.Suspend, current));

        Assert.Equal(AuthorityGrantMutationStatus.Denied, denied.Status);
        Assert.Equal(AuthorityGrantMutationStatus.Unavailable, unavailable.Status);
        Assert.Null(denied.Grant);
        Assert.Null(unavailable.Grant);
        Assert.Equal(0, deniedHarness.Store.CommitCalls);
        Assert.Equal(0, malformedHarness.Store.CommitCalls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task Cached_or_replayed_authorization_from_another_trusted_instant_fails_closed(int minuteDelta)
    {
        var harness = new Harness();
        harness.Authorizer.EchoEvaluatedAtUtc = AuthorityGrantApplicationTestFixture.Now.AddMinutes(minuteDelta);

        var result = await harness.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request());

        Assert.Equal(AuthorityGrantMutationStatus.Unavailable, result.Status);
        Assert.Null(result.Grant);
        Assert.Null(result.Evidence);
        Assert.Equal(1, harness.Authorizer.Calls);
        Assert.Equal(0, harness.Profile.Calls);
        Assert.Equal(0, harness.Store.CommitCalls);
    }

    [Fact]
    public async Task Invalid_request_and_authority_fence_failures_return_value_free_closed_results()
    {
        var invalidHarness = new Harness();
        var invalid = await invalidHarness.Service.MutateAsync(null);
        var unavailableHarness = new Harness(new ThrowBeforeTransaction());
        var unavailable = await unavailableHarness.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request());
        var deniedHarness = new Harness(new ThrowAfterTransaction());
        deniedHarness.Authorizer.Status = AuthorityGrantActorAuthorizationStatus.Denied;
        var ambiguous = await deniedHarness.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request());
        var durableHarness = new Harness(new ThrowAfterTransaction());
        var durable = await durableHarness.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request());

        Assert.Equal(AuthorityGrantMutationStatus.Invalid, invalid.Status);
        Assert.NotEmpty(invalid.ValidationErrors);
        Assert.Equal(AuthorityGrantMutationStatus.Unavailable, unavailable.Status);
        Assert.Equal(AuthorityGrantMutationStatus.Ambiguous, ambiguous.Status);
        Assert.Equal(AuthorityGrantMutationStatus.Committed, durable.Status);
        Assert.NotNull(durable.Evidence);
    }

    [Theory]
    [InlineData("read-exception", AuthorityGrantMutationStatus.Unavailable)]
    [InlineData("operation-conflict", AuthorityGrantMutationStatus.Conflict)]
    [InlineData("ambiguous", AuthorityGrantMutationStatus.Ambiguous)]
    [InlineData("malformed-existing", AuthorityGrantMutationStatus.Ambiguous)]
    public async Task Hostile_store_reads_fail_closed_before_authorization(string posture, AuthorityGrantMutationStatus expected)
    {
        var harness = new Harness();
        var request = AuthorityGrantApplicationTestFixture.Request();
        switch (posture)
        {
            case "read-exception":
                harness.Store.ReadException = new IOException("read unavailable");
                break;
            case "operation-conflict":
                harness.Store.ReadOverride = new(AuthorityGrantStoreReadStatus.OperationConflict, 0, null, null);
                break;
            case "ambiguous":
                harness.Store.ReadOverride = new(AuthorityGrantStoreReadStatus.Ambiguous, 0, null, null);
                break;
            case "malformed-existing":
                var grant = AuthorityGrantApplicationTestFixture.Grant();
                var evidence = AuthorityGrantApplicationTestFixture.CommittedEvidence(grant, "different-operation");
                harness.Store.ReadOverride = new(
                    AuthorityGrantStoreReadStatus.NotFound,
                    1,
                    null,
                    new AuthorityGrantStoredOperation(grant.GrantId, evidence));
                break;
        }

        var result = await harness.Service.MutateAsync(request);

        Assert.Equal(expected, result.Status);
        Assert.Equal(0, harness.Authorizer.Calls);
        Assert.Equal(0, harness.Store.CommitCalls);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("role")]
    [InlineData("publication")]
    [InlineData("binding")]
    [InlineData("owner")]
    public async Task Inactive_or_substituted_exact_dependencies_fail_without_persistence(string dependency)
    {
        var harness = new Harness();
        switch (dependency)
        {
            case "profile":
                harness.Profile.Status = AuthorityGrantDependencyStatus.Stale;
                break;
            case "role":
                harness.Role.Status = AuthorityGrantDependencyStatus.Disabled;
                break;
            case "publication":
                harness.Publication.Status = GovernedLoopPublishedRevisionResolutionStatus.Stale;
                break;
            case "binding":
                harness.LoopBinding.Status = AuthorityGrantDependencyStatus.Ambiguous;
                break;
            case "owner":
                harness.LoopBinding.SubstituteOwner = true;
                break;
        }

        var result = await harness.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request());

        Assert.Equal(AuthorityGrantMutationStatus.DependencyUnavailable, result.Status);
        Assert.Equal(0, harness.Store.CommitCalls);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("role")]
    [InlineData("publication")]
    [InlineData("binding")]
    public async Task Dependency_port_exceptions_are_closed_unavailability_not_ambient_failures(string dependency)
    {
        var harness = new Harness();
        switch (dependency)
        {
            case "profile":
                harness.Profile.Exception = new IOException("profile offline");
                break;
            case "role":
                harness.Role.Exception = new IOException("role offline");
                break;
            case "publication":
                harness.Publication.Exception = new IOException("loop offline");
                break;
            case "binding":
                harness.LoopBinding.Exception = new IOException("binding offline");
                break;
        }

        var result = await harness.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request());

        Assert.Equal(AuthorityGrantMutationStatus.DependencyUnavailable, result.Status);
        Assert.Equal(0, harness.Store.CommitCalls);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("role")]
    [InlineData("loop")]
    public async Task Every_dependency_ceiling_dimension_is_enforced_with_a_durable_conflict(string source)
    {
        var harness = new Harness();
        var binding = AuthorityGrantApplicationTestFixture.Binding();
        switch (source)
        {
            case "profile":
                var profile = AuthorityGrantApplicationTestFixture.Profile(ceiling: AuthorityGrantApplicationTestFixture.Ceiling(capabilities: []));
                harness.Profile.Profile = profile;
                binding = binding with
                {
                    Profile = new AuthorityGrantProfilePin(new(profile.ProfileId, profile.Revision), AuthorityGrantApplicationTestFixture.ProfileHash(profile)),
                };
                break;
            case "role":
                var role = AuthorityGrantApplicationTestFixture.Role(capabilityIds: []);
                harness.Role.Role = role;
                binding = binding with { Role = new AuthorityGrantRolePin(role.Identity, role.ContentHash) };
                break;
            case "loop":
                harness.LoopBinding.CapabilityIds = [];
                break;
        }

        var result = await harness.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request(binding: binding));

        Assert.Equal(AuthorityGrantMutationStatus.CeilingExceeded, result.Status);
        Assert.NotNull(result.Evidence);
        Assert.Equal(AuthorityGrantOperationFailureCode.CeilingExceeded, result.Evidence!.FailureCode);
        Assert.NotNull(result.Evidence.DependencyEvidenceHash);
        Assert.Equal(1, harness.Store.CommitCalls);
    }

    [Fact]
    public async Task Expired_candidate_boundary_commits_conflict_without_dependency_reads()
    {
        var harness = new Harness();
        var request = AuthorityGrantApplicationTestFixture.Request(
            boundary: AuthorityGrantApplicationTestFixture.Boundary(
                effective: AuthorityGrantApplicationTestFixture.Now.AddHours(-1),
                expires: AuthorityGrantApplicationTestFixture.Now));

        var result = await harness.Service.MutateAsync(request);

        Assert.Equal(AuthorityGrantMutationStatus.BoundaryConflict, result.Status);
        Assert.Equal(AuthorityGrantOperationFailureCode.BoundaryConflict, result.Evidence!.FailureCode);
        Assert.Equal(0, harness.Profile.Calls);
        Assert.Equal(0, harness.Role.Calls);
    }

    [Fact]
    public async Task Cancellation_before_durable_intent_propagates_and_does_not_commit()
    {
        var harness = new Harness();
        harness.Authorizer.Handler = async (request, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return harness.Authorizer.Decision(request);
        };
        using var cancellation = new CancellationTokenSource();
        var pending = harness.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request(), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, harness.Store.CommitCalls);
    }

    [Fact]
    public async Task Cancellation_after_durable_intent_does_not_cancel_commit_or_erase_proof()
    {
        using var cancellation = new CancellationTokenSource();
        var harness = new Harness();
        harness.Store.OnCommit = () => cancellation.Cancel();

        var result = await harness.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request(), cancellation.Token);

        Assert.Equal(AuthorityGrantMutationStatus.Committed, result.Status);
        Assert.NotNull(result.Evidence);
        Assert.False(harness.Store.LastCommitToken.CanBeCanceled);
    }

    [Fact]
    public async Task One_optimistic_store_conflict_is_retried_once()
    {
        var harness = new Harness();
        harness.Store.ConflictFirstCommit = true;

        var result = await harness.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request());

        Assert.Equal(AuthorityGrantMutationStatus.Committed, result.Status);
        Assert.Equal(2, harness.Store.CommitCalls);
        Assert.Equal(2, harness.Authorizer.Calls);
        Assert.Equal(2, harness.Profile.Calls);
    }

    [Fact]
    public async Task Two_optimistic_conflicts_exhaust_the_single_retry_budget()
    {
        var harness = new Harness();
        harness.Store.AlwaysConflict = true;

        var result = await harness.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request());

        Assert.Equal(AuthorityGrantMutationStatus.Conflict, result.Status);
        Assert.Equal(2, harness.Store.CommitCalls);
    }

    [Theory]
    [InlineData("operation-null", AuthorityGrantMutationStatus.Conflict)]
    [InlineData("operation-proof", AuthorityGrantMutationStatus.Conflict)]
    [InlineData("limit", AuthorityGrantMutationStatus.LimitExceeded)]
    [InlineData("unavailable", AuthorityGrantMutationStatus.Unavailable)]
    [InlineData("unknown", AuthorityGrantMutationStatus.Ambiguous)]
    public async Task Commit_port_dispositions_map_without_inventing_durable_proof(string disposition, AuthorityGrantMutationStatus expected)
    {
        var harness = new Harness();
        harness.Store.CommitFactory = mutation => disposition switch
        {
            "operation-null" => new(AuthorityGrantStoreCommitStatus.OperationConflict, mutation.ExpectedStoreGeneration, null, null),
            "operation-proof" => new(
                AuthorityGrantStoreCommitStatus.OperationConflict,
                mutation.ExpectedStoreGeneration,
                new AuthorityGrantStoredOperation(
                    mutation.Operation.GrantId,
                    mutation.Operation with { RequestHash = AuthorityGrantApplicationTestFixture.Hash64('f') }),
                null),
            "limit" => new(AuthorityGrantStoreCommitStatus.LimitExceeded, mutation.ExpectedStoreGeneration, null, null),
            "unavailable" => new(AuthorityGrantStoreCommitStatus.Unavailable, mutation.ExpectedStoreGeneration, null, null),
            _ => new((AuthorityGrantStoreCommitStatus)999, mutation.ExpectedStoreGeneration, null, null),
        };

        var result = await harness.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request());

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Evidence);
        Assert.Null(result.Grant);
    }

    [Fact]
    public async Task Store_replayed_commit_requires_and_returns_exact_proof()
    {
        var harness = new Harness();
        harness.Store.CommitStatusOverride = AuthorityGrantStoreCommitStatus.Replayed;

        var result = await harness.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request());

        Assert.Equal(AuthorityGrantMutationStatus.Replayed, result.Status);
        Assert.NotNull(result.Evidence);
        Assert.NotNull(result.Grant);
    }

    [Fact]
    public async Task Store_replayed_receipt_returns_the_same_replay_disposition_as_an_initial_read()
    {
        var harness = new Harness();
        harness.Store.CommitStatusOverride = AuthorityGrantStoreCommitStatus.Replayed;
        var request = AuthorityGrantApplicationTestFixture.Request(
            boundary: AuthorityGrantApplicationTestFixture.Boundary(
                effective: AuthorityGrantApplicationTestFixture.Now.AddHours(-1),
                expires: AuthorityGrantApplicationTestFixture.Now));

        var result = await harness.Service.MutateAsync(request);

        Assert.Equal(AuthorityGrantMutationStatus.Replayed, result.Status);
        Assert.Equal(AuthorityGrantOperationFailureCode.BoundaryConflict, result.Evidence!.FailureCode);
        Assert.Null(result.Grant);
    }

    [Theory]
    [InlineData("lifecycle")]
    [InlineData("boundary")]
    [InlineData("ceiling")]
    public async Task Receipt_commit_proof_rejects_missing_existing_target_snapshot(string posture)
    {
        var harness = new Harness();
        var current = AuthorityGrantApplicationTestFixture.Grant(recordedAtUtc: AuthorityGrantApplicationTestFixture.Now.AddMinutes(-1));
        harness.Store.Seed(current);
        var request = posture switch
        {
            "lifecycle" => AuthorityGrantApplicationTestFixture.Request(
                AuthorityGrantOperationKind.Create,
                current,
                expectedRevision: 0,
                expectedStatus: AuthorityGrantLifecycleStatus.Unknown),
            "boundary" => AuthorityGrantApplicationTestFixture.Request(
                AuthorityGrantOperationKind.Replace,
                current,
                boundary: AuthorityGrantApplicationTestFixture.Boundary(
                    effective: AuthorityGrantApplicationTestFixture.Now.AddHours(-1),
                    expires: AuthorityGrantApplicationTestFixture.Now)),
            _ => AuthorityGrantApplicationTestFixture.Request(AuthorityGrantOperationKind.Replace, current),
        };
        if (posture == "ceiling")
        {
            harness.LoopBinding.CapabilityIds = [];
        }

        harness.Store.CommitFactory = mutation => new AuthorityGrantStoreCommitResult(
            AuthorityGrantStoreCommitStatus.Committed,
            mutation.ExpectedStoreGeneration + 1,
            new AuthorityGrantStoredOperation(mutation.Operation.GrantId, mutation.Operation),
            null);

        var result = await harness.Service.MutateAsync(request);

        Assert.Equal(AuthorityGrantMutationStatus.Ambiguous, result.Status);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public async Task Commit_proof_rejects_a_valid_later_snapshot_tail()
    {
        var harness = new Harness();
        harness.Store.CommitFactory = mutation =>
        {
            var grant = mutation.GrantToAppend!;
            var later = new AuthorityGrantOperationEvidence(
                AuthorityGrantContractLimits.CurrentSchemaVersion,
                "later-conflict",
                AuthorityGrantApplicationTestFixture.Hash64('f'),
                AuthorityGrantOperationKind.Create,
                AuthorityGrantOperationOutcome.Conflict,
                AuthorityGrantOperationFailureCode.LifecycleConflict,
                grant.GrantId,
                0,
                null,
                grant.ChangedByActorId,
                grant.Reason,
                AuthorityGrantApplicationTestFixture.Hash64('e'),
                null,
                mutation.Operation.RecordedAtUtc.AddTicks(1));
            var snapshot = new AuthorityGrantStoreSnapshot(grant, [grant], [mutation.Operation, later]);
            return new AuthorityGrantStoreCommitResult(
                AuthorityGrantStoreCommitStatus.Replayed,
                mutation.ExpectedStoreGeneration + 2,
                new AuthorityGrantStoredOperation(grant.GrantId, mutation.Operation),
                snapshot);
        };

        var result = await harness.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request());

        Assert.Equal(AuthorityGrantMutationStatus.Ambiguous, result.Status);
        Assert.Null(result.Evidence);
        Assert.Null(result.Grant);
    }

    [Fact]
    public async Task Clock_failure_reversed_time_and_unmet_expiry_fail_before_commit()
    {
        var defaultClock = new Harness();
        defaultClock.Time.Value = default;
        var unavailable = await defaultClock.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request());
        var throwingClock = new Harness();
        throwingClock.Time.Exception = new InvalidOperationException("clock offline");
        var exception = await throwingClock.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request());
        var current = AuthorityGrantApplicationTestFixture.Grant(recordedAtUtc: AuthorityGrantApplicationTestFixture.Now.AddMinutes(1));
        var reversed = new Harness();
        reversed.Store.Seed(current);
        var reversedResult = await reversed.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request(AuthorityGrantOperationKind.Suspend, current));
        var expireCurrent = AuthorityGrantApplicationTestFixture.Grant(boundary: AuthorityGrantApplicationTestFixture.Boundary(expires: AuthorityGrantApplicationTestFixture.Now.AddMinutes(1)));
        var unmetExpiry = new Harness();
        unmetExpiry.Store.Seed(expireCurrent);
        var boundary = await unmetExpiry.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request(AuthorityGrantOperationKind.Expire, expireCurrent));

        Assert.Equal(AuthorityGrantMutationStatus.Unavailable, unavailable.Status);
        Assert.Equal(AuthorityGrantMutationStatus.Unavailable, exception.Status);
        Assert.Equal(AuthorityGrantMutationStatus.Unavailable, reversedResult.Status);
        Assert.Equal(AuthorityGrantMutationStatus.BoundaryConflict, boundary.Status);
    }

    [Fact]
    public async Task One_trusted_instant_governs_authorization_boundary_evaluation_and_evidence()
    {
        var authorizationTime = AuthorityGrantApplicationTestFixture.Now;
        var earlierPlanningTime = authorizationTime.AddMinutes(-2);
        var clock = new SequenceTimeProvider(authorizationTime, earlierPlanningTime);
        var harness = new Harness(timeProvider: clock);
        var request = AuthorityGrantApplicationTestFixture.Request(
            boundary: AuthorityGrantApplicationTestFixture.Boundary(
                effective: authorizationTime.AddHours(-1),
                expires: authorizationTime.AddMinutes(-1)));

        var result = await harness.Service.MutateAsync(request);

        Assert.Equal(AuthorityGrantMutationStatus.BoundaryConflict, result.Status);
        Assert.Equal(authorizationTime, harness.Authorizer.LastEvaluatedAtUtc);
        Assert.Equal(authorizationTime, result.Evidence!.RecordedAtUtc);
        Assert.Null(result.Grant);
        Assert.Equal(1, clock.Calls);
    }

    [Fact]
    public async Task Post_commit_exception_recovers_exact_durable_operation()
    {
        var harness = new Harness();
        harness.Store.ThrowAfterCommit = true;

        var result = await harness.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request());

        Assert.Equal(AuthorityGrantMutationStatus.Replayed, result.Status);
        Assert.NotNull(result.Evidence);
        Assert.NotNull(result.Grant);
    }

    [Fact]
    public async Task Maximum_store_generation_and_same_generation_fake_commit_fail_closed()
    {
        var exhausted = new Harness();
        exhausted.Store.ReadGenerationOverride = long.MaxValue;
        var rejectedRead = await exhausted.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request());
        var forged = new Harness();
        forged.Store.ReadGenerationOverride = long.MaxValue - 1;
        forged.Store.ReturnSameGenerationFakeCommit = true;
        var rejectedCommit = await forged.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request());
        var maximumReplay = new Harness();
        maximumReplay.Store.CommitFactory = mutation => new AuthorityGrantStoreCommitResult(
            AuthorityGrantStoreCommitStatus.Replayed,
            long.MaxValue,
            new AuthorityGrantStoredOperation(mutation.Operation.GrantId, mutation.Operation),
            null);
        var maximumReplayRequest = AuthorityGrantApplicationTestFixture.Request(
            boundary: AuthorityGrantApplicationTestFixture.Boundary(
                effective: AuthorityGrantApplicationTestFixture.Now.AddHours(-1),
                expires: AuthorityGrantApplicationTestFixture.Now));
        var rejectedReplay = await maximumReplay.Service.MutateAsync(maximumReplayRequest);

        Assert.Equal(AuthorityGrantMutationStatus.Ambiguous, rejectedRead.Status);
        Assert.Equal(0, exhausted.Authorizer.Calls);
        Assert.Equal(AuthorityGrantMutationStatus.Ambiguous, rejectedCommit.Status);
        Assert.Null(rejectedCommit.Evidence);
        Assert.Equal(AuthorityGrantMutationStatus.Ambiguous, rejectedReplay.Status);
        Assert.Null(rejectedReplay.Evidence);
    }

    [Fact]
    public async Task Shared_transaction_blocks_competing_authority_mutation_until_commit_finishes()
    {
        var transaction = new SerializingCapabilityAuthorityTransaction();
        var harness = new Harness(transaction);
        var commitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Store.CommitBarrier = async () =>
        {
            commitEntered.SetResult();
            await releaseCommit.Task;
        };
        var mutation = harness.Service.MutateAsync(AuthorityGrantApplicationTestFixture.Request());
        await commitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var competitorEntered = false;
        var competitor = transaction.ExecuteAsync(
            _ =>
            {
                competitorEntered = true;
                return Task.FromResult(true);
            });

        await Task.Delay(50);
        Assert.False(competitorEntered);
        releaseCommit.SetResult();
        Assert.Equal(AuthorityGrantMutationStatus.Committed, (await mutation).Status);
        Assert.True(await competitor);
        Assert.True(competitorEntered);
    }

    private sealed class Harness
    {
        internal Harness(ICapabilityAuthorityTransaction? transaction = null, TimeProvider? timeProvider = null)
        {
            Transaction = transaction ?? new StubCapabilityAuthorityTransaction();
            Service = new AuthorityGrantLifecycleService(Store, Authorizer, Profile, Role, Publication, LoopBinding, Transaction, timeProvider ?? Time);
        }

        internal GrantStore Store { get; } = new();
        internal AuthorizerStub Authorizer { get; } = new();
        internal ProfileSourceStub Profile { get; } = new();
        internal RoleSourceStub Role { get; } = new();
        internal PublicationSourceStub Publication { get; } = new();
        internal LoopBindingSourceStub LoopBinding { get; } = new();
        internal FixedTimeProvider Time { get; } = new(AuthorityGrantApplicationTestFixture.Now);
        internal ICapabilityAuthorityTransaction Transaction { get; }
        internal AuthorityGrantLifecycleService Service { get; }
    }

    private sealed class GrantStore : IAuthorityGrantStore
    {
        private readonly Dictionary<string, AuthorityGrantStoredOperation> _operations = new(StringComparer.Ordinal);
        private AuthorityGrantStoreSnapshot? _snapshot;
        private long _generation;

        internal int CommitCalls { get; private set; }
        internal bool ConflictFirstCommit { get; set; }
        internal bool ThrowAfterCommit { get; set; }
        internal Action? OnCommit { get; set; }
        internal Func<Task>? CommitBarrier { get; set; }
        internal CancellationToken LastCommitToken { get; private set; }
        internal long? ReadGenerationOverride { get; set; }
        internal bool ReturnSameGenerationFakeCommit { get; set; }
        internal bool AlwaysConflict { get; set; }
        internal AuthorityGrantStoreReadResult? ReadOverride { get; set; }
        internal Exception? ReadException { get; set; }
        internal Func<AuthorityGrantStoreMutation, AuthorityGrantStoreCommitResult>? CommitFactory { get; set; }
        internal AuthorityGrantStoreCommitStatus? CommitStatusOverride { get; set; }

        internal void Seed(AuthorityGrant grant)
        {
            var evidence = AuthorityGrantApplicationTestFixture.CommittedEvidence(grant, "seed-create", AuthorityGrantApplicationTestFixture.Hash64('a'));
            _snapshot = new AuthorityGrantStoreSnapshot(grant, [grant], [evidence]);
            _operations.Add(evidence.OperationId, new AuthorityGrantStoredOperation(grant.GrantId, evidence));
            _generation = 1;
        }

        public Task<AuthorityGrantStoreReadResult> ReadAsync(AuthorityGrantId grantId, CancellationToken cancellationToken = default)
            => Task.FromResult(Read(grantId, null));

        public Task<AuthorityGrantStoreReadResult> ReadForMutationAsync(AuthorityGrantId grantId, string operationId, string requestHash, CancellationToken cancellationToken = default)
            => Task.FromResult(Read(grantId, operationId));

        public async Task<AuthorityGrantStoreCommitResult> CommitAsync(AuthorityGrantStoreMutation mutation, CancellationToken cancellationToken = default)
        {
            CommitCalls++;
            LastCommitToken = cancellationToken;
            OnCommit?.Invoke();
            if (CommitBarrier is not null)
            {
                await CommitBarrier();
            }

            if (AlwaysConflict || ConflictFirstCommit && CommitCalls == 1)
            {
                _generation++;
                return new(AuthorityGrantStoreCommitStatus.StoreConflict, _generation, null, _snapshot);
            }

            if (CommitFactory is not null)
            {
                return CommitFactory(mutation);
            }

            if (mutation.ExpectedStoreGeneration != _generation)
            {
                if (!ReturnSameGenerationFakeCommit || mutation.ExpectedStoreGeneration != ReadGenerationOverride)
                {
                    return new(AuthorityGrantStoreCommitStatus.StoreConflict, _generation, null, _snapshot);
                }
            }

            if (ReturnSameGenerationFakeCommit)
            {
                var forgedStored = new AuthorityGrantStoredOperation(mutation.Operation.GrantId, mutation.Operation);
                var forgedSnapshot = mutation.GrantToAppend is null
                    ? null
                    : new AuthorityGrantStoreSnapshot(mutation.GrantToAppend, [mutation.GrantToAppend], [mutation.Operation]);
                return new AuthorityGrantStoreCommitResult(
                    AuthorityGrantStoreCommitStatus.Committed,
                    mutation.ExpectedStoreGeneration,
                    forgedStored,
                    forgedSnapshot);
            }

            _generation++;
            var stored = new AuthorityGrantStoredOperation(mutation.Operation.GrantId, mutation.Operation);
            _operations.Add(mutation.Operation.OperationId, stored);
            if (mutation.GrantToAppend is { } grant)
            {
                var revisions = (_snapshot?.Revisions ?? []).Append(grant).ToArray();
                var operations = (_snapshot?.Operations ?? []).Append(mutation.Operation).ToArray();
                _snapshot = new AuthorityGrantStoreSnapshot(grant, revisions, operations);
            }
            else if (_snapshot is not null)
            {
                _snapshot = new AuthorityGrantStoreSnapshot(
                    _snapshot.CurrentGrant,
                    _snapshot.Revisions,
                    _snapshot.Operations.Append(mutation.Operation).ToArray());
            }

            if (ThrowAfterCommit)
            {
                throw new IOException("lost response after commit");
            }

            return new AuthorityGrantStoreCommitResult(CommitStatusOverride ?? AuthorityGrantStoreCommitStatus.Committed, _generation, stored, _snapshot);
        }

        private AuthorityGrantStoreReadResult Read(AuthorityGrantId grantId, string? operationId)
        {
            if (ReadException is not null)
            {
                throw ReadException;
            }

            if (ReadOverride is not null)
            {
                return ReadOverride;
            }

            _operations.TryGetValue(operationId ?? string.Empty, out var existing);
            var exactSnapshot = _snapshot?.CurrentGrant.GrantId.Equals(grantId) == true ? _snapshot : null;
            return new AuthorityGrantStoreReadResult(
                exactSnapshot is null ? AuthorityGrantStoreReadStatus.NotFound : AuthorityGrantStoreReadStatus.Ready,
                ReadGenerationOverride ?? _generation,
                exactSnapshot,
                existing);
        }
    }

    private sealed class AuthorizerStub : IAuthorityGrantActorAuthorizer
    {
        internal int Calls { get; private set; }
        internal AuthorityGrantActorAuthorizationStatus Status { get; set; } = AuthorityGrantActorAuthorizationStatus.Authorized;
        internal string? EchoOperationId { get; set; }
        internal DateTimeOffset? EchoEvaluatedAtUtc { get; set; }
        internal DateTimeOffset LastEvaluatedAtUtc { get; private set; }
        internal Func<AuthorityGrantActorAuthorizationRequest, CancellationToken, Task<AuthorityGrantActorAuthorization>>? Handler { get; set; }

        internal AuthorityGrantActorAuthorization Decision(AuthorityGrantActorAuthorizationRequest request)
            => new(
                Status,
                EchoOperationId ?? request.Request.OperationId,
                request.RequestHash,
                request.Request.ActorId,
                EchoEvaluatedAtUtc ?? request.EvaluatedAtUtc,
                AuthorityGrantApplicationTestFixture.Hash64('b'));

        public Task<AuthorityGrantActorAuthorization> AuthorizeAsync(AuthorityGrantActorAuthorizationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastEvaluatedAtUtc = request.EvaluatedAtUtc;
            return Handler?.Invoke(request, cancellationToken) ?? Task.FromResult(Decision(request));
        }
    }

    private sealed class ProfileSourceStub : IAuthorityGrantProfileSource
    {
        internal int Calls { get; private set; }
        internal AuthorityGrantDependencyStatus Status { get; set; } = AuthorityGrantDependencyStatus.Active;
        internal EmbodySense.Core.Common.Authority.Models.AuthorityProfile? Profile { get; set; }
        internal Exception? Exception { get; set; }

        public Task<AuthorityGrantProfileResolution> ResolveAsync(AuthorityGrantProfilePin? pin, DateTimeOffset evaluatedAtUtc, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Exception is not null)
            {
                throw Exception;
            }

            var profile = Profile ?? AuthorityGrantApplicationTestFixture.Profile();
            return Task.FromResult(new AuthorityGrantProfileResolution(Status, pin, profile, AuthorityGrantApplicationTestFixture.Hash64('c')));
        }
    }

    private sealed class RoleSourceStub : IAuthorityGrantRoleSource
    {
        internal int Calls { get; private set; }
        internal AuthorityGrantDependencyStatus Status { get; set; } = AuthorityGrantDependencyStatus.Active;
        internal Common.ContextualRoles.Models.ContextualRoleRevision? Role { get; set; }
        internal Exception? Exception { get; set; }

        public Task<AuthorityGrantRoleResolution> ResolveAsync(AuthorityGrantRolePin? pin, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Exception is not null)
            {
                throw Exception;
            }

            var role = Role ?? AuthorityGrantApplicationTestFixture.Role();
            return Task.FromResult(new AuthorityGrantRoleResolution(Status, pin, role, AuthorityGrantApplicationTestFixture.RoleLifecycle(role), AuthorityGrantApplicationTestFixture.Hash64('d')));
        }
    }

    private sealed class PublicationSourceStub : IGovernedLoopPublishedRevisionSource
    {
        internal GovernedLoopPublishedRevisionResolutionStatus Status { get; set; } = GovernedLoopPublishedRevisionResolutionStatus.Active;
        internal Exception? Exception { get; set; }

        public Task<GovernedLoopPublishedRevisionResolution> ResolveAsync(GovernedLoopRevisionPublicationPin? pin, CancellationToken cancellationToken = default)
        {
            if (Exception is not null)
            {
                throw Exception;
            }

            var resolution = AuthorityGrantApplicationTestFixture.PublishedLoop(pin);
            return Task.FromResult(resolution with { Status = Status });
        }
    }

    private sealed class LoopBindingSourceStub : IGovernedLoopGrantBindingSource
    {
        internal AuthorityGrantDependencyStatus Status { get; set; } = AuthorityGrantDependencyStatus.Active;
        internal IReadOnlyList<string>? CapabilityIds { get; set; }
        internal bool SubstituteOwner { get; set; }
        internal Exception? Exception { get; set; }

        public Task<GovernedLoopGrantBindingResolution> ResolveAsync(GovernedLoopRevisionPublicationPin? pin, CancellationToken cancellationToken = default)
        {
            if (Exception is not null)
            {
                throw Exception;
            }

            var owner = SubstituteOwner ? new Common.ContextualRoles.Models.ContextualRoleRevisionIdentity("other-role", 1) : AuthorityGrantApplicationTestFixture.Role().Identity;
            return Task.FromResult(new GovernedLoopGrantBindingResolution(
                Status,
                pin,
                owner,
                CapabilityIds ?? [AuthorityGrantApplicationTestFixture.Capability().Id.Value],
                AuthorityGrantApplicationTestFixture.Hash64('e')));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Value { get; set; } = now;
        internal Exception? Exception { get; set; }

        public override DateTimeOffset GetUtcNow() => Exception is null ? Value : throw Exception;
    }

    private sealed class SequenceTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private readonly Queue<DateTimeOffset> _values = new(values);

        internal int Calls { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            Calls++;
            return _values.Count > 0 ? _values.Dequeue() : throw new InvalidOperationException("No trusted test instant remains.");
        }
    }

    private sealed class ThrowBeforeTransaction : ICapabilityAuthorityTransaction
    {
        public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
            => Task.FromException<TResult>(new IOException("fence unavailable"));

        public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
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
}
