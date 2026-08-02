using System.Text.Json;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Application.Tests.Capabilities;

public sealed class CapabilityPostureServiceTests
{
    [Fact]
    public async Task Administrative_reads_project_safe_public_posture_without_mutating_authority_state()
    {
        const string SecretValue = "private-token-value-must-not-leak";
        Assert.True(CapabilityDataClass.TryParse("workspace-content", out var dataClass, out _));
        var baseDescriptor = CapabilityArtifactTestData.Manifest(secrets: true).Descriptor;
        var requirements = new CapabilityAccessRequirements([dataClass!], CapabilityEgressMode.Restricted, ["api.example.test"], baseDescriptor.Requirements.Secrets);
        var entry = CapabilityPostureTestData.Entry(baseDescriptor with { Requirements = requirements });
        var catalog = new StubCapabilityPostureCatalogStore { Entries = [entry] };
        var lifecycle = new StubCapabilityLifecycleMutationStore { ReadResult = CapabilityPostureTestData.Lifecycle(entry) };
        var index = new StubCapabilityDependentIndex
        {
            Snapshot = new CapabilityDependentIndexSnapshot(CapabilityDependentIndexStatus.Available, "sha256:dependents", [CapabilityPostureTestData.Dependent(entry.Descriptor.Id, CapabilityRequirementKind.Required, "[1.0.0]")], "available")
        };
        var service = Service(catalog, lifecycle, index);

        var exact = await service.ReadAsync(entry.Descriptor.Id);
        var page = await service.ReadCatalogAsync(null, 10);

        Assert.Equal(CapabilityPostureReadStatus.Available, exact.Status);
        var posture = Assert.IsType<CapabilityPostureProjection>(exact.Posture);
        Assert.Equal("org.example/echo", posture.Id);
        Assert.Equal("1.0.0", posture.Version);
        Assert.Equal("skill", posture.Kind);
        Assert.Equal("org.example", posture.ProviderId);
        Assert.Equal("echo", posture.ImplementationId);
        Assert.Equal("local-source", posture.ProvenanceKind);
        Assert.Equal("file:///redacted", posture.SourceUri);
        Assert.DoesNotContain("/sources/echo.exe", JsonSerializer.Serialize(new { exact, page }), StringComparison.Ordinal);
        Assert.Equal(["api_token"], posture.SecretRequirements);
        Assert.Equal(["workspace-content"], posture.DataClasses);
        Assert.Equal("restricted", posture.EgressMode);
        Assert.Equal(["api.example.test"], posture.EgressDestinations);
        Assert.Equal(["windows/x64"], posture.SupportedPlatforms);
        Assert.Equal(CapabilityPostureState.Available, posture.State);
        Assert.Single(posture.Dependents);
        Assert.True(posture.AreDependentsAvailable);
        Assert.False(posture.DependentsTruncated);
        Assert.Equal(7, page.CatalogRevision);
        Assert.Equal([posture.Id], page.Entries.Select(item => item.Id));
        Assert.DoesNotContain(SecretValue, JsonSerializer.Serialize(new { exact, page }), StringComparison.Ordinal);
        Assert.Equal(0, catalog.MutationCount);
        Assert.Null(lifecycle.PreviewRequest);
        Assert.Null(lifecycle.MutatedPreview);
        Assert.Equal(2, lifecycle.ReadCount);
    }

    [Fact]
    public async Task Administrative_posture_distinguishes_removed_unavailable_incompatible_conflicting_and_degraded_states()
    {
        var descriptor = CapabilityArtifactTestData.Manifest().Descriptor;
        var removed = await ReadStateAsync(CapabilityPostureTestData.Entry(descriptor, retirement: CapabilityRetirementState.Removed));
        var unavailable = await ReadStateAsync(CapabilityPostureTestData.Entry(descriptor, enablement: CapabilityEnablementState.Disabled));
        var incompatible = await ReadStateAsync(CapabilityPostureTestData.Entry(CapabilityPostureTestData.WithCompatibility(descriptor, "*", "linux/arm64")));
        var conflict = await ReadStateAsync(CapabilityPostureTestData.Entry(descriptor), CapabilityPostureTestData.Dependent(descriptor.Id, CapabilityRequirementKind.Required, "[2.0.0]"));
        var degraded = await ReadStateAsync(CapabilityPostureTestData.Entry(descriptor, health: CapabilityHealthState.Degraded));
        var dependencyUnavailable = await ReadStateAsync(CapabilityPostureTestData.Entry(descriptor), indexStatus: CapabilityDependentIndexStatus.Unavailable);
        var missingLifecycle = await Service(new StubCapabilityPostureCatalogStore { Entries = [CapabilityPostureTestData.Entry(descriptor)] }, new StubCapabilityLifecycleMutationStore()).ReadAsync(descriptor.Id);

        Assert.Equal(CapabilityPostureState.Removed, removed);
        Assert.Equal(CapabilityPostureState.Unavailable, unavailable);
        Assert.Equal(CapabilityPostureState.Incompatible, incompatible);
        Assert.Equal(CapabilityPostureState.DependencyConflict, conflict);
        Assert.Equal(CapabilityPostureState.Degraded, degraded);
        Assert.Equal(CapabilityPostureState.DependencyConflict, dependencyUnavailable);
        Assert.Equal(CapabilityPostureState.Available, missingLifecycle.Posture?.State);
        Assert.True(missingLifecycle.Posture!.IsLifecycleEnabled);
    }

