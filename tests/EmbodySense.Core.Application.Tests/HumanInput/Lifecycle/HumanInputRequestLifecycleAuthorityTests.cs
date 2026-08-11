using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecycleAuthorityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Unavailable_ambiguous_or_throwing_grant_dependency_never_projects_observed_state(int scenario)
    {
        var (harness, request) = await SeededHarnessAsync();
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Resolver.Handler = scenario switch
        {
            0 => (_, _) => throw new InvalidOperationException("Dependency failed."),
            1 => (reference, _) => UnavailableResolution(AuthorityGrantResolutionStatus.Unavailable, reference),
            _ => (reference, _) => UnavailableResolution(AuthorityGrantResolutionStatus.Ambiguous, reference),
        };
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            $"unavailable-grant-{scenario}",
            request.RequestId);

        var result = await harness.Service.MutateAsync(command);

        AssertAuthorityFailure(result, HumanInputRequestLifecycleMutationStatus.GrantUnavailable);
        Assert.Single(harness.Resolver.Calls);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.Commits);
    }

    [Theory]
    [InlineData(AuthorityGrantLifecycleStatus.Suspended, 0)]
    [InlineData(AuthorityGrantLifecycleStatus.Revoked, 0)]
    [InlineData(AuthorityGrantLifecycleStatus.Expired, 0)]
    [InlineData(AuthorityGrantLifecycleStatus.Active, 1)]
    [InlineData(AuthorityGrantLifecycleStatus.Active, 2)]
    public async Task Forged_active_resolution_rejects_closed_or_out_of_time_grant(
        AuthorityGrantLifecycleStatus status,
        int boundaryScenario)
    {
        var (harness, request) = await SeededHarnessAsync();
        var boundary = boundaryScenario switch
        {
            1 => new AuthorityGrantBoundary(
                HumanInputRequestLifecycleTestData.Now.AddTicks(1),
                HumanInputRequestLifecycleTestData.Now.AddHours(1),
                AuthorityGrantCompletionConstraintKind.None),
            2 => new AuthorityGrantBoundary(
                HumanInputRequestLifecycleTestData.Now.AddHours(-1),
                HumanInputRequestLifecycleTestData.Now,
                AuthorityGrantCompletionConstraintKind.None),
            _ => AuthorityGrantApplicationTestFixture.Boundary(),
        };
        var hostile = HumanInputRequestLifecycleTestData.Grant(status, boundary);
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Resolver.Handler = (_, _) => HumanInputRequestLifecycleTestData.ActiveResolution(hostile);
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Remind,
            $"forged-active-{status.ToString().ToLowerInvariant()}-{boundaryScenario}",
            request.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(hostile),
            expected: harness.Store.Snapshot(request.RequestId)!.Head);

        var result = await harness.Service.MutateAsync(command);

        AssertAuthorityFailure(result, HumanInputRequestLifecycleMutationStatus.GrantUnavailable);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.Commits);
    }

    [Fact]
    public async Task Exact_active_resolution_rejects_reference_ceiling_dependency_and_time_malformations()
    {
        var mutations = new Func<AuthorityGrantResolution, AuthorityGrantResolution>[]
        {
            resolution => resolution with
            {
                RequestedReference = new AuthorityGrantReference(
                    AuthorityGrantApplicationTestFixture.GrantId("different-grant"),
                    AuthorityGrantApplicationTestFixture.GrantRevision(1),
                    "sha256:" + HumanInputRequestLifecycleTestData.Hash('9')),
            },
            resolution => resolution with { Grant = null },
            resolution => resolution with { EffectiveCeiling = AuthorityGrantApplicationTestFixture.Ceiling(maxTargets: 1) },
            resolution => resolution with { DependencyEvidenceHash = "invalid" },
            resolution => resolution with
            {
                EvaluatedAtUtc = new DateTimeOffset(
                    HumanInputRequestLifecycleTestData.Now.DateTime,
                    TimeSpan.FromHours(1)),
            },
            resolution => resolution with { EvaluatedAtUtc = resolution.Grant!.RecordedAtUtc.AddTicks(-1) },
        };

        for (var index = 0; index < mutations.Length; index++)
        {
            var (harness, request) = await SeededHarnessAsync();
            HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
            var exact = HumanInputRequestLifecycleTestData.ActiveResolution(harness.Grant);
            harness.Resolver.Handler = (_, _) => mutations[index](exact);
            var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
                harness,
                HumanInputRequestLifecycleOperationKind.Remind,
                $"malformed-grant-resolution-{index}",
                request.RequestId);

            var result = await harness.Service.MutateAsync(command);

            AssertAuthorityFailure(result, HumanInputRequestLifecycleMutationStatus.GrantUnavailable);
            Assert.Empty(harness.Authorizer.Requests);
            Assert.Empty(harness.Store.Commits);
        }
    }

    [Fact]
    public async Task Actor_decision_must_exactly_echo_operation_hash_workspace_time_actor_and_evidence()
    {
        var mutations = new Func<HumanInputRequestLifecycleActorAuthorization, HumanInputRequestLifecycleActorAuthorization>[]
        {
            decision => decision with { Status = HumanInputRequestLifecycleActorAuthorizationStatus.Unknown },
            decision => decision with { OperationId = "different-operation" },
            decision => decision with { RequestHash = HumanInputRequestLifecycleTestData.Hash('f') },
            decision => decision with { WorkspaceId = "different-workspace" },
            decision => decision with { EvaluatedAtUtc = decision.EvaluatedAtUtc.AddTicks(1) },
            decision => decision with
            {
                EvaluatedAtUtc = new DateTimeOffset(decision.EvaluatedAtUtc.DateTime, TimeSpan.FromHours(1)),
            },
            decision => decision with { ActorId = null },
            decision => decision with { AuthorityEvidenceHash = "invalid" },
        };

        for (var index = 0; index < mutations.Length; index++)
        {
            var (harness, request) = await SeededHarnessAsync();
            HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
            harness.Authorizer.Handler = (authorizationRequest, _) => mutations[index](Authorized(authorizationRequest));
            var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
                harness,
                HumanInputRequestLifecycleOperationKind.Remind,
                $"malformed-actor-decision-{index}",
                request.RequestId);

            var result = await harness.Service.MutateAsync(command);

            AssertAuthorityFailure(result, HumanInputRequestLifecycleMutationStatus.Unavailable);
            Assert.Single(harness.Authorizer.Requests);
            Assert.Empty(harness.Store.Commits);
        }
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleActorAuthorizationStatus.Denied, HumanInputRequestLifecycleMutationStatus.Denied)]
    [InlineData(HumanInputRequestLifecycleActorAuthorizationStatus.Unavailable, HumanInputRequestLifecycleMutationStatus.Unavailable)]
    public async Task Non_authorized_actor_postures_never_project_observed_state(
        HumanInputRequestLifecycleActorAuthorizationStatus authorizationStatus,
        HumanInputRequestLifecycleMutationStatus expectedStatus)
    {
        var (harness, request) = await SeededHarnessAsync();
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Authorizer.Handler = (authorizationRequest, _) => Authorized(authorizationRequest) with
        {
            Status = authorizationStatus,
            ActorId = authorizationStatus == HumanInputRequestLifecycleActorAuthorizationStatus.Unavailable
                ? null
                : AuthorityGrantApplicationTestFixture.Actor("human-input-actor"),
            AuthorityEvidenceHash = authorizationStatus == HumanInputRequestLifecycleActorAuthorizationStatus.Unavailable
                ? string.Empty
                : HumanInputRequestLifecycleTestData.Hash('a'),
        };
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            $"actor-{authorizationStatus.ToString().ToLowerInvariant()}",
            request.RequestId);

        var result = await harness.Service.MutateAsync(command);

        AssertAuthorityFailure(result, expectedStatus);
        Assert.Empty(harness.Store.Commits);
    }

    [Fact]
    public async Task Throwing_actor_dependency_fails_closed_without_projection()
    {
        var (harness, request) = await SeededHarnessAsync();
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Authorizer.Handler = (_, _) => throw new InvalidOperationException("Actor source failed.");
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            "throwing-actor-source",
            request.RequestId);

        var result = await harness.Service.MutateAsync(command);

        AssertAuthorityFailure(result, HumanInputRequestLifecycleMutationStatus.Unavailable);
        Assert.Empty(harness.Store.Commits);
    }

    [Fact]
    public async Task Candidate_workspace_mismatch_is_rejected_before_grant_or_actor_without_projection()
    {
        var (harness, request) = await SeededHarnessAsync();
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var candidate = HumanInputRequestHash.Apply(
            HumanInputRequestLifecycleTransitionTestSupport.RerouteCandidate(request) with
            {
                Binding = request.Binding with { WorkspaceId = "different-workspace" },
                RequestHash = string.Empty,
            });
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Reroute,
            "reroute-different-workspace",
            request.RequestId,
            candidate);

        var result = await harness.Service.MutateAsync(command);

        AssertAuthorityFailure(result, HumanInputRequestLifecycleMutationStatus.Invalid);
        Assert.Contains(
            result.ValidationErrors,
            error => error.Code == HumanInputRequestLifecycleMutationValidationErrorCode.InvalidOperationShape);
        Assert.Empty(harness.Store.MutationReads);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.Commits);
    }

    [Fact]
    public async Task Throwing_cleanup_clock_fails_closed_without_grant_projection_or_commit()
    {
        var (harness, request) = await SeededHarnessAsync();
        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        harness.Time.ThrowOnRead = true;
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Cancel,
            "cancel-with-throwing-clock",
            request.RequestId);

        var result = await harness.Service.MutateAsync(command);

        AssertAuthorityFailure(result, HumanInputRequestLifecycleMutationStatus.Unavailable);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.Commits);
    }

    private static HumanInputRequestLifecycleActorAuthorization Authorized(
        HumanInputRequestLifecycleActorAuthorizationRequest request)
        => new(
            HumanInputRequestLifecycleActorAuthorizationStatus.Authorized,
            request.Command.OperationId,
            request.RequestHash,
            request.WorkspaceId,
            request.EvaluatedAtUtc,
            AuthorityGrantApplicationTestFixture.Actor("human-input-actor"),
            HumanInputRequestLifecycleTestData.Hash('a'));

    private static AuthorityGrantResolution UnavailableResolution(
        AuthorityGrantResolutionStatus status,
        AuthorityGrantReference? reference)
        => new(
            status,
            reference,
            null,
            new AuthorityCeiling([], [], 0, 0, false, false, false),
            string.Empty,
            default);

    private static async Task<(HumanInputRequestLifecycleHarness Harness, EmbodySense.Core.Common.HumanInput.Models.HumanInputRequest Request)> SeededHarnessAsync()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request);
        return (harness, request);
    }

    private static void AssertAuthorityFailure(
        HumanInputRequestLifecycleMutationResult result,
        HumanInputRequestLifecycleMutationStatus expectedStatus)
    {
        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.Proof);
        Assert.Null(result.Primary);
        Assert.Null(result.Related);
        Assert.Null(result.DeliveryOpportunity);
    }
}
