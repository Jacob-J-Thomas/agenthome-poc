using System.Runtime.CompilerServices;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Startup.Triggers.Schedules.Models;

namespace EmbodySense.Core.Startup.Tests.Triggers.Schedules;

public sealed class ScheduleCurrentEvidenceAdapterTests
{
    [Fact]
    public async Task Exact_current_evidence_is_deterministic_defensive_and_releases_authority_before_payload_resolution()
    {
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var sourceOwnedPayload = context.Payload;
        var adapter = context.AdapterUnderTest();

        var first = await adapter.ResolveAsync(context.Definition, context.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc);
        sourceOwnedPayload[0] ^= byte.MaxValue;
        var repeated = await adapter.ResolveAsync(context.Definition, context.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc);
        var differentObservation = await adapter.ResolveAsync(context.Definition, context.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc.AddTicks(-1));

        Assert.Equal(ScheduleCurrentEvidenceStatus.Available, first.Status);
        var evidence = Assert.IsType<ScheduleCurrentEvidence>(first.Evidence);
        Assert.Equal(context.Target, evidence.Target);
        Assert.Equal(context.Adapter, evidence.Adapter);
        Assert.Equal(context.Definition.ActorId, evidence.ActorContext.ActorId);
        Assert.Equal(context.Definition.SurfaceId, evidence.ActorContext.SurfaceId);
        Assert.Equal(context.Definition.WorkspaceId, evidence.ActorContext.WorkspaceId);
        Assert.Equal(context.Definition.RoleId, evidence.ActorContext.RoleId);
        Assert.Equal(context.Profile, evidence.Authority.Profile);
        Assert.Equal(ScheduleCurrentEvidenceTestContext.GrantEvaluatedAtUtc.AddMilliseconds(25), evidence.ObservedAtUtc);
        Assert.Equal(AuthorityBoundaryDecision.Direct, evidence.Authority.BoundaryReceipt.Decision);
        Assert.Equal(ScheduleCurrentEvidenceTestContext.GrantEvaluatedAtUtc, evidence.Authority.BoundaryReceipt.EvaluatedAtUtc);
        Assert.True(evidence.RecurrencePermitted);
        Assert.Equal("bounded scheduled input"u8.ToArray(), evidence.GetResolvedPayload());
        var callerOwned = evidence.GetResolvedPayload();
        callerOwned[0] ^= byte.MaxValue;
        Assert.Equal("bounded scheduled input"u8.ToArray(), evidence.GetResolvedPayload());
        Assert.Matches("^[0-9a-f]{64}$", evidence.EvidenceHash);
        Assert.Equal(evidence.EvidenceHash, repeated.Evidence!.EvidenceHash);
        Assert.NotEqual(evidence.EvidenceHash, differentObservation.Evidence!.EvidenceHash);
        Assert.Equal(3, context.FenceCount);
        Assert.True(context.AllReadsInsideFence);
        Assert.False(context.PayloadReadInsideFence);
        Assert.Equal(3, context.TargetReadCount);
        Assert.Equal(3, context.GrantReadCount);
        Assert.Equal(3, context.ProfileReadCount);
        Assert.Equal(3, context.CatalogReadCount);
        Assert.Equal(3, context.PayloadReadCount);
        Assert.Equal(context.Definition.Payload.GovernedReference, context.RequestedPayloadReference);
    }

    [Fact]
    public async Task Malformed_inputs_and_workspace_drift_fail_before_authority_reads()
    {
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var adapter = context.AdapterUnderTest();
        var wrongZone = context.Occurrence with
        {
            TimeZone = context.Occurrence.TimeZone with { RulesFingerprint = new string('9', 64) },
        };

        var nullDefinition = await adapter.ResolveAsync(null!, context.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc);
        var wrongOccurrence = await adapter.ResolveAsync(context.Definition, wrongZone, ScheduleCurrentEvidenceTestContext.ObservedAtUtc);
        var beforeOccurrence = await adapter.ResolveAsync(context.Definition, context.Occurrence, context.Occurrence.ScheduledAtUtc.AddTicks(-1));
        var wrongWorkspace = await adapter.ResolveAsync(context.Definition with { WorkspaceId = "workspace-2" }, context.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc);

        Assert.All([nullDefinition, wrongOccurrence, beforeOccurrence], result =>
        {
            Assert.Equal(ScheduleCurrentEvidenceStatus.Corrupt, result.Status);
            Assert.Null(result.Evidence);
        });
        Assert.Equal(ScheduleCurrentEvidenceStatus.ActorUnavailable, wrongWorkspace.Status);
        Assert.Null(wrongWorkspace.Evidence);
        Assert.Equal(0, context.FenceCount);
    }