    [Fact]
    public async Task Administrative_posture_marks_recovered_and_lifecycle_drift_honestly()
    {
        var entry = CapabilityPostureTestData.Entry();
        var contradictoryDescriptor = entry.Descriptor with { Purpose = "A descriptor that contradicts its catalog identity." };
        var contradictoryEntry = entry with { Descriptor = contradictoryDescriptor };
        var lifecycleState = new CapabilityLifecycleState(contradictoryDescriptor, CapabilityIntegrityDigest.Compute("drift"u8), true, false, 9, "drift", CapabilityPostureTestData.Now);
        var lifecycle = new StubCapabilityLifecycleMutationStore
        {
            ReadResult = new CapabilityLifecycleReadResult(CapabilityLifecycleReadStatus.RecoveredLastProved, lifecycleState, [], [], 9, "recovered")
        };
        var catalog = new StubCapabilityPostureCatalogStore { Status = CapabilityCatalogReadStatus.RecoveredLastProved, Entries = [contradictoryEntry] };

        var result = await Service(catalog, lifecycle).ReadAsync(entry.Descriptor.Id);

        Assert.Equal(CapabilityPostureReadStatus.Recovered, result.Status);
        Assert.Equal(CapabilityPostureState.DependencyConflict, result.Posture?.State);
        Assert.True(result.Posture?.IsRecovered);
        Assert.Equal(9, result.Posture?.LifecycleRevision);
    }

    [Fact]
    public async Task Absence_from_a_recovered_catalog_is_unavailable_instead_of_not_found()
    {
        var capabilityId = CapabilityPostureTestData.Entry().Descriptor.Id;
        var catalog = new StubCapabilityPostureCatalogStore { Status = CapabilityCatalogReadStatus.RecoveredLastProved };
        var service = Service(catalog);

        var read = await service.ReadAsync(capabilityId);
        var preview = await service.PreviewAsync(new CapabilityPosturePreviewQuery(capabilityId, CapabilityLifecycleOperationKind.Disable));

        Assert.Equal(CapabilityPostureReadStatus.Unavailable, read.Status);
        Assert.Null(read.Posture);
        Assert.Equal("capability_posture_unavailable", read.Error?.Code);
        Assert.Equal(CapabilityPostureReadStatus.Unavailable, preview.Status);
        Assert.Null(preview.Preview);
        Assert.Equal("capability_posture_unavailable", preview.Error?.Code);
    }

    [Theory]
    [InlineData("2.0.0", "upgrade-v2")]
    [InlineData("0.9.0", "rollback-v1")]
    public async Task Lifecycle_replacement_descriptor_is_the_effective_upgrade_or_rollback_posture(string version, string operationId)
    {
        var entry = CapabilityPostureTestData.Entry();
        var replacement = entry.Descriptor with { Version = CapabilityPostureTestData.Version(version), Purpose = $"Effective {operationId} descriptor." };
        var lifecycleState = new CapabilityLifecycleState(replacement, CapabilityIntegrityDigest.Compute("replacement"u8), true, false, 9, operationId, CapabilityPostureTestData.Now);
        var lifecycle = new StubCapabilityLifecycleMutationStore
        {
            ReadResult = new CapabilityLifecycleReadResult(CapabilityLifecycleReadStatus.Available, lifecycleState, [], [], 9, "available")
        };

        var result = await Service(new StubCapabilityPostureCatalogStore { Entries = [entry] }, lifecycle).ReadAsync(entry.Descriptor.Id);

        Assert.Equal(CapabilityPostureReadStatus.Available, result.Status);
        var posture = Assert.IsType<CapabilityPostureProjection>(result.Posture);
        Assert.Equal(CapabilityPostureState.Available, posture.State);
        Assert.Equal(version, posture.Version);
        Assert.Equal(replacement.Purpose, posture.Purpose);
        Assert.True(CapabilityDescriptorIdentity.TryCreate(replacement, out var identity, out _));
        Assert.Equal(identity!.Hash.Value, posture.DescriptorHash);
    }

