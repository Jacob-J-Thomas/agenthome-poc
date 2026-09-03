using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.HumanInput.Catalog.Models;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Persistence.Tests.HumanInput.Requests;
using EmbodySense.Core.Startup.HumanInput;
using EmbodySense.Core.Startup.HumanInput.Models;

namespace EmbodySense.Core.Startup.Tests.HumanInput;

public sealed class HumanInputLifecycleCandidatePreparationTests
{
    [Theory]
    [InlineData(HumanInputResponsePolicyKind.FirstValid)]
    [InlineData(HumanInputResponsePolicyKind.Quorum)]
    [InlineData(HumanInputResponsePolicyKind.NamedRoles)]
    [InlineData(HumanInputResponsePolicyKind.Merge)]
    [InlineData(HumanInputResponsePolicyKind.ManualSelection)]
    public async Task Reroute_options_are_opaque_and_policy_valid_for_every_supported_policy(HumanInputResponsePolicyKind policyKind)
    {
        var fixture = CreateFixture(policyKind);
        var input = RerouteInput(fixture.Request, fixture.Clock.GetUtcNow().AddMinutes(5));

        var result = await fixture.Preparer.PrepareRerouteAsync(input);

        Assert.Equal(HumanInputSupersedePreparationStatus.Ready, result.Status);
        Assert.NotEmpty(result.Options);
        Assert.InRange(result.Options.Count, 1, HumanInputLifecycleCandidateLimits.MaxRerouteOptions);
        foreach (var option in result.Options)
        {
            Assert.DoesNotContain("user-", option.Label, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("role-", option.Label, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(input.CandidateExpiresAtUtc, option.ExpiresAtUtc);
            Assert.True(fixture.Registry.TryResolve(HumanInputRequestLifecycleOperationKind.Reroute, option.CandidateKey, fixture.Request.Binding.WorkspaceId, "user-one", input.OperationId, input.RequestId, input.ExpectedLifecycleVersion, input.ExpectedRequest!.RequestVersionId, input.ExpectedRequest.RequestHash, fixture.Clock.GetUtcNow(), out var resolution));
            Assert.NotNull(resolution);
            Assert.Equal(fixture.Request.RequestId, resolution!.CandidateRequest.RequestId);
            Assert.NotEqual(fixture.Request.RequestHash, resolution.CandidateRequest.RequestHash);
            Assert.Equal(fixture.Request.Binding, resolution.CandidateRequest.Binding);
            Assert.Equal(fixture.Request.ResponsePolicy.Kind, resolution.CandidateRequest.ResponsePolicy.Kind);
            Assert.Equal(fixture.Request.ResponsePolicy.RequiredResponseCount, resolution.CandidateRequest.ResponsePolicy.RequiredResponseCount);
            Assert.Equal(fixture.Request.ResponsePolicy.OrderedRoleIds, resolution.CandidateRequest.ResponsePolicy.OrderedRoleIds);
            Assert.True(HumanInputValidator.ValidateRequest(resolution.CandidateRequest).IsValid);
        }
    }

    [Fact]
    public async Task Reroute_prepare_replays_exact_keys_and_conflicts_on_changed_intent()
    {
        var fixture = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        var input = RerouteInput(fixture.Request, fixture.Clock.GetUtcNow().AddMinutes(5));

        var first = await fixture.Preparer.PrepareRerouteAsync(input);
        var replay = await fixture.Preparer.PrepareRerouteAsync(input);

        Assert.Equal(HumanInputSupersedePreparationStatus.Ready, first.Status);
        Assert.Equal(first.Options, replay.Options);
        Assert.Equal(first.Options.Select(option => option.CandidateKey), replay.Options.Select(option => option.CandidateKey));

        var changedExpiry = await fixture.Preparer.PrepareRerouteAsync(input with { CandidateExpiresAtUtc = fixture.Clock.GetUtcNow().AddMinutes(6) });

        Assert.Equal(HumanInputSupersedePreparationStatus.Conflict, changedExpiry.Status);
        Assert.Null(changedExpiry.ExpiresAtUtc);
    }

    [Fact]
    public async Task Reroute_prepare_replays_an_unexpired_candidate_in_its_final_minute()
    {
        var fixture = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        var input = RerouteInput(fixture.Request, fixture.Clock.GetUtcNow().AddMinutes(2));

        var first = await fixture.Preparer.PrepareRerouteAsync(input);
        fixture.Clock.SetUtcNow(fixture.Clock.GetUtcNow().AddMinutes(1));
        var replay = await fixture.Preparer.PrepareRerouteAsync(input);

        Assert.Equal(HumanInputSupersedePreparationStatus.Ready, first.Status);
        Assert.Equal(HumanInputSupersedePreparationStatus.Ready, replay.Status);
        Assert.Equal(first.Options, replay.Options);
    }

    [Fact]
    public async Task Candidate_expiry_requires_canonical_utc_and_inclusive_one_to_fifteen_minute_bounds()
    {
        var exactMinimum = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        var minimum = await exactMinimum.Preparer.PrepareRerouteAsync(RerouteInput(exactMinimum.Request, exactMinimum.Clock.GetUtcNow().Add(HumanInputLifecycleCandidateLimits.MinCandidateLifetime)));
        Assert.Equal(HumanInputSupersedePreparationStatus.Ready, minimum.Status);

        var exactMaximum = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        var maximum = await exactMaximum.Preparer.PrepareRerouteAsync(RerouteInput(exactMaximum.Request, exactMaximum.Clock.GetUtcNow().Add(HumanInputLifecycleCandidateLimits.MaxCandidateLifetime)));
        Assert.Equal(HumanInputSupersedePreparationStatus.Ready, maximum.Status);

        var belowMinimum = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        var below = await belowMinimum.Preparer.PrepareRerouteAsync(RerouteInput(belowMinimum.Request, belowMinimum.Clock.GetUtcNow().Add(HumanInputLifecycleCandidateLimits.MinCandidateLifetime).AddTicks(-1)));
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, below.Status);

        var aboveMaximum = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        var above = await aboveMaximum.Preparer.PrepareRerouteAsync(RerouteInput(aboveMaximum.Request, aboveMaximum.Clock.GetUtcNow().Add(HumanInputLifecycleCandidateLimits.MaxCandidateLifetime).AddTicks(1)));
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, above.Status);

        var nonUtc = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        var offsetDateTime = DateTime.SpecifyKind(nonUtc.Clock.GetUtcNow().DateTime.AddMinutes(5), DateTimeKind.Unspecified);
        var offset = await nonUtc.Preparer.PrepareRerouteAsync(RerouteInput(nonUtc.Request, new DateTimeOffset(offsetDateTime, TimeSpan.FromHours(1))));
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, offset.Status);
    }

    [Fact]
    public async Task Amend_replays_deterministically_and_preserves_canonical_binding_routing_policy_and_continuation()
    {
        var fixture = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        var input = AmendInput(fixture.Request, fixture.Clock.GetUtcNow().AddMinutes(5), fixture.Clock.GetUtcNow().AddMinutes(30));

        var first = await fixture.Preparer.PrepareAmendAsync(input);
        var replay = await fixture.Preparer.PrepareAmendAsync(input);

        Assert.Equal(HumanInputSupersedePreparationStatus.Ready, first.Status);
        Assert.Equal(first.CandidateKey, replay.CandidateKey);
        Assert.True(((IHumanInputSupersedeCandidateRegistry)fixture.Registry).TryResolve(HumanInputRequestLifecycleOperationKind.Amend, first.CandidateKey!, fixture.Request.Binding.WorkspaceId, "user-one", input.OperationId, input.RequestId, input.ExpectedLifecycleVersion, input.ExpectedRequest!.RequestVersionId, input.ExpectedRequest.RequestHash, fixture.Clock.GetUtcNow(), out var resolution));
        Assert.NotNull(resolution);
        Assert.Equal(fixture.Request.RequestId, resolution!.CandidateRequest.RequestId);
        Assert.NotEqual(fixture.Request.RequestVersionId, resolution.CandidateRequest.RequestVersionId);
        Assert.NotEqual(fixture.Request.RequestHash, resolution.CandidateRequest.RequestHash);
        Assert.Equal(fixture.Request.Binding, resolution.CandidateRequest.Binding);
        Assert.Equal(fixture.Request.EligibleRespondents, resolution.CandidateRequest.EligibleRespondents);
        Assert.Equal(fixture.Request.ResponsePolicy.Kind, resolution.CandidateRequest.ResponsePolicy.Kind);
        Assert.Equal(fixture.Request.ResponsePolicy.RequiredResponseCount, resolution.CandidateRequest.ResponsePolicy.RequiredResponseCount);
        Assert.Equal(fixture.Request.ResponsePolicy.OrderedRoleIds, resolution.CandidateRequest.ResponsePolicy.OrderedRoleIds);
        Assert.Equal(fixture.Request.ContinuationBinding, resolution.CandidateRequest.ContinuationBinding);
        Assert.Equal(fixture.Request.Timing.RequestedAtUtc, resolution.CandidateRequest.Timing.RequestedAtUtc);
        Assert.Equal(input.Purpose, resolution.CandidateRequest.Purpose);
        Assert.Equal(input.Prompt, resolution.CandidateRequest.Prompt);
        Assert.True(HumanInputValidator.ValidateRequest(resolution.CandidateRequest).IsValid);

        var changedRequestExpiry = await fixture.Preparer.PrepareAmendAsync(input with { RequestExpiresAtUtc = fixture.Clock.GetUtcNow().AddMinutes(31) });
        Assert.Equal(HumanInputSupersedePreparationStatus.Conflict, changedRequestExpiry.Status);
    }

    [Fact]
    public async Task Lifecycle_version_at_contract_limit_projects_limit_exceeded()
    {
        var fixture = CreateFixture(HumanInputResponsePolicyKind.FirstValid, HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion);
        var result = await fixture.Preparer.PrepareAmendAsync(AmendInput(fixture.Request, fixture.Clock.GetUtcNow().AddMinutes(5), fixture.Clock.GetUtcNow().AddMinutes(30)) with { ExpectedLifecycleVersion = HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion });

        Assert.Equal(HumanInputSupersedePreparationStatus.LimitExceeded, result.Status);
        Assert.Null(result.CandidateKey);
    }

    [Fact]
    public async Task Candidate_preparation_fails_closed_when_the_trusted_clock_is_unavailable()
    {
        var source = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        var clock = new HumanInputThrowingTimeProvider();
        var preparer = new HumanInputSupersedeCandidatePreparer(source.Catalog, source.GrantResolver, new HumanInputSupersedeCandidateRegistry(clock), source.Request.Binding.WorkspaceId, "user-one", clock);

        var reroute = await preparer.PrepareRerouteAsync(RerouteInput(source.Request, source.Clock.GetUtcNow().AddMinutes(5)));
        var amend = await preparer.PrepareAmendAsync(AmendInput(source.Request, source.Clock.GetUtcNow().AddMinutes(5), source.Clock.GetUtcNow().AddMinutes(30)));

        Assert.Equal(HumanInputSupersedePreparationStatus.Unavailable, reroute.Status);
        Assert.Equal(HumanInputSupersedePreparationStatus.Unavailable, amend.Status);
        Assert.Empty(reroute.Options);
        Assert.Null(amend.CandidateKey);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleContractLimits.MaxRequestVersionsPerRequest)]
    [InlineData(HumanInputRequestLifecycleContractLimits.MaxRequestVersionsPerRequest + 1)]
    public async Task Request_version_limit_projects_limit_exceeded_at_and_above_the_exact_bound(int versionCount)
    {
        var fixture = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        var versions = new List<HumanInputRequest> { fixture.Request };
        for (var index = 1; index < versionCount; index++)
        {
            versions.Add(HumanInputRequestHash.Apply(fixture.Request with { RequestVersionId = $"version-extra-{index}", RequestHash = string.Empty }));
        }

        var lifecycle = new HumanInputRequestLifecycleStoreSnapshot(fixture.Lifecycle.Head, versions, fixture.Lifecycle.Operations, fixture.Lifecycle.AnswerOperation);
        fixture.Catalog.ReadResponse = new HumanInputRequestCatalogReadResult(HumanInputRequestCatalogReadStatus.Ready, 1, new HumanInputRequestCatalogEntry(lifecycle, null!));
        var result = await fixture.Preparer.PrepareAmendAsync(AmendInput(fixture.Request, fixture.Clock.GetUtcNow().AddMinutes(5), fixture.Clock.GetUtcNow().AddMinutes(30)));

        Assert.Equal(HumanInputSupersedePreparationStatus.LimitExceeded, result.Status);
        Assert.Null(result.CandidateKey);
    }

    private static Fixture CreateFixture(HumanInputResponsePolicyKind policyKind, long lifecycleVersion = 1)
    {
        var policyName = policyKind.ToString().ToLowerInvariant();
        var baseMutation = HumanInputRequestStoreTestData.CreateMutation($"request-candidate-{policyName}", $"version-candidate-{policyName}", $"create-candidate-{policyName}");
        var respondents = new[]
        {
            new HumanInputEligibleRespondent("user-one", "role-one", "route-one"),
            new HumanInputEligibleRespondent("user-two", "role-two", "route-two"),
            new HumanInputEligibleRespondent("user-three", "role-three", "route-three")
        };
        var policy = policyKind switch
        {
            HumanInputResponsePolicyKind.FirstValid => new HumanInputResponsePolicy(policyKind, null, null),
            HumanInputResponsePolicyKind.Quorum => new HumanInputResponsePolicy(policyKind, 2, null),
            HumanInputResponsePolicyKind.NamedRoles => new HumanInputResponsePolicy(policyKind, null, ["role-one", "role-two"]),
            HumanInputResponsePolicyKind.Merge => new HumanInputResponsePolicy(policyKind, 2, ["role-one", "role-two"]),
            HumanInputResponsePolicyKind.ManualSelection => new HumanInputResponsePolicy(policyKind, null, ["role-one"]),
            _ => throw new ArgumentOutOfRangeException(nameof(policyKind))
        };
        var request = HumanInputRequestHash.Apply(baseMutation.RequestToAppend! with { EligibleRespondents = respondents, ResponsePolicy = policy, RequestHash = string.Empty });
        var head = HumanInputRequestStoreTestData.Head(request, lifecycleVersion, HumanInputRequestLifecycleStatus.Pending, 0, null, null, baseMutation.Operation.OperationId, HumanInputRequestStoreTestData.Time);
        var evidence = HumanInputRequestStoreTestData.Evidence(HumanInputRequestLifecycleOperationKind.Create, request.RequestId, baseMutation.Operation.OperationId, request.RequestHash, HumanInputRequestStoreTestData.Time, null, head, request);
        var lifecycle = new HumanInputRequestLifecycleStoreSnapshot(head, [request], [evidence]);
        var catalog = new HumanInputSupersedeCandidatePreparerTestCatalog { ReadResponse = new HumanInputRequestCatalogReadResult(HumanInputRequestCatalogReadStatus.Ready, 1, new HumanInputRequestCatalogEntry(lifecycle, null!)) };
        var clock = new HumanInputFixedTimeProvider(HumanInputRequestStoreTestData.Time.AddMinutes(30));
        var grant = baseMutation.Operation.GrantReference!;
        var resolver = new HumanInputSupersedeCandidatePreparerTestGrantResolver(new AuthorityGrantResolution(AuthorityGrantResolutionStatus.Active, grant, null!, new AuthorityCeiling([], [], 0, CapabilitySideEffectClass.None, false, false, false), "grant-evidence", HumanInputRequestStoreTestData.Time));
        var registry = new HumanInputSupersedeCandidateRegistry(clock);
        var preparer = new HumanInputSupersedeCandidatePreparer(catalog, resolver, registry, request.Binding.WorkspaceId, "user-one", clock);
        return new Fixture(preparer, registry, request, clock, catalog, resolver, lifecycle);
    }

    private static HumanInputReroutePreparationInput RerouteInput(HumanInputRequest request, DateTimeOffset candidateExpiresAtUtc)
        => new("reroute-operation", request.RequestId, new HumanInputSurfaceRequestReference(request.RequestId, request.RequestVersionId, request.RequestHash), 1, HumanInputRequestLifecycleStatus.Pending.ToString(), candidateExpiresAtUtc);

    private static HumanInputAmendPreparationInput AmendInput(HumanInputRequest request, DateTimeOffset candidateExpiresAtUtc, DateTimeOffset requestExpiresAtUtc)
        => new("amend-operation", request.RequestId, new HumanInputSurfaceRequestReference(request.RequestId, request.RequestVersionId, request.RequestHash), 1, HumanInputRequestLifecycleStatus.Pending.ToString(), "Amended purpose", "Amended prompt", request.PrivacyClass.ToString(), requestExpiresAtUtc, candidateExpiresAtUtc);

    private sealed record Fixture(HumanInputSupersedeCandidatePreparer Preparer, HumanInputSupersedeCandidateRegistry Registry, HumanInputRequest Request, HumanInputFixedTimeProvider Clock, HumanInputSupersedeCandidatePreparerTestCatalog Catalog, HumanInputSupersedeCandidatePreparerTestGrantResolver GrantResolver, HumanInputRequestLifecycleStoreSnapshot Lifecycle);
}