    [Theory]
    [InlineData(AuthorityBoundaryDecision.Review, AuthorityBoundaryReason.MandatoryReview)]
    [InlineData(AuthorityBoundaryDecision.Pause, AuthorityBoundaryReason.StaleEvidence)]
    [InlineData(AuthorityBoundaryDecision.Deny, AuthorityBoundaryReason.Recurrence)]
    public async Task Non_direct_current_profile_never_becomes_direct_schedule_authority(
        AuthorityBoundaryDecision decision,
        AuthorityBoundaryReason reason)
    {
        var context = ScheduleCurrentEvidenceTestContext.Create([new AuthorityBoundaryCondition(decision, reason)]);

        var result = await context.AdapterUnderTest().ResolveAsync(
            context.Definition,
            context.Occurrence,
            ScheduleCurrentEvidenceTestContext.ObservedAtUtc);

        Assert.Equal(ScheduleCurrentEvidenceStatus.PermissionDenied, result.Status);
        Assert.Null(result.Evidence);
        Assert.Equal(0, context.PayloadReadCount);
    }

    [Fact]
    public async Task Recurrence_and_exact_adapter_must_both_remain_inside_current_profile_and_grant()
    {
        var recurrenceDenied = ScheduleCurrentEvidenceTestContext.Create(allowsRecurrence: false);
        var adapterDenied = ScheduleCurrentEvidenceTestContext.Create(grantIncludesAdapter: false);

        var recurrenceResult = await recurrenceDenied.AdapterUnderTest().ResolveAsync(
            recurrenceDenied.Definition,
            recurrenceDenied.Occurrence,
            ScheduleCurrentEvidenceTestContext.ObservedAtUtc);
        var adapterResult = await adapterDenied.AdapterUnderTest().ResolveAsync(
            adapterDenied.Definition,
            adapterDenied.Occurrence,
            ScheduleCurrentEvidenceTestContext.ObservedAtUtc);

        Assert.Equal(ScheduleCurrentEvidenceStatus.RecurrenceDenied, recurrenceResult.Status);
        Assert.Equal(ScheduleCurrentEvidenceStatus.PermissionDenied, adapterResult.Status);
        Assert.Equal(0, recurrenceDenied.PayloadReadCount);
        Assert.Equal(0, adapterDenied.PayloadReadCount);
    }

    [Fact]
    public async Task Malformed_nested_target_and_profile_evidence_return_corrupt_without_throwing()
    {
        var malformedTarget = ScheduleCurrentEvidenceTestContext.Create();
        malformedTarget.BindingResolution = malformedTarget.BindingResolution with
        {
            Artifact = null,
        };
        var malformedProfile = ScheduleCurrentEvidenceTestContext.Create();
        malformedProfile.ProfileResolution = malformedProfile.ProfileResolution with { Profile = null };

        var targetResult = await malformedTarget.AdapterUnderTest().ResolveAsync(
            malformedTarget.Definition,
            malformedTarget.Occurrence,
            ScheduleCurrentEvidenceTestContext.ObservedAtUtc);
        var profileResult = await malformedProfile.AdapterUnderTest().ResolveAsync(
            malformedProfile.Definition,
            malformedProfile.Occurrence,
            ScheduleCurrentEvidenceTestContext.ObservedAtUtc);

        Assert.Equal(ScheduleCurrentEvidenceStatus.Corrupt, targetResult.Status);
        Assert.Equal(ScheduleCurrentEvidenceStatus.Corrupt, profileResult.Status);
        Assert.Null(targetResult.Evidence);
        Assert.Null(profileResult.Evidence);
    }