    [Fact]
    public async Task Lifecycle_disable_and_remove_override_stale_catalog_tokens_and_removed_wins_an_enabled_contradiction()
    {
        var entry = CapabilityPostureTestData.Entry();
        var disabledState = new CapabilityLifecycleState(entry.Descriptor, CapabilityIntegrityDigest.Compute("disabled"u8), false, false, 8, "disable", CapabilityPostureTestData.Now);
        var removedState = new CapabilityLifecycleState(entry.Descriptor, CapabilityIntegrityDigest.Compute("removed"u8), true, true, 9, "remove", CapabilityPostureTestData.Now);
        var catalogRemovedEntry = entry with
        {
            Lifecycle = entry.Lifecycle with
            {
                Declaration = CapabilityDeclarationState.Withdrawn,
                Installation = CapabilityInstallationState.NotInstalled,
                Enablement = CapabilityEnablementState.Disabled,
                Health = CapabilityHealthState.Unavailable,
                Retirement = CapabilityRetirementState.Removed
            }
        };
        var staleEnabledState = new CapabilityLifecycleState(entry.Descriptor, CapabilityIntegrityDigest.Compute("stale-enabled"u8), true, false, 10, "stale-enable", CapabilityPostureTestData.Now);

        var disabled = await ReadWithLifecycleAsync(entry, disabledState);
        var removed = await ReadWithLifecycleAsync(entry, removedState);
        var catalogRemoved = await ReadWithLifecycleAsync(catalogRemovedEntry, staleEnabledState);

        Assert.Equal(CapabilityPostureState.Unavailable, disabled.State);
        Assert.Equal("disabled", disabled.Enablement);
        Assert.Equal("active", disabled.Retirement);
        Assert.False(disabled.IsLifecycleEnabled);
        Assert.False(disabled.IsRemoved);
        Assert.Equal(CapabilityPostureState.Removed, removed.State);
        Assert.Equal("disabled", removed.Enablement);
        Assert.Equal("removed", removed.Retirement);
        Assert.False(removed.IsLifecycleEnabled);
        Assert.True(removed.IsRemoved);
        Assert.Equal(CapabilityPostureState.Removed, catalogRemoved.State);
        Assert.Equal("disabled", catalogRemoved.Enablement);
        Assert.Equal("removed", catalogRemoved.Retirement);
        Assert.False(catalogRemoved.IsLifecycleEnabled);
        Assert.True(catalogRemoved.IsRemoved);
    }

    [Fact]
    public async Task Lifecycle_preview_is_bounded_deterministic_and_never_creates_mutation_authority()
    {
        var entry = CapabilityPostureTestData.Entry();
        var dependents = Enumerable.Range(0, 101)
            .Select(index => CapabilityPostureTestData.Dependent(entry.Descriptor.Id, index == 100 ? CapabilityRequirementKind.Optional : CapabilityRequirementKind.Required, "[2.0.0]", index))
            .ToArray();
        var catalog = new StubCapabilityPostureCatalogStore { Entries = [entry] };
        var lifecycle = new StubCapabilityLifecycleMutationStore { ReadResult = CapabilityPostureTestData.Lifecycle(entry) };
        var index = new StubCapabilityDependentIndex { Snapshot = new CapabilityDependentIndexSnapshot(CapabilityDependentIndexStatus.Available, "sha256:current-dependent-set", dependents, "available") };

        var result = await Service(catalog, lifecycle, index).PreviewAsync(new CapabilityPosturePreviewQuery(entry.Descriptor.Id, CapabilityLifecycleOperationKind.Disable));

        Assert.Equal(CapabilityPostureReadStatus.Available, result.Status);
        var preview = Assert.IsType<CapabilityPosturePreviewProjection>(result.Preview);
        Assert.Equal("sha256:current-dependent-set", preview.DependentSetHash);
        Assert.True(preview.IsBlocked);
        Assert.True(preview.ImpactsTruncated);
        Assert.Equal(100, preview.Impacts.Count);
        Assert.All(preview.Impacts, item => Assert.Equal(CapabilityLifecycleImpactOutcome.Blocked, item.Outcome));
        Assert.Null(lifecycle.PreviewRequest);
        Assert.Null(lifecycle.MutatedPreview);
        Assert.Equal(0, lifecycle.AuditMarks);
        Assert.Equal(0, catalog.MutationCount);
    }

