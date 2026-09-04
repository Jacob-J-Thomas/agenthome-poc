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

    [Fact]
    public async Task Reroute_and_amend_map_catalog_and_expected_state_failures_without_payloads()
    {
        var fixture = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        var rerouteInput = RerouteInput(fixture.Request, fixture.Clock.GetUtcNow().AddMinutes(5));
        var amendInput = AmendInput(fixture.Request, fixture.Clock.GetUtcNow().AddMinutes(5), fixture.Clock.GetUtcNow().AddMinutes(30));
        var catalogEntry = fixture.Catalog.ReadResponse!.Entry;

        foreach (var (status, expected) in new[]
        {
            (HumanInputRequestCatalogReadStatus.NotFound, HumanInputSupersedePreparationStatus.NotFound),
            (HumanInputRequestCatalogReadStatus.Invalid, HumanInputSupersedePreparationStatus.Invalid),
            (HumanInputRequestCatalogReadStatus.Unavailable, HumanInputSupersedePreparationStatus.Unavailable),
            (HumanInputRequestCatalogReadStatus.Ambiguous, HumanInputSupersedePreparationStatus.Ambiguous),
            (HumanInputRequestCatalogReadStatus.Unknown, HumanInputSupersedePreparationStatus.Ambiguous)
        })
        {
            fixture.Catalog.ReadResponse = new HumanInputRequestCatalogReadResult(status, 1, null);
            var suffix = status.ToString().ToLowerInvariant();
            var reroute = await fixture.Preparer.PrepareRerouteAsync(rerouteInput with { OperationId = $"reroute-catalog-{suffix}" });
            var amend = await fixture.Preparer.PrepareAmendAsync(amendInput with { OperationId = $"amend-catalog-{suffix}" });
            Assert.Equal(expected, reroute.Status);
            Assert.Equal(expected, amend.Status);
            Assert.Empty(reroute.Options);
            Assert.Null(amend.CandidateKey);
        }

        fixture.Catalog.ReadResponse = null!;
        Assert.Equal(HumanInputSupersedePreparationStatus.NotFound, (await fixture.Preparer.PrepareRerouteAsync(rerouteInput)).Status);

        fixture.Catalog.ReadResponse = new HumanInputRequestCatalogReadResult(HumanInputRequestCatalogReadStatus.Ready, 1, null);
        var emptyEntry = await fixture.Preparer.PrepareAmendAsync(amendInput);
        Assert.Equal(HumanInputSupersedePreparationStatus.Ambiguous, emptyEntry.Status);

        var conflictingHead = fixture.Lifecycle.Head! with { LifecycleVersion = fixture.Lifecycle.Head.LifecycleVersion + 1 };
        fixture.Catalog.ReadResponse = new HumanInputRequestCatalogReadResult(
            HumanInputRequestCatalogReadStatus.Ready,
            1,
            new HumanInputRequestCatalogEntry(new HumanInputRequestLifecycleStoreSnapshot(conflictingHead, [fixture.Request], fixture.Lifecycle.Operations), null!));
        var conflict = await fixture.Preparer.PrepareRerouteAsync(rerouteInput);
        Assert.Equal(HumanInputSupersedePreparationStatus.Conflict, conflict.Status);
        Assert.Empty(conflict.Options);

        fixture.Catalog.ReadResponse = new HumanInputRequestCatalogReadResult(HumanInputRequestCatalogReadStatus.Ready, 1, catalogEntry);
        var invalidExpected = await fixture.Preparer.PrepareAmendAsync(amendInput with { ExpectedLifecycleStatus = "Completed" });
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, invalidExpected.Status);
        Assert.Null(invalidExpected.CandidateKey);
    }

    [Fact]
    public async Task Reroute_rejects_ambiguous_request_or_grant_evidence_before_candidate_generation()
    {
        var fixture = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        var input = RerouteInput(fixture.Request, fixture.Clock.GetUtcNow().AddMinutes(5));

        var duplicateVersions = new HumanInputRequestLifecycleStoreSnapshot(fixture.Lifecycle.Head, [fixture.Request, fixture.Request], fixture.Lifecycle.Operations);
        fixture.Catalog.ReadResponse = new HumanInputRequestCatalogReadResult(HumanInputRequestCatalogReadStatus.Ready, 1, new HumanInputRequestCatalogEntry(duplicateVersions, null!));
        var duplicate = await fixture.Preparer.PrepareRerouteAsync(input);
        Assert.Equal(HumanInputSupersedePreparationStatus.Ambiguous, duplicate.Status);

        var malformedVersion = fixture.Request with { RequestHash = "invalid" };
        var malformedVersions = new HumanInputRequestLifecycleStoreSnapshot(fixture.Lifecycle.Head, [malformedVersion], fixture.Lifecycle.Operations);
        fixture.Catalog.ReadResponse = new HumanInputRequestCatalogReadResult(HumanInputRequestCatalogReadStatus.Ready, 1, new HumanInputRequestCatalogEntry(malformedVersions, null!));
        var malformed = await fixture.Preparer.PrepareRerouteAsync(input);
        Assert.Equal(HumanInputSupersedePreparationStatus.Ambiguous, malformed.Status);

        var missingGrant = new HumanInputRequestLifecycleStoreSnapshot(fixture.Lifecycle.Head, [fixture.Request], []);
        fixture.Catalog.ReadResponse = new HumanInputRequestCatalogReadResult(HumanInputRequestCatalogReadStatus.Ready, 1, new HumanInputRequestCatalogEntry(missingGrant, null!));
        var noGrant = await fixture.Preparer.PrepareRerouteAsync(input);
        Assert.Equal(HumanInputSupersedePreparationStatus.Ambiguous, noGrant.Status);
    }

    [Theory]
    [InlineData(HumanInputRouteIntentSourceStatus.Invalid, HumanInputSupersedePreparationStatus.Invalid)]
    [InlineData(HumanInputRouteIntentSourceStatus.Unavailable, HumanInputSupersedePreparationStatus.Unavailable)]
    [InlineData(HumanInputRouteIntentSourceStatus.Ambiguous, HumanInputSupersedePreparationStatus.Ambiguous)]
    [InlineData(HumanInputRouteIntentSourceStatus.Unknown, HumanInputSupersedePreparationStatus.Ambiguous)]
    public async Task Reroute_route_source_dispositions_fail_closed_without_candidate_options(HumanInputRouteIntentSourceStatus sourceStatus, HumanInputSupersedePreparationStatus expectedStatus)
    {
        var fixture = CreateFixture(HumanInputResponsePolicyKind.FirstValid, routeIntentSource: new HumanInputRouteIntentSourceTestDouble(HumanInputRouteIntentSourceResultFor(sourceStatus)));

        var result = await fixture.Preparer.PrepareRerouteAsync(RerouteInput(fixture.Request, fixture.Clock.GetUtcNow().AddMinutes(5)));

        Assert.Equal(expectedStatus, result.Status);
        Assert.Empty(result.Options);
    }

    [Fact]
    public async Task Reroute_rejects_changed_route_intent_digest_before_registry_publication()
    {
        var fixture = CreateFixture(HumanInputResponsePolicyKind.FirstValid, routeIntentSource: new HumanInputRouteIntentSourceTestDouble(
            new HumanInputRouteIntentSourceResult(
                HumanInputRouteIntentSourceStatus.Ready,
                HumanInputRouteIntentContract.ContractId,
                HumanInputRouteIntentContract.Version,
                [new HumanInputRouteExclusionIntent(0, new string('a', HumanInputLimits.Sha256HexCharacters))],
                new string('b', HumanInputLimits.Sha256HexCharacters))));

        var result = await fixture.Preparer.PrepareRerouteAsync(RerouteInput(fixture.Request, fixture.Clock.GetUtcNow().AddMinutes(5)));

        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, result.Status);
        Assert.Empty(result.Options);
    }

    [Theory]
    [InlineData(AuthorityGrantResolutionStatus.NotFound, HumanInputSupersedePreparationStatus.NotFound)]
    [InlineData(AuthorityGrantResolutionStatus.Invalid, HumanInputSupersedePreparationStatus.NotFound)]
    [InlineData(AuthorityGrantResolutionStatus.Revoked, HumanInputSupersedePreparationStatus.Denied)]
    [InlineData(AuthorityGrantResolutionStatus.ProfileUnavailable, HumanInputSupersedePreparationStatus.Unavailable)]
    [InlineData(AuthorityGrantResolutionStatus.RoleUnavailable, HumanInputSupersedePreparationStatus.Unavailable)]
    [InlineData(AuthorityGrantResolutionStatus.LoopUnavailable, HumanInputSupersedePreparationStatus.Unavailable)]
    [InlineData(AuthorityGrantResolutionStatus.Unavailable, HumanInputSupersedePreparationStatus.Unavailable)]
    [InlineData(AuthorityGrantResolutionStatus.Unknown, HumanInputSupersedePreparationStatus.Ambiguous)]
    [InlineData(AuthorityGrantResolutionStatus.Ambiguous, HumanInputSupersedePreparationStatus.Ambiguous)]
    public async Task Reroute_maps_non_active_grants_without_exposing_resolution_details(AuthorityGrantResolutionStatus grantStatus, HumanInputSupersedePreparationStatus expectedStatus)
    {
        var fixture = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        fixture.GrantResolver.Resolution = new AuthorityGrantResolution(grantStatus, fixture.GrantResolver.Resolution.RequestedReference, null!, new AuthorityCeiling([], [], 0, CapabilitySideEffectClass.None, false, false, false), string.Empty, fixture.Clock.GetUtcNow());

        var result = await fixture.Preparer.PrepareRerouteAsync(RerouteInput(fixture.Request, fixture.Clock.GetUtcNow().AddMinutes(5)) with { OperationId = $"reroute-grant-{grantStatus.ToString().ToLowerInvariant()}" });

        Assert.Equal(expectedStatus, result.Status);
        Assert.Empty(result.Options);
        Assert.Null(result.ExpiresAtUtc);
    }

    [Fact]
    public async Task Reroute_and_amend_map_an_active_grant_with_a_mismatched_reference_as_ambiguous()
    {
        var fixture = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        var grant = fixture.Lifecycle.Operations[0].GrantReference!;
        var mismatched = grant with { ContentHash = "sha256:" + new string('f', HumanInputLimits.Sha256HexCharacters) };
        fixture.GrantResolver.Resolution = fixture.GrantResolver.Resolution with { RequestedReference = mismatched };

        var reroute = await fixture.Preparer.PrepareRerouteAsync(RerouteInput(fixture.Request, fixture.Clock.GetUtcNow().AddMinutes(5)));
        var amend = await fixture.Preparer.PrepareAmendAsync(AmendInput(fixture.Request, fixture.Clock.GetUtcNow().AddMinutes(5), fixture.Clock.GetUtcNow().AddMinutes(30)));

        Assert.Equal(HumanInputSupersedePreparationStatus.Ambiguous, reroute.Status);
        Assert.Empty(reroute.Options);
        Assert.Equal(HumanInputSupersedePreparationStatus.Ambiguous, amend.Status);
        Assert.Null(amend.CandidateKey);
    }

    [Fact]
    public async Task Reroute_maps_grant_resolver_failure_and_no_valid_option_as_fail_closed()
    {
        var resolverFailure = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        resolverFailure.GrantResolver.ResolveException = new InvalidOperationException("private grant detail");
        var unavailable = await resolverFailure.Preparer.PrepareRerouteAsync(RerouteInput(resolverFailure.Request, resolverFailure.Clock.GetUtcNow().AddMinutes(5)));
        Assert.Equal(HumanInputSupersedePreparationStatus.Unavailable, unavailable.Status);
        Assert.Empty(unavailable.Options);

        var noOption = CreateFixture(HumanInputResponsePolicyKind.FirstValid, respondentCount: 1);
        var conflict = await noOption.Preparer.PrepareRerouteAsync(RerouteInput(noOption.Request, noOption.Clock.GetUtcNow().AddMinutes(5)));
        Assert.Equal(HumanInputSupersedePreparationStatus.Conflict, conflict.Status);
        Assert.Empty(conflict.Options);

        var atLimit = CreateFixture(HumanInputResponsePolicyKind.FirstValid, HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion);
        var limited = await atLimit.Preparer.PrepareRerouteAsync(RerouteInput(atLimit.Request, atLimit.Clock.GetUtcNow().AddMinutes(5)) with { ExpectedLifecycleVersion = HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion });
        Assert.Equal(HumanInputSupersedePreparationStatus.LimitExceeded, limited.Status);
        Assert.Empty(limited.Options);
    }

    [Fact]
    public async Task Candidate_preparation_fails_closed_for_midflight_dependencies_and_propagates_cancellation()
    {
        var catalogFailure = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        catalogFailure.Catalog.ReadException = new InvalidOperationException("catalog unavailable");
        var unavailableCatalog = await catalogFailure.Preparer.PrepareAmendAsync(AmendInput(catalogFailure.Request, catalogFailure.Clock.GetUtcNow().AddMinutes(5), catalogFailure.Clock.GetUtcNow().AddMinutes(30)));

        Assert.Equal(HumanInputSupersedePreparationStatus.Unavailable, unavailableCatalog.Status);
        Assert.Null(unavailableCatalog.CandidateKey);

        var catalogCancellation = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        catalogCancellation.Catalog.DelayReadUntilCancellation = true;
        catalogCancellation.Catalog.ReadEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = catalogCancellation.Preparer.PrepareAmendAsync(AmendInput(catalogCancellation.Request, catalogCancellation.Clock.GetUtcNow().AddMinutes(5), catalogCancellation.Clock.GetUtcNow().AddMinutes(30)), cancellation.Token);
            await catalogCancellation.Catalog.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }

        var routeFailureSource = new HumanInputRouteIntentSourceTestDouble(HumanInputRouteIntentSourceResultFor(HumanInputRouteIntentSourceStatus.Unavailable))
        {
            ResolveException = new InvalidOperationException("route source unavailable")
        };
        var routeFailure = CreateFixture(HumanInputResponsePolicyKind.FirstValid, routeIntentSource: routeFailureSource);
        var unavailableRoute = await routeFailure.Preparer.PrepareRerouteAsync(RerouteInput(routeFailure.Request, routeFailure.Clock.GetUtcNow().AddMinutes(5)));

        Assert.Equal(HumanInputSupersedePreparationStatus.Unavailable, unavailableRoute.Status);
        Assert.Empty(unavailableRoute.Options);

        var routeCancellationSource = new HumanInputRouteIntentSourceTestDouble(HumanInputRouteIntentSourceResultFor(HumanInputRouteIntentSourceStatus.Unavailable))
        {
            DelayResolveUntilCancellation = true,
            ResolveEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var routeCancellation = CreateFixture(HumanInputResponsePolicyKind.FirstValid, routeIntentSource: routeCancellationSource);
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = routeCancellation.Preparer.PrepareRerouteAsync(RerouteInput(routeCancellation.Request, routeCancellation.Clock.GetUtcNow().AddMinutes(5)), cancellation.Token);
            await routeCancellationSource.ResolveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }

        var grantCancellation = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        grantCancellation.GrantResolver.DelayResolveUntilCancellation = true;
        grantCancellation.GrantResolver.ResolveEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = grantCancellation.Preparer.PrepareAmendAsync(AmendInput(grantCancellation.Request, grantCancellation.Clock.GetUtcNow().AddMinutes(5), grantCancellation.Clock.GetUtcNow().AddMinutes(30)), cancellation.Token);
            await grantCancellation.GrantResolver.ResolveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
    }

    [Fact]
    public async Task Reroute_rejects_malformed_shape_and_cancellation_before_catalog_access()
    {
        var fixture = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        var input = RerouteInput(fixture.Request, fixture.Clock.GetUtcNow().AddMinutes(5));

        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, (await fixture.Preparer.PrepareRerouteAsync(null)).Status);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, (await fixture.Preparer.PrepareRerouteAsync(input with { ExpectedRequest = null })).Status);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, (await fixture.Preparer.PrepareRerouteAsync(input with { ExpectedRequest = input.ExpectedRequest! with { RequestHash = "invalid" } })).Status);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, (await fixture.Preparer.PrepareRerouteAsync(input with { CandidateExpiresAtUtc = fixture.Clock.GetUtcNow().AddMinutes(5).ToOffset(TimeSpan.FromHours(1)) })).Status);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Preparer.PrepareRerouteAsync(input, cancellation.Token));
    }

    [Fact]
    public async Task Amend_rejects_expiry_privacy_and_content_validation_failures()
    {
        var fixture = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        var now = fixture.Clock.GetUtcNow();
        var input = AmendInput(fixture.Request, now.AddMinutes(5), now.AddMinutes(30));

        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, (await fixture.Preparer.PrepareAmendAsync(input with { RequestExpiresAtUtc = fixture.Request.Timing.RequestedAtUtc.AddTicks(-1) })).Status);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, (await fixture.Preparer.PrepareAmendAsync(input with { RequestExpiresAtUtc = fixture.Request.Timing.RequestedAtUtc.Add(HumanInputLimits.MaxResponseWindow).AddTicks(1) })).Status);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, (await fixture.Preparer.PrepareAmendAsync(input with { PrivacyClass = "Unknown" })).Status);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, (await fixture.Preparer.PrepareAmendAsync(input with { Purpose = new string('x', HumanInputLimits.MaxPurposeCharacters + 1) })).Status);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, (await fixture.Preparer.PrepareAmendAsync(input with { CandidateExpiresAtUtc = now.AddMinutes(5).ToOffset(TimeSpan.FromHours(1)) })).Status);

        var sensitive = CreateFixture(HumanInputResponsePolicyKind.FirstValid, privacyClass: HumanInputPrivacyClass.Sensitive);
        var downgrade = AmendInput(sensitive.Request, sensitive.Clock.GetUtcNow().AddMinutes(5), sensitive.Clock.GetUtcNow().AddMinutes(30)) with { PrivacyClass = HumanInputPrivacyClass.Private.ToString() };
        var result = await sensitive.Preparer.PrepareAmendAsync(downgrade);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, result.Status);
        Assert.Null(result.CandidateKey);
    }

    [Fact]
    public async Task Amend_rejects_malformed_shape_and_cancellation_without_catalog_access()
    {
        var fixture = CreateFixture(HumanInputResponsePolicyKind.FirstValid);
        var input = AmendInput(fixture.Request, fixture.Clock.GetUtcNow().AddMinutes(5), fixture.Clock.GetUtcNow().AddMinutes(30));

        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, (await fixture.Preparer.PrepareAmendAsync(null)).Status);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, (await fixture.Preparer.PrepareAmendAsync(input with { ExpectedRequest = null })).Status);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, (await fixture.Preparer.PrepareAmendAsync(input with { RequestExpiresAtUtc = default })).Status);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Preparer.PrepareAmendAsync(input, cancellation.Token));
    }

    private static Fixture CreateFixture(HumanInputResponsePolicyKind policyKind, long lifecycleVersion = 1, int respondentCount = 3, HumanInputPrivacyClass privacyClass = HumanInputPrivacyClass.Private, IHumanInputRouteIntentSource? routeIntentSource = null)
    {
        var policyName = policyKind.ToString().ToLowerInvariant();
        var baseMutation = HumanInputRequestStoreTestData.CreateMutation($"request-candidate-{policyName}", $"version-candidate-{policyName}", $"create-candidate-{policyName}");
        var respondentNames = new[] { "one", "two", "three" };
        var respondents = respondentNames.Take(respondentCount)
            .Select(name => new HumanInputEligibleRespondent($"user-{name}", $"role-{name}", $"route-{name}"))
            .ToArray();
        var policy = policyKind switch
        {
            HumanInputResponsePolicyKind.FirstValid => new HumanInputResponsePolicy(policyKind, null, null),
            HumanInputResponsePolicyKind.Quorum => new HumanInputResponsePolicy(policyKind, 2, null),
            HumanInputResponsePolicyKind.NamedRoles => new HumanInputResponsePolicy(policyKind, null, ["role-one", "role-two"]),
            HumanInputResponsePolicyKind.Merge => new HumanInputResponsePolicy(policyKind, 2, ["role-one", "role-two"]),
            HumanInputResponsePolicyKind.ManualSelection => new HumanInputResponsePolicy(policyKind, null, ["role-one"]),
            _ => throw new ArgumentOutOfRangeException(nameof(policyKind))
        };
        var request = HumanInputRequestHash.Apply(baseMutation.RequestToAppend! with { EligibleRespondents = respondents, ResponsePolicy = policy, PrivacyClass = privacyClass, RequestHash = string.Empty });
        var head = HumanInputRequestStoreTestData.Head(request, lifecycleVersion, HumanInputRequestLifecycleStatus.Pending, 0, null, null, baseMutation.Operation.OperationId, HumanInputRequestStoreTestData.Time);
        var evidence = HumanInputRequestStoreTestData.Evidence(HumanInputRequestLifecycleOperationKind.Create, request.RequestId, baseMutation.Operation.OperationId, request.RequestHash, HumanInputRequestStoreTestData.Time, null, head, request);
        var lifecycle = new HumanInputRequestLifecycleStoreSnapshot(head, [request], [evidence]);
        var catalog = new HumanInputSupersedeCandidatePreparerTestCatalog { ReadResponse = new HumanInputRequestCatalogReadResult(HumanInputRequestCatalogReadStatus.Ready, 1, new HumanInputRequestCatalogEntry(lifecycle, null!)) };
        var clock = new HumanInputFixedTimeProvider(HumanInputRequestStoreTestData.Time.AddMinutes(30));
        var grant = baseMutation.Operation.GrantReference!;
        var resolver = new HumanInputSupersedeCandidatePreparerTestGrantResolver(new AuthorityGrantResolution(AuthorityGrantResolutionStatus.Active, grant, null!, new AuthorityCeiling([], [], 0, CapabilitySideEffectClass.None, false, false, false), "grant-evidence", HumanInputRequestStoreTestData.Time));
        var registry = new HumanInputSupersedeCandidateRegistry(clock);
        var preparer = new HumanInputSupersedeCandidatePreparer(catalog, resolver, registry, request.Binding.WorkspaceId, "user-one", clock, routeIntentSource);
        return new Fixture(preparer, registry, request, clock, catalog, resolver, lifecycle);
    }

    private static HumanInputRouteIntentSourceResult HumanInputRouteIntentSourceResultFor(HumanInputRouteIntentSourceStatus status)
        => status switch
        {
            HumanInputRouteIntentSourceStatus.Invalid => HumanInputRouteIntentSourceResultFactory.Invalid(),
            HumanInputRouteIntentSourceStatus.Unavailable => HumanInputRouteIntentSourceResultFactory.Unavailable(),
            HumanInputRouteIntentSourceStatus.Ambiguous => HumanInputRouteIntentSourceResultFactory.Ambiguous(),
            _ => new HumanInputRouteIntentSourceResult(HumanInputRouteIntentSourceStatus.Unknown, HumanInputRouteIntentContract.ContractId, HumanInputRouteIntentContract.Version, [], string.Empty)
        };

    private static HumanInputReroutePreparationInput RerouteInput(HumanInputRequest request, DateTimeOffset candidateExpiresAtUtc)
        => new("reroute-operation", request.RequestId, new HumanInputSurfaceRequestReference(request.RequestId, request.RequestVersionId, request.RequestHash), 1, HumanInputRequestLifecycleStatus.Pending.ToString(), candidateExpiresAtUtc);

    private static HumanInputAmendPreparationInput AmendInput(HumanInputRequest request, DateTimeOffset candidateExpiresAtUtc, DateTimeOffset requestExpiresAtUtc)
        => new("amend-operation", request.RequestId, new HumanInputSurfaceRequestReference(request.RequestId, request.RequestVersionId, request.RequestHash), 1, HumanInputRequestLifecycleStatus.Pending.ToString(), "Amended purpose", "Amended prompt", request.PrivacyClass.ToString(), requestExpiresAtUtc, candidateExpiresAtUtc);

    private sealed record Fixture(HumanInputSupersedeCandidatePreparer Preparer, HumanInputSupersedeCandidateRegistry Registry, HumanInputRequest Request, HumanInputFixedTimeProvider Clock, HumanInputSupersedeCandidatePreparerTestCatalog Catalog, HumanInputSupersedeCandidatePreparerTestGrantResolver GrantResolver, HumanInputRequestLifecycleStoreSnapshot Lifecycle);
}