    [Fact]
    public async Task Active_target_requires_exact_artifact_owner_and_canonical_graph_capabilities()
    {
        var missingArtifact = ScheduleCurrentEvidenceTestContext.Create();
        missingArtifact.BindingResolution = missingArtifact.BindingResolution with { Artifact = null };
        var wrongOwner = ScheduleCurrentEvidenceTestContext.Create();
        wrongOwner.BindingResolution = wrongOwner.BindingResolution with
        {
            OwningRole = new EmbodySense.Core.Common.ContextualRoles.Models.ContextualRoleRevisionPin(
                new EmbodySense.Core.Common.ContextualRoles.Models.ContextualRoleRevisionIdentity("other-role", 1),
                new string('a', 64)),
        };
        var substitutedCapabilities = ScheduleCurrentEvidenceTestContext.Create();
        var binding = substitutedCapabilities.BindingResolution;
        substitutedCapabilities.BindingResolution = new GovernedLoopGrantBindingResolution(
            binding.Status,
            binding.PublicationPin,
            binding.Artifact,
            binding.OwningRole,
            [],
            binding.EvidenceHash);

        var results = new[]
        {
            await missingArtifact.AdapterUnderTest().ResolveAsync(missingArtifact.Definition, missingArtifact.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc),
            await wrongOwner.AdapterUnderTest().ResolveAsync(wrongOwner.Definition, wrongOwner.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc),
            await substitutedCapabilities.AdapterUnderTest().ResolveAsync(substitutedCapabilities.Definition, substitutedCapabilities.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc),
        };

        Assert.All(results, result =>
        {
            Assert.Equal(ScheduleCurrentEvidenceStatus.Corrupt, result.Status);
            Assert.Null(result.Evidence);
        });
    }

    [Fact]
    public async Task Malformed_active_graph_artifact_and_publication_pin_are_corrupt_not_unavailable()
    {
        var malformedArtifact = ScheduleCurrentEvidenceTestContext.Create();
        malformedArtifact.BindingResolution = malformedArtifact.BindingResolution with
        {
            Artifact = (EmbodySense.Core.Common.Loops.Revisions.Models.GovernedLoopGraphRevisionArtifact)
                RuntimeHelpers.GetUninitializedObject(
                    typeof(EmbodySense.Core.Common.Loops.Revisions.Models.GovernedLoopGraphRevisionArtifact)),
        };
        var malformedPublication = ScheduleCurrentEvidenceTestContext.Create();
        malformedPublication.BindingResolution = malformedPublication.BindingResolution with
        {
            PublicationPin = malformedPublication.BindingResolution.PublicationPin! with { SchemaVersion = 2 },
        };

        var artifactResult = await malformedArtifact.AdapterUnderTest().ResolveAsync(
            malformedArtifact.Definition,
            malformedArtifact.Occurrence,
            ScheduleCurrentEvidenceTestContext.ObservedAtUtc);
        var publicationResult = await malformedPublication.AdapterUnderTest().ResolveAsync(
            malformedPublication.Definition,
            malformedPublication.Occurrence,
            ScheduleCurrentEvidenceTestContext.ObservedAtUtc);

        Assert.Equal(ScheduleCurrentEvidenceStatus.Corrupt, artifactResult.Status);
        Assert.Equal(ScheduleCurrentEvidenceStatus.Corrupt, publicationResult.Status);
        Assert.Null(artifactResult.Evidence);
        Assert.Null(publicationResult.Evidence);
    }

    [Fact]
    public async Task Active_grant_requires_the_exact_validated_requested_ceiling()
    {
        var missing = ScheduleCurrentEvidenceTestContext.Create();
        missing.GrantResolution = missing.GrantResolution with { EffectiveCeiling = null! };
        var narrower = ScheduleCurrentEvidenceTestContext.Create();
        narrower.GrantResolution = narrower.GrantResolution with
        {
            EffectiveCeiling = EmbodySense.Core.Common.Authority.AuthorityCeilingIntersection.EmptyCeiling(),
        };
        var wider = ScheduleCurrentEvidenceTestContext.Create();
        wider.GrantResolution = wider.GrantResolution with
        {
            EffectiveCeiling = wider.Ceiling with { AllowsExternalPublication = true },
        };

        var results = new[]
        {
            await missing.AdapterUnderTest().ResolveAsync(missing.Definition, missing.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc),
            await narrower.AdapterUnderTest().ResolveAsync(narrower.Definition, narrower.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc),
            await wider.AdapterUnderTest().ResolveAsync(wider.Definition, wider.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc),
        };

        Assert.All(results, result =>
        {
            Assert.Equal(ScheduleCurrentEvidenceStatus.Corrupt, result.Status);
            Assert.Null(result.Evidence);
        });
    }