    [Fact]
    public async Task Lifecycle_preview_reports_optional_degradation_preserved_upgrade_and_rollback_history()
    {
        var entry = CapabilityPostureTestData.Entry();
        var prior = entry.Descriptor with { Version = CapabilityPostureTestData.Version("0.9.0") };
        var lifecycle = new StubCapabilityLifecycleMutationStore
        {
            ReadResult = new CapabilityLifecycleReadResult(
                CapabilityLifecycleReadStatus.Available,
                CapabilityPostureTestData.Lifecycle(entry).State,
                [new CapabilityLifecycleHistoryEntry(prior, CapabilityIntegrityDigest.Compute("prior"u8), true, false, 6, "prior", CapabilityPostureTestData.Now)],
                [],
                7,
                "available")
        };
        var optional = CapabilityPostureTestData.Dependent(entry.Descriptor.Id, CapabilityRequirementKind.Optional, "[2.0.0]");
        var required = CapabilityPostureTestData.Dependent(entry.Descriptor.Id, CapabilityRequirementKind.Required, "[2.0.0]", 1);
        var index = new StubCapabilityDependentIndex { Snapshot = new CapabilityDependentIndexSnapshot(CapabilityDependentIndexStatus.Available, "sha256:deps", [optional, required], "available") };
        var service = Service(new StubCapabilityPostureCatalogStore { Entries = [entry] }, lifecycle, index);

        var disable = await service.PreviewAsync(new CapabilityPosturePreviewQuery(entry.Descriptor.Id, CapabilityLifecycleOperationKind.Disable));
        var upgrade = await service.PreviewAsync(new CapabilityPosturePreviewQuery(entry.Descriptor.Id, CapabilityLifecycleOperationKind.Upgrade, CapabilityPostureTestData.Version("2.0.0")));
        var rollback = await service.PreviewAsync(new CapabilityPosturePreviewQuery(entry.Descriptor.Id, CapabilityLifecycleOperationKind.Rollback));

        Assert.True(disable.Preview?.HasDegradation);
        Assert.Contains(disable.Preview!.Impacts, item => item.Outcome == CapabilityLifecycleImpactOutcome.Degraded);
        Assert.All(upgrade.Preview!.Impacts, item => Assert.Equal(CapabilityLifecycleImpactOutcome.Preserved, item.Outcome));
        Assert.Equal("0.9.0", rollback.Preview?.TargetVersion);
        Assert.True(rollback.Preview?.IsBlocked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Enable_preview_projects_the_current_proved_version_and_preserves_compatible_dependents(bool hasLifecycleState)
    {
        var entry = CapabilityPostureTestData.Entry();
        var lifecycle = new StubCapabilityLifecycleMutationStore();
        if (hasLifecycleState)
        {
            lifecycle.ReadResult = CapabilityPostureTestData.Lifecycle(entry) with
            {
                State = CapabilityPostureTestData.Lifecycle(entry).State! with { IsEnabled = false }
            };
        }
        var required = CapabilityPostureTestData.Dependent(entry.Descriptor.Id, CapabilityRequirementKind.Required, "[1.0.0]");
        var index = new StubCapabilityDependentIndex { Snapshot = new CapabilityDependentIndexSnapshot(CapabilityDependentIndexStatus.Available, "sha256:enable-deps", [required], "available") };

        var result = await Service(new StubCapabilityPostureCatalogStore { Entries = [entry] }, lifecycle, index)
            .PreviewAsync(new CapabilityPosturePreviewQuery(entry.Descriptor.Id, CapabilityLifecycleOperationKind.Enable));

        Assert.Equal(CapabilityPostureReadStatus.Available, result.Status);
        Assert.Equal(entry.Descriptor.Version.Value, result.Preview?.TargetVersion);
        Assert.False(result.Preview?.IsBlocked);
        var impact = Assert.Single(result.Preview!.Impacts);
        Assert.True(impact.IsCompatible);
        Assert.Equal(CapabilityLifecycleImpactOutcome.Preserved, impact.Outcome);
        Assert.Null(lifecycle.PreviewRequest);
        Assert.Null(lifecycle.MutatedPreview);
        Assert.Equal(0, lifecycle.AuditMarks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rollback_preview_blocks_required_dependents_when_the_immediately_prior_state_was_not_admitted(bool wasRemoved)
    {
        var entry = CapabilityPostureTestData.Entry();
        var prior = entry.Descriptor with { Version = CapabilityPostureTestData.Version("0.9.0") };
        var lifecycle = new StubCapabilityLifecycleMutationStore
        {
            ReadResult = new CapabilityLifecycleReadResult(
                CapabilityLifecycleReadStatus.Available,
                CapabilityPostureTestData.Lifecycle(entry).State,
                [new CapabilityLifecycleHistoryEntry(prior, CapabilityIntegrityDigest.Compute("prior-not-admitted"u8), false, wasRemoved, 6, "prior-not-admitted", CapabilityPostureTestData.Now)],
                [],
                7,
                "available")
        };
        var required = CapabilityPostureTestData.Dependent(entry.Descriptor.Id, CapabilityRequirementKind.Required, "[0.9.0]");
        var index = new StubCapabilityDependentIndex { Snapshot = new CapabilityDependentIndexSnapshot(CapabilityDependentIndexStatus.Available, "sha256:deps", [required], "available") };

        var result = await Service(new StubCapabilityPostureCatalogStore { Entries = [entry] }, lifecycle, index).PreviewAsync(new CapabilityPosturePreviewQuery(entry.Descriptor.Id, CapabilityLifecycleOperationKind.Rollback));

        Assert.Equal(CapabilityPostureReadStatus.Available, result.Status);
        Assert.Equal("0.9.0", result.Preview?.TargetVersion);
        Assert.True(result.Preview?.IsBlocked);
        var impact = Assert.Single(result.Preview!.Impacts);
        Assert.False(impact.IsCompatible);
        Assert.Equal(CapabilityLifecycleImpactOutcome.Blocked, impact.Outcome);
    }

    [Fact]
    public async Task Model_context_contains_only_exact_assigned_and_currently_authorized_admitted_pins()
    {
        var admission = CapabilityPostureTestData.Admission("org.example/alpha", "org.example/beta", "org.example/gamma");
        var service = Service();

        var result = await service.ReadModelContextAsync(admission, ["org.example/gamma", "org.example/beta"], ["org.example/alpha", "org.example/beta"]);

        Assert.Equal(CapabilityPostureReadStatus.Available, result.Status);
        var capability = Assert.Single(result.Capabilities);
        Assert.Equal("org.example/beta", capability.Id);
        Assert.Equal("1.0.0", capability.Version);
        Assert.Equal("graph-node", capability.Kind);
        Assert.Equal("Test-safe description for beta.", capability.Description);
        Assert.Equal("{\"schemaVersion\":1,\"capabilities\":[{\"id\":\"org.example/beta\",\"version\":\"1.0.0\",\"kind\":\"graph-node\",\"description\":\"Test-safe description for beta.\"}]}", result.CanonicalJson);
        Assert.DoesNotContain("alpha", result.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("gamma", result.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("implementation", result.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("provenance", result.CanonicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_context_fails_closed_with_stable_non_leaking_errors_and_bounds()
    {
        var one = CapabilityPostureTestData.Admission("org.example/alpha");
        var rejectedService = Service(admission: new TestCapabilityAdmissionService { RevalidationResult = new CapabilityRevalidationResult(false, [], "private workspace mismatch /secret/path") });
        var rejected = await rejectedService.ReadModelContextAsync(one, ["org.example/alpha"], ["org.example/alpha"]);
        var otherPin = CapabilityPostureTestData.Admission("org.example/beta").Pins.Single();
        var substituted = await Service(admission: new TestCapabilityAdmissionService { RevalidationResult = new CapabilityRevalidationResult(true, [otherPin], "forged") }).ReadModelContextAsync(one, ["org.example/alpha"], ["org.example/alpha"]);
        var two = CapabilityPostureTestData.Admission("org.example/alpha", "org.example/beta");
        var duplicated = await Service(admission: new TestCapabilityAdmissionService { RevalidationResult = new CapabilityRevalidationResult(true, [two.Pins[0], two.Pins[0]], "forged") }).ReadModelContextAsync(two, ["org.example/alpha"], ["org.example/alpha"]);
        var malformed = await Service().ReadModelContextAsync(one with { SchemaVersion = 2 }, ["org.example/alpha"], ["org.example/alpha"]);
        var duplicate = await Service().ReadModelContextAsync(one, ["org.example/alpha", "org.example/alpha"], ["org.example/alpha"]);

        var ids = Enumerable.Range(0, 17).Select(index => $"org.example/cap-{index:D2}").ToArray();
        var oversized = await Service().ReadModelContextAsync(CapabilityPostureTestData.Admission(ids), ids, ids);

        Assert.Equal("capability_posture_unavailable", rejected.Error?.Code);
        Assert.Equal("capability_posture_unavailable", substituted.Error?.Code);
        Assert.Equal("capability_posture_unavailable", duplicated.Error?.Code);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(rejected), StringComparison.Ordinal);
        Assert.Equal("invalid_capability_posture_request", malformed.Error?.Code);
        Assert.Equal("invalid_capability_posture_request", duplicate.Error?.Code);
        Assert.Equal("capability_posture_limit_exceeded", oversized.Error?.Code);
        Assert.All([rejected, substituted, duplicated, malformed, duplicate, oversized], result =>
        {
            Assert.Empty(result.Capabilities);
            Assert.Equal(string.Empty, result.CanonicalJson);
        });
    }

    [Fact]
    public async Task Read_errors_are_stable_and_cancellation_propagates()
    {
        var entry = CapabilityPostureTestData.Entry();
        var unavailableCatalog = new StubCapabilityPostureCatalogStore { Status = CapabilityCatalogReadStatus.Unavailable };
        var unavailable = await Service(unavailableCatalog).ReadAsync(entry.Descriptor.Id);
        var unavailablePage = await Service(unavailableCatalog).ReadCatalogAsync(null, 10);
        var notFound = await Service(new StubCapabilityPostureCatalogStore()).ReadAsync(entry.Descriptor.Id);
        var nullId = await Service().ReadAsync(null!);
        var invalidPage = await Service().ReadCatalogAsync("Not Canonical", 0);
        var unavailableLifecycle = await Service(new StubCapabilityPostureCatalogStore { Entries = [entry] }, new StubCapabilityLifecycleMutationStore { ReadResult = new CapabilityLifecycleReadResult(CapabilityLifecycleReadStatus.Unavailable, null, [], [], null, "private failure") }).ReadAsync(entry.Descriptor.Id);
        var unavailableLifecyclePage = await Service(new StubCapabilityPostureCatalogStore { Entries = [entry] }, new StubCapabilityLifecycleMutationStore { ReadResult = new CapabilityLifecycleReadResult(CapabilityLifecycleReadStatus.Unavailable, null, [], [], null, "private failure") }).ReadCatalogAsync(null, 10);
        var unavailableDependents = await Service(new StubCapabilityPostureCatalogStore { Entries = [entry] }, index: new StubCapabilityDependentIndex { Snapshot = new CapabilityDependentIndexSnapshot(CapabilityDependentIndexStatus.Unavailable, string.Empty, [], "private failure") }).PreviewAsync(new CapabilityPosturePreviewQuery(entry.Descriptor.Id, CapabilityLifecycleOperationKind.Disable));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Service(new StubCapabilityPostureCatalogStore { Entries = [entry] }).ReadAsync(entry.Descriptor.Id, cancellation.Token));

        Assert.Equal("capability_posture_unavailable", unavailable.Error?.Code);
        Assert.Equal("capability_posture_unavailable", unavailablePage.Error?.Code);
        Assert.Equal(CapabilityPostureReadStatus.NotFound, notFound.Status);
        Assert.Equal(CapabilityPostureReadStatus.Invalid, nullId.Status);
        Assert.Equal("capability_posture_unavailable", notFound.Error?.Code);
        Assert.Equal("invalid_capability_posture_request", invalidPage.Error?.Code);
        Assert.Equal("capability_posture_unavailable", unavailableLifecycle.Error?.Code);
        Assert.Equal("capability_posture_unavailable", unavailableLifecyclePage.Error?.Code);
        Assert.Equal("capability_dependency_posture_unavailable", unavailableDependents.Error?.Code);
        Assert.DoesNotContain("private failure", JsonSerializer.Serialize(new { unavailableLifecycle, unavailableDependents }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_rejects_missing_removed_and_invalid_evidence_without_calling_mutation_ports()
    {
        var entry = CapabilityPostureTestData.Entry();
        var removedEntry = CapabilityPostureTestData.Entry(entry.Descriptor, retirement: CapabilityRetirementState.Removed);
        var missing = await Service().PreviewAsync(new CapabilityPosturePreviewQuery(entry.Descriptor.Id, CapabilityLifecycleOperationKind.Disable));
        var unavailableCatalog = await Service(new StubCapabilityPostureCatalogStore { Status = CapabilityCatalogReadStatus.Unavailable }).PreviewAsync(new CapabilityPosturePreviewQuery(entry.Descriptor.Id, CapabilityLifecycleOperationKind.Disable));
        var unavailableLifecycle = await Service(new StubCapabilityPostureCatalogStore { Entries = [entry] }, new StubCapabilityLifecycleMutationStore { ReadResult = new CapabilityLifecycleReadResult(CapabilityLifecycleReadStatus.Unavailable, null, [], [], null, "private") }).PreviewAsync(new CapabilityPosturePreviewQuery(entry.Descriptor.Id, CapabilityLifecycleOperationKind.Disable));
        var missingLifecycle = await Service(new StubCapabilityPostureCatalogStore { Entries = [entry] }, new StubCapabilityLifecycleMutationStore()).PreviewAsync(new CapabilityPosturePreviewQuery(entry.Descriptor.Id, CapabilityLifecycleOperationKind.Disable));
        var removed = await Service(new StubCapabilityPostureCatalogStore { Entries = [removedEntry] }).PreviewAsync(new CapabilityPosturePreviewQuery(entry.Descriptor.Id, CapabilityLifecycleOperationKind.Remove));
        var rollbackMissing = await Service(new StubCapabilityPostureCatalogStore { Entries = [entry] }).PreviewAsync(new CapabilityPosturePreviewQuery(entry.Descriptor.Id, CapabilityLifecycleOperationKind.Rollback));
        var invalid = await Service().PreviewAsync(null!);

        Assert.Equal(CapabilityPostureReadStatus.NotFound, missing.Status);
        Assert.Equal(CapabilityPostureReadStatus.Unavailable, unavailableCatalog.Status);
        Assert.Equal(CapabilityPostureReadStatus.Unavailable, unavailableLifecycle.Status);
        Assert.Equal(CapabilityPostureReadStatus.Available, missingLifecycle.Status);
        Assert.Equal(CapabilityPostureReadStatus.Invalid, removed.Status);
        Assert.Equal(CapabilityPostureReadStatus.NotFound, rollbackMissing.Status);
        Assert.Equal(CapabilityPostureReadStatus.Invalid, invalid.Status);
        Assert.NotNull(missingLifecycle.Preview);
        Assert.All([missing, unavailableCatalog, unavailableLifecycle, removed, rollbackMissing, invalid], result => Assert.Null(result.Preview));
    }

    [Fact]
    public async Task Availability_exceptions_map_to_stable_results_while_cancellation_always_propagates()
    {
        var entry = CapabilityPostureTestData.Entry();
        var throwingCatalog = new StubCapabilityPostureCatalogStore { ReadException = new IOException("private path /secret/catalog") };
        var throwingLifecycle = new StubCapabilityLifecycleMutationStore { ReadException = new FormatException("private lifecycle") };
        var throwingDependents = new StubCapabilityDependentIndex { CaptureException = new InvalidOperationException("private dependencies") };
        var throwingAdmission = new TestCapabilityAdmissionService { RevalidationException = new OverflowException("private admission") };
        var admission = CapabilityPostureTestData.Admission("org.example/alpha");

        var catalog = await Service(throwingCatalog).ReadCatalogAsync(null, 10);
        var exact = await Service(new StubCapabilityPostureCatalogStore { Entries = [entry] }, throwingLifecycle).ReadAsync(entry.Descriptor.Id);
        var preview = await Service(new StubCapabilityPostureCatalogStore { Entries = [entry] }, index: throwingDependents).PreviewAsync(new CapabilityPosturePreviewQuery(entry.Descriptor.Id, CapabilityLifecycleOperationKind.Disable));
        var model = await Service(admission: throwingAdmission).ReadModelContextAsync(admission, ["org.example/alpha"], ["org.example/alpha"]);

        Assert.All([catalog.Error, exact.Error, preview.Error, model.Error], error => Assert.Equal("capability_posture_unavailable", error?.Code));
        Assert.DoesNotContain("private", JsonSerializer.Serialize(new { catalog, exact, preview, model }), StringComparison.Ordinal);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Service().ReadModelContextAsync(admission, ["org.example/alpha"], ["org.example/alpha"], cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => Service(new StubCapabilityPostureCatalogStore { Entries = [entry] }).PreviewAsync(new CapabilityPosturePreviewQuery(entry.Descriptor.Id, CapabilityLifecycleOperationKind.Disable), cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => Service(new StubCapabilityPostureCatalogStore { Entries = [entry] }).ReadCatalogAsync(null, 10, cancellation.Token));
    }

    [Fact]
    public async Task Exact_reads_fail_closed_for_changing_repeating_and_oversized_catalog_scans()
    {
        var entry = CapabilityPostureTestData.Entry();
        Assert.True(CapabilityId.TryParse("org.example/missing", out var missingId, out _));
        var changing = new StubCapabilityPostureCatalogStore();
        changing.ReadResults.Enqueue(ReadPage(7, [entry], "org.example/cursor-a"));
        changing.ReadResults.Enqueue(ReadPage(8, [entry], null));
        var repeating = new StubCapabilityPostureCatalogStore();
        repeating.ReadResults.Enqueue(ReadPage(7, [entry], "org.example/cursor-a"));
        repeating.ReadResults.Enqueue(ReadPage(7, [entry], "org.example/cursor-a"));
        var oversized = new StubCapabilityPostureCatalogStore();
        for (var page = 0; page < 10; page++)
        {
            oversized.ReadResults.Enqueue(ReadPage(7, Enumerable.Repeat(entry, 50).ToArray(), $"org.example/cursor-{page}"));
        }
        oversized.ReadResults.Enqueue(ReadPage(7, Enumerable.Repeat(entry, 13).ToArray(), null));

        var changed = await Service(changing).ReadAsync(missingId!);
        var repeated = await Service(repeating).ReadAsync(missingId!);
        var exceeded = await Service(oversized).ReadAsync(missingId!);

        Assert.All([changed, repeated, exceeded], result =>
        {
            Assert.Equal(CapabilityPostureReadStatus.Unavailable, result.Status);
            Assert.Equal("capability_posture_unavailable", result.Error?.Code);
        });
    }

    [Fact]
    public async Task Exact_reads_and_previews_reach_the_last_entry_in_a_full_catalog()
    {
        var template = CapabilityPostureTestData.Entry();
        var entries = Enumerable.Range(0, 512)
            .Select(index =>
            {
                Assert.True(CapabilityId.TryParse($"org.example/cap-{index:D3}", out var id, out _));
                return CapabilityPostureTestData.Entry(template.Descriptor with { Id = id! });
            })
            .ToArray();
        var catalog = new StubCapabilityPostureCatalogStore { Entries = entries };
        var service = Service(catalog);
        var target = entries[^1].Descriptor.Id;

        var exact = await service.ReadAsync(target);
        var preview = await service.PreviewAsync(new CapabilityPosturePreviewQuery(target, CapabilityLifecycleOperationKind.Disable));

        Assert.Equal(CapabilityPostureReadStatus.Available, exact.Status);
        Assert.Equal(target.Value, exact.Posture?.Id);
        Assert.Equal(CapabilityPostureReadStatus.Available, preview.Status);
        Assert.Equal(target.Value, preview.Preview?.CapabilityId);
        Assert.Equal(22, catalog.ReadCount);
    }

    [Fact]
    public async Task Model_context_enforces_authority_collection_and_utf8_output_bounds()
    {
        var ids = Enumerable.Range(0, 16).Select(index => $"org.example/cap-{index:D2}").ToArray();
        var admission = CapabilityPostureTestData.Admission(ids);
        var longDescription = new string('\u00e9', CapabilityContractLimits.MaxPurposeCharacters);
        var large = admission with { Pins = admission.Pins.Select(pin => pin with { SafeDescription = longDescription }).ToArray() };
        var largeResult = await Service().ReadModelContextAsync(large, ids, ids);
        var tooManyIds = Enumerable.Range(0, CapabilityContractLimits.MaxCapabilityAdmissionPins + 1).Select(index => $"org.example/authority-{index:D3}").ToArray();
        var authorityResult = await Service().ReadModelContextAsync(admission, tooManyIds, ids);
        var nullAuthority = await Service().ReadModelContextAsync(admission, null!, ids);

        Assert.Equal("capability_posture_limit_exceeded", largeResult.Error?.Code);
        Assert.Equal("invalid_capability_posture_request", authorityResult.Error?.Code);
        Assert.Equal("invalid_capability_posture_request", nullAuthority.Error?.Code);
    }

    [Fact]
    public void Constructor_rejects_missing_ports_and_non_exact_host_platform()
    {
        var catalog = new StubCapabilityPostureCatalogStore();
        var lifecycle = new StubCapabilityLifecycleMutationStore();
        var index = new StubCapabilityDependentIndex();
        var admission = new TestCapabilityAdmissionService();
        var version = CapabilityPostureTestData.Version("1.0.0");
        var platform = CapabilityPostureTestData.Platform("windows/x64");

        Assert.Throws<ArgumentNullException>(() => new CapabilityPostureService(null!, lifecycle, index, admission, version, platform));
        Assert.Throws<ArgumentNullException>(() => new CapabilityPostureService(catalog, null!, index, admission, version, platform));
        Assert.Throws<ArgumentNullException>(() => new CapabilityPostureService(catalog, lifecycle, null!, admission, version, platform));
        Assert.Throws<ArgumentNullException>(() => new CapabilityPostureService(catalog, lifecycle, index, null!, version, platform));
        Assert.Throws<ArgumentNullException>(() => new CapabilityPostureService(catalog, lifecycle, index, admission, null!, platform));
        Assert.Throws<ArgumentNullException>(() => new CapabilityPostureService(catalog, lifecycle, index, admission, version, null!));
        Assert.Throws<ArgumentException>(() => new CapabilityPostureService(catalog, lifecycle, index, admission, version, CapabilityPlatform.Any));
    }

    private static CapabilityPostureService Service(
        StubCapabilityPostureCatalogStore? catalog = null,
        StubCapabilityLifecycleMutationStore? lifecycle = null,
        StubCapabilityDependentIndex? index = null,
        TestCapabilityAdmissionService? admission = null)
    {
        var resolvedCatalog = catalog ?? new StubCapabilityPostureCatalogStore();
        var resolvedLifecycle = lifecycle ?? (resolvedCatalog.Entries.Count == 1
            ? new StubCapabilityLifecycleMutationStore { ReadResult = CapabilityPostureTestData.Lifecycle(resolvedCatalog.Entries[0]) }
            : new StubCapabilityLifecycleMutationStore());
        return new CapabilityPostureService(
            resolvedCatalog,
            resolvedLifecycle,
            index ?? new StubCapabilityDependentIndex(),
            admission ?? new TestCapabilityAdmissionService(),
            CapabilityPostureTestData.Version("1.0.0"),
            CapabilityPostureTestData.Platform("windows/x64"));
    }

    private static async Task<CapabilityPostureState> ReadStateAsync(CapabilityCatalogEntry entry, CapabilityDependent? dependent = null, CapabilityDependentIndexStatus indexStatus = CapabilityDependentIndexStatus.Available)
    {
        var dependents = dependent is null ? [] : new[] { dependent };
        var index = new StubCapabilityDependentIndex { Snapshot = new CapabilityDependentIndexSnapshot(indexStatus, indexStatus == CapabilityDependentIndexStatus.Available ? "sha256:deps" : string.Empty, dependents, "test") };
        var result = await Service(new StubCapabilityPostureCatalogStore { Entries = [entry] }, index: index).ReadAsync(entry.Descriptor.Id);
        return Assert.IsType<CapabilityPostureProjection>(result.Posture).State;
    }

    private static async Task<CapabilityPostureProjection> ReadWithLifecycleAsync(CapabilityCatalogEntry entry, CapabilityLifecycleState state)
    {
        var lifecycle = new StubCapabilityLifecycleMutationStore
        {
            ReadResult = new CapabilityLifecycleReadResult(CapabilityLifecycleReadStatus.Available, state, [], [], state.Revision, "available")
        };
        var result = await Service(new StubCapabilityPostureCatalogStore { Entries = [entry] }, lifecycle).ReadAsync(entry.Descriptor.Id);
        Assert.Equal(CapabilityPostureReadStatus.Available, result.Status);
        return Assert.IsType<CapabilityPostureProjection>(result.Posture);
    }

    private static CapabilityCatalogReadResult ReadPage(long revision, IReadOnlyList<CapabilityCatalogEntry> entries, string? nextCursor)
    {
        return new CapabilityCatalogReadResult(CapabilityCatalogReadStatus.Available, new CapabilityCatalogPage(revision, entries, nextCursor), "test");
    }
}