    [Theory]
    [MemberData(nameof(DependencyFailures))]
    public async Task Closed_target_profile_and_grant_postures_map_without_fabricating_evidence(
        string dependency,
        int rawStatus,
        ScheduleCurrentEvidenceStatus expected)
    {
        var context = ScheduleCurrentEvidenceTestContext.Create();
        switch (dependency)
        {
            case "target":
                context.BindingResolution = context.BindingResolution with { Status = (AuthorityGrantDependencyStatus)rawStatus };
                break;
            case "profile":
                context.ProfileResolution = context.ProfileResolution with { Status = (AuthorityGrantDependencyStatus)rawStatus };
                break;
            default:
                context.GrantResolution = context.GrantResolution with { Status = (AuthorityGrantResolutionStatus)rawStatus };
                break;
        }

        var result = await context.AdapterUnderTest().ResolveAsync(
            context.Definition,
            context.Occurrence,
            ScheduleCurrentEvidenceTestContext.ObservedAtUtc);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Evidence);
        Assert.Equal(0, context.PayloadReadCount);
    }

    [Fact]
    public async Task Missing_recovered_unhealthy_future_dated_and_throwing_adapter_catalogs_fail_closed()
    {
        var missing = ScheduleCurrentEvidenceTestContext.Create();
        missing.CatalogRead = missing.AvailableCatalog();
        var recovered = ScheduleCurrentEvidenceTestContext.Create();
        recovered.CatalogRead = new CapabilityCatalogReadResult(
            CapabilityCatalogReadStatus.RecoveredLastProved,
            new CapabilityCatalogPage(1, [recovered.CatalogEntry], null),
            "recovered");
        var unhealthy = ScheduleCurrentEvidenceTestContext.Create();
        unhealthy.CatalogRead = unhealthy.AvailableCatalog(unhealthy.CatalogEntry with
        {
            Lifecycle = unhealthy.CatalogEntry.Lifecycle with { Health = CapabilityHealthState.Degraded },
        });
        var futureDated = ScheduleCurrentEvidenceTestContext.Create();
        futureDated.CatalogRead = futureDated.AvailableCatalog(futureDated.CatalogEntry with
        {
            UpdatedAtUtc = ScheduleCurrentEvidenceTestContext.GrantEvaluatedAtUtc.AddTicks(1),
        });
        var unavailable = ScheduleCurrentEvidenceTestContext.Create();
        unavailable.CatalogFailure = new IOException("catalog unavailable");

        var results = new[]
        {
            await missing.AdapterUnderTest().ResolveAsync(missing.Definition, missing.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc),
            await recovered.AdapterUnderTest().ResolveAsync(recovered.Definition, recovered.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc),
            await unhealthy.AdapterUnderTest().ResolveAsync(unhealthy.Definition, unhealthy.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc),
            await futureDated.AdapterUnderTest().ResolveAsync(futureDated.Definition, futureDated.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc),
            await unavailable.AdapterUnderTest().ResolveAsync(unavailable.Definition, unavailable.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc),
        };

        Assert.Equal(ScheduleCurrentEvidenceStatus.AdapterUnavailable, results[0].Status);
        Assert.Equal(ScheduleCurrentEvidenceStatus.AdapterUnavailable, results[1].Status);
        Assert.Equal(ScheduleCurrentEvidenceStatus.AdapterUnavailable, results[2].Status);
        Assert.Equal(ScheduleCurrentEvidenceStatus.Corrupt, results[3].Status);
        Assert.Equal(ScheduleCurrentEvidenceStatus.Unavailable, results[4].Status);
        Assert.All(results, result => Assert.Null(result.Evidence));
    }

    [Fact]
    public async Task Complete_multi_page_catalog_is_required_before_exact_adapter_is_authorized()
    {
        var laterMatch = ScheduleCurrentEvidenceTestContext.Create();
        var earlier = laterMatch.CatalogEntryWithId("org.embodysense/a-before");
        laterMatch.CatalogReads.Enqueue(Page(1, [earlier], earlier.Descriptor.Id.Value));
        laterMatch.CatalogReads.Enqueue(Page(1, [laterMatch.CatalogEntry], null));

        var duplicate = ScheduleCurrentEvidenceTestContext.Create();
        duplicate.CatalogReads.Enqueue(Page(1, [duplicate.CatalogEntry], duplicate.CatalogEntry.Descriptor.Id.Value));
        duplicate.CatalogReads.Enqueue(Page(1, [duplicate.CatalogEntry], null));

        Assert.Throws<ArgumentException>(() => Page(1, [null!], null));

        var revisionChange = ScheduleCurrentEvidenceTestContext.Create();
        var revisionEarlier = revisionChange.CatalogEntryWithId("org.embodysense/a-before");
        revisionChange.CatalogReads.Enqueue(Page(1, [revisionEarlier], revisionEarlier.Descriptor.Id.Value));
        revisionChange.CatalogReads.Enqueue(Page(2, [revisionChange.CatalogEntry], null));

        var cursorCycle = ScheduleCurrentEvidenceTestContext.Create();
        var cycleFirst = cursorCycle.CatalogEntryWithId("org.embodysense/a-before");
        var cycleSecond = cursorCycle.CatalogEntryWithId("org.embodysense/b-before");
        cursorCycle.CatalogReads.Enqueue(Page(1, [cycleFirst], cycleFirst.Descriptor.Id.Value));
        cursorCycle.CatalogReads.Enqueue(Page(1, [cycleSecond], cycleFirst.Descriptor.Id.Value));

        var laterResult = await laterMatch.AdapterUnderTest().ResolveAsync(laterMatch.Definition, laterMatch.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc);
        var duplicateResult = await duplicate.AdapterUnderTest().ResolveAsync(duplicate.Definition, duplicate.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc);
        var revisionResult = await revisionChange.AdapterUnderTest().ResolveAsync(revisionChange.Definition, revisionChange.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc);
        var cycleResult = await cursorCycle.AdapterUnderTest().ResolveAsync(cursorCycle.Definition, cursorCycle.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc);

        Assert.Equal(ScheduleCurrentEvidenceStatus.Available, laterResult.Status);
        Assert.Equal(2, laterMatch.CatalogReadCount);
        Assert.All([duplicateResult, revisionResult, cycleResult], result =>
        {
            Assert.Equal(ScheduleCurrentEvidenceStatus.Corrupt, result.Status);
            Assert.Null(result.Evidence);
        });
    }

    [Fact]
    public async Task Catalog_limit_plus_one_backpressures_without_count_overflow_or_partial_authorization()
    {
        var context = ScheduleCurrentEvidenceTestContext.Create();
        var entries = Enumerable.Range(0, 513)
            .Select(index => context.CatalogEntryWithId($"org.embodysense/test-{index:0000}"))
            .ToArray();
        for (var offset = 0; offset < entries.Length; offset += 100)
        {
            var page = entries.Skip(offset).Take(100).ToArray();
            var next = offset + page.Length < entries.Length ? page[^1].Descriptor.Id.Value : null;
            context.CatalogReads.Enqueue(Page(1, page, next));
        }

        var result = await context.AdapterUnderTest().ResolveAsync(
            context.Definition,
            context.Occurrence,
            ScheduleCurrentEvidenceTestContext.ObservedAtUtc);

        Assert.Equal(ScheduleCurrentEvidenceStatus.Backpressured, result.Status);
        Assert.Null(result.Evidence);
        Assert.Equal(6, context.CatalogReadCount);
        Assert.Equal(0, context.PayloadReadCount);
    }

    [Theory]
    [MemberData(nameof(PayloadFailures))]
    public async Task Governed_payload_failures_are_bounded_and_never_project_bytes(
        ScheduleGovernedPayloadResolution resolution,
        ScheduleCurrentEvidenceStatus expected)
    {
        var context = ScheduleCurrentEvidenceTestContext.Create();
        context.PayloadResolution = resolution;

        var result = await context.AdapterUnderTest().ResolveAsync(
            context.Definition,
            context.Occurrence,
            ScheduleCurrentEvidenceTestContext.ObservedAtUtc);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Evidence);
        Assert.Equal(1, context.PayloadReadCount);
        Assert.False(context.PayloadReadInsideFence);
    }

    [Fact]
    public async Task Payload_digest_reference_size_utf8_and_source_exception_fail_closed()
    {
        var wrongReference = ScheduleCurrentEvidenceTestContext.Create();
        wrongReference.PayloadResolution = new ScheduleGovernedPayloadResolution(
            ScheduleGovernedPayloadResolutionStatus.Available,
            "payload/other",
            wrongReference.Definition.Payload.ContentHash,
            wrongReference.Payload);
        var digestMismatch = ScheduleCurrentEvidenceTestContext.Create();
        digestMismatch.PayloadResolution = new ScheduleGovernedPayloadResolution(
            ScheduleGovernedPayloadResolutionStatus.Available,
            digestMismatch.Definition.Payload.GovernedReference,
            digestMismatch.Definition.Payload.ContentHash,
            "changed"u8.ToArray());
        var oversized = ScheduleCurrentEvidenceTestContext.Create();
        var oversizedBytes = new byte[TriggerDeliveryLimits.MaxInlinePayloadBytes + 1];
        oversized.PayloadResolution = new ScheduleGovernedPayloadResolution(
            ScheduleGovernedPayloadResolutionStatus.Available,
            oversized.Definition.Payload.GovernedReference,
            CapabilityIntegrityDigest.Compute(oversizedBytes),
            oversizedBytes);
        var invalidUtf8 = ScheduleCurrentEvidenceTestContext.Create();
        invalidUtf8.PayloadResolution = new ScheduleGovernedPayloadResolution(
            ScheduleGovernedPayloadResolutionStatus.Available,
            invalidUtf8.Definition.Payload.GovernedReference,
            CapabilityIntegrityDigest.Compute([0xff]),
            [0xff]);
        var unavailable = ScheduleCurrentEvidenceTestContext.Create();
        unavailable.PayloadFailure = new IOException("payload unavailable");

        var results = new[]
        {
            await wrongReference.AdapterUnderTest().ResolveAsync(wrongReference.Definition, wrongReference.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc),
            await digestMismatch.AdapterUnderTest().ResolveAsync(digestMismatch.Definition, digestMismatch.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc),
            await oversized.AdapterUnderTest().ResolveAsync(oversized.Definition, oversized.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc),
            await invalidUtf8.AdapterUnderTest().ResolveAsync(invalidUtf8.Definition, invalidUtf8.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc),
            await unavailable.AdapterUnderTest().ResolveAsync(unavailable.Definition, unavailable.Occurrence, ScheduleCurrentEvidenceTestContext.ObservedAtUtc),
        };

        Assert.All(results[..4], result => Assert.Equal(ScheduleCurrentEvidenceStatus.Corrupt, result.Status));
        Assert.Equal(ScheduleCurrentEvidenceStatus.PayloadUnavailable, results[4].Status);
        Assert.All(results, result => Assert.Null(result.Evidence));
    }

    [Fact]
    public async Task Over_limit_payload_sources_are_marked_invalid_without_snapshotting_or_hashing_their_bytes()
    {
        var limitPlusOne = ScheduleCurrentEvidenceTestContext.Create();
        var limitPlusOneBytes = new byte[TriggerDeliveryLimits.MaxInlinePayloadBytes + 1];
        var limitPlusOneResolution = new ScheduleGovernedPayloadResolution(
            ScheduleGovernedPayloadResolutionStatus.Available,
            limitPlusOne.Definition.Payload.GovernedReference,
            limitPlusOne.Definition.Payload.ContentHash,
            limitPlusOneBytes);
        limitPlusOne.PayloadResolution = limitPlusOneResolution;

        var large = ScheduleCurrentEvidenceTestContext.Create();
        var largeBytes = new byte[TriggerDeliveryLimits.MaxInlinePayloadBytes * 16];
        var largeResolution = new ScheduleGovernedPayloadResolution(
            ScheduleGovernedPayloadResolutionStatus.Available,
            large.Definition.Payload.GovernedReference,
            large.Definition.Payload.ContentHash,
            largeBytes);
        large.PayloadResolution = largeResolution;

        limitPlusOneBytes[0] = byte.MaxValue;
        largeBytes[0] = byte.MaxValue;
        var limitResult = await limitPlusOne.AdapterUnderTest().ResolveAsync(
            limitPlusOne.Definition,
            limitPlusOne.Occurrence,
            ScheduleCurrentEvidenceTestContext.ObservedAtUtc);
        var largeResult = await large.AdapterUnderTest().ResolveAsync(
            large.Definition,
            large.Occurrence,
            ScheduleCurrentEvidenceTestContext.ObservedAtUtc);

        Assert.False(limitPlusOneResolution.HasBoundedContent);
        Assert.False(largeResolution.HasBoundedContent);
        Assert.Null(limitPlusOneResolution.GetContent());
        Assert.Null(largeResolution.GetContent());
        Assert.Equal(ScheduleCurrentEvidenceStatus.Corrupt, limitResult.Status);
        Assert.Equal(ScheduleCurrentEvidenceStatus.Corrupt, largeResult.Status);
        Assert.Null(limitResult.Evidence);
        Assert.Null(largeResult.Evidence);
        Assert.Equal(1, limitPlusOne.PayloadReadCount);
        Assert.Equal(1, large.PayloadReadCount);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated_before_and_during_payload_resolution()
    {
        var before = ScheduleCurrentEvidenceTestContext.Create();
        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => before.AdapterUnderTest().ResolveAsync(
            before.Definition,
            before.Occurrence,
            ScheduleCurrentEvidenceTestContext.ObservedAtUtc,
            alreadyCanceled.Token));
        Assert.Equal(0, before.FenceCount);

        var during = ScheduleCurrentEvidenceTestContext.Create();
        using var canceledByPayload = new CancellationTokenSource();
        during.BeforePayloadResolve = canceledByPayload.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => during.AdapterUnderTest().ResolveAsync(
            during.Definition,
            during.Occurrence,
            ScheduleCurrentEvidenceTestContext.ObservedAtUtc,
            canceledByPayload.Token));
        Assert.Equal(1, during.FenceCount);
        Assert.Equal(1, during.PayloadReadCount);
        Assert.False(during.PayloadReadInsideFence);
    }

    public static TheoryData<string, int, ScheduleCurrentEvidenceStatus> DependencyFailures()
        => new()
        {
            { "target", (int)AuthorityGrantDependencyStatus.NotFound, ScheduleCurrentEvidenceStatus.TargetUnavailable },
            { "target", (int)AuthorityGrantDependencyStatus.Unavailable, ScheduleCurrentEvidenceStatus.Unavailable },
            { "target", (int)AuthorityGrantDependencyStatus.Ambiguous, ScheduleCurrentEvidenceStatus.Corrupt },
            { "profile", (int)AuthorityGrantDependencyStatus.Disabled, ScheduleCurrentEvidenceStatus.AuthorityUnavailable },
            { "profile", (int)AuthorityGrantDependencyStatus.Unavailable, ScheduleCurrentEvidenceStatus.Unavailable },
            { "profile", (int)AuthorityGrantDependencyStatus.Invalid, ScheduleCurrentEvidenceStatus.Corrupt },
            { "grant", (int)AuthorityGrantResolutionStatus.Suspended, ScheduleCurrentEvidenceStatus.PermissionDenied },
            { "grant", (int)AuthorityGrantResolutionStatus.ProfileUnavailable, ScheduleCurrentEvidenceStatus.AuthorityUnavailable },
            { "grant", (int)AuthorityGrantResolutionStatus.RoleUnavailable, ScheduleCurrentEvidenceStatus.ActorUnavailable },
            { "grant", (int)AuthorityGrantResolutionStatus.LoopUnavailable, ScheduleCurrentEvidenceStatus.TargetUnavailable },
            { "grant", (int)AuthorityGrantResolutionStatus.Unavailable, ScheduleCurrentEvidenceStatus.Unavailable },
            { "grant", (int)AuthorityGrantResolutionStatus.Ambiguous, ScheduleCurrentEvidenceStatus.Corrupt },
        };

    public static TheoryData<ScheduleGovernedPayloadResolution, ScheduleCurrentEvidenceStatus> PayloadFailures()
        => new()
        {
            { new(ScheduleGovernedPayloadResolutionStatus.NotFound, null, null, null), ScheduleCurrentEvidenceStatus.PayloadUnavailable },
            { new(ScheduleGovernedPayloadResolutionStatus.Unavailable, null, null, null), ScheduleCurrentEvidenceStatus.PayloadUnavailable },
            { new(ScheduleGovernedPayloadResolutionStatus.Backpressured, null, null, null), ScheduleCurrentEvidenceStatus.Backpressured },
            { new(ScheduleGovernedPayloadResolutionStatus.Corrupt, null, null, null), ScheduleCurrentEvidenceStatus.Corrupt },
            { new(ScheduleGovernedPayloadResolutionStatus.Unknown, null, null, null), ScheduleCurrentEvidenceStatus.Corrupt },
            { new(ScheduleGovernedPayloadResolutionStatus.Available, null, null, null), ScheduleCurrentEvidenceStatus.Corrupt },
        };

    private static CapabilityCatalogReadResult Page(
        long revision,
        IReadOnlyList<CapabilityCatalogEntry> entries,
        string? nextCursor)
        => new(
            CapabilityCatalogReadStatus.Available,
            new CapabilityCatalogPage(revision, entries, nextCursor),
            "page");
}
