using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using System.Text.Json;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Effects;

public sealed class GovernedActuatorCatalogResolverTests
{
    private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-08-12T19:00:00Z");

    [Fact]
    public async Task Exact_active_catalog_and_registry_identity_resolve_the_registered_operation()
    {
        var entry = Entry("org.example/probe");
        var operation = Operation(entry);
        var resolver = Resolver(
            new ScriptedCatalogStore(
            [Page(null, 7, [entry], null), Page(null, 7, [entry], null)]),
            new GovernedActuatorOperationRegistry([operation]));

        var result = await resolver.ResolveAsync(Pin(entry), operation.Descriptor.OperationId);
        var read = await resolver.ReadAsync(8);

        Assert.Equal(GovernedActuatorCatalogResolutionStatus.Active, result.Status);
        Assert.Equal(entry.Descriptor, result.Capability);
        Assert.Equal(operation.Descriptor, result.Descriptor);
        Assert.Same(operation, result.Operation);
        Assert.Equal(GovernedActuatorCatalogReadStatus.Available, read.Status);
        Assert.Equal(operation.Descriptor, Assert.Single(read.Operations));
    }

    [Fact]
    public async Task Repeated_cursor_and_nonterminal_empty_page_fail_closed()
    {
        var entry = Entry("org.example/probe");
        var operation = Operation(entry);
        var repeated = Resolver(
            new ScriptedCatalogStore(
            [
                Page(null, 7, [entry], entry.Descriptor.Id.Value),
                Page(entry.Descriptor.Id.Value, 7, [Entry("org.example/zeta")], entry.Descriptor.Id.Value),
            ]),
            new GovernedActuatorOperationRegistry([operation]));
        var empty = Resolver(
            new ScriptedCatalogStore(
            [Page(null, 7, [], "org.example/probe")]),
            new GovernedActuatorOperationRegistry([operation]));

        Assert.Equal(
            GovernedActuatorCatalogResolutionStatus.CatalogAmbiguous,
            (await repeated.ResolveAsync(Pin(entry), operation.Descriptor.OperationId)).Status);
        Assert.Equal(
            GovernedActuatorCatalogResolutionStatus.CatalogAmbiguous,
            (await empty.ResolveAsync(Pin(entry), operation.Descriptor.OperationId)).Status);
    }

    [Fact]
    public async Task Revision_drift_and_duplicate_ids_across_pages_fail_closed()
    {
        var first = Entry("org.example/alpha");
        var target = Entry("org.example/probe");
        var operation = Operation(target);
        var cursor = first.Descriptor.Id.Value;
        var drift = Resolver(
            new ScriptedCatalogStore(
            [Page(null, 7, [first], cursor), Page(cursor, 8, [target], null)]),
            new GovernedActuatorOperationRegistry([operation]));
        var repeated = Resolver(
            new ScriptedCatalogStore(
            [Page(null, 7, [first], cursor), Page(cursor, 7, [first], null)]),
            new GovernedActuatorOperationRegistry([operation]));

        Assert.Equal(GovernedActuatorCatalogResolutionStatus.CatalogAmbiguous, (await drift.ResolveAsync(Pin(target), operation.Descriptor.OperationId)).Status);
        Assert.Equal(GovernedActuatorCatalogResolutionStatus.CatalogAmbiguous, (await repeated.ResolveAsync(Pin(target), operation.Descriptor.OperationId)).Status);
    }

    [Fact]
    public async Task Duplicate_ids_within_page_and_malformed_ordering_fail_closed()
    {
        var alpha = Entry("org.example/alpha");
        var target = Entry("org.example/probe");
        var operation = Operation(target);
        var duplicates = Resolver(
            new ScriptedCatalogStore([Page(null, 7, [target, target], null)]),
            new GovernedActuatorOperationRegistry([operation]));
        var outOfOrder = Resolver(
            new ScriptedCatalogStore([Page(null, 7, [target, alpha], null)]),
            new GovernedActuatorOperationRegistry([operation]));

        Assert.Equal(GovernedActuatorCatalogResolutionStatus.CatalogAmbiguous, (await duplicates.ResolveAsync(Pin(target), operation.Descriptor.OperationId)).Status);
        Assert.Equal(GovernedActuatorCatalogResolutionStatus.CatalogAmbiguous, (await outOfOrder.ResolveAsync(Pin(target), operation.Descriptor.OperationId)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Null_or_throwing_catalog_port_fails_closed_without_exposing_dependency_detail(bool throws)
    {
        var entry = Entry("org.example/probe");
        var operation = Operation(entry);
        var resolver = Resolver(
            new HostileCatalogStore(throws),
            new GovernedActuatorOperationRegistry([operation]));

        var resolution = await resolver.ResolveAsync(Pin(entry), operation.Descriptor.OperationId);
        var read = await resolver.ReadAsync(8);

        Assert.Equal(GovernedActuatorCatalogResolutionStatus.CatalogUnavailable, resolution.Status);
        Assert.Equal(GovernedActuatorCatalogReadStatus.Unavailable, read.Status);
        Assert.DoesNotContain("secret-canary", resolution.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-canary", read.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Throwing_registry_snapshot_fails_closed_without_reading_catalog()
    {
        var entry = Entry("org.example/probe");
        var store = new CountingCatalogStore(Page(null, 7, [entry], null));
        var resolver = Resolver(store, new HostileRegistry());

        var resolution = await resolver.ResolveAsync(Pin(entry), "probe/observe");
        var read = await resolver.ReadAsync(8);

        Assert.Equal(GovernedActuatorCatalogResolutionStatus.CatalogUnavailable, resolution.Status);
        Assert.Equal(GovernedActuatorCatalogReadStatus.Unavailable, read.Status);
        Assert.Equal(0, store.ReadCalls);
    }

    [Fact]
    public async Task Oversized_registry_snapshot_fails_closed_before_catalog_or_resolution_access()
    {
        var entry = Entry("org.example/probe");
        var operation = Operation(entry);
        var store = new CountingCatalogStore(Page(null, 7, [entry], null));
        var registry = new TrackingRegistry(Enumerable.Repeat(operation.Descriptor, 257).ToArray());
        var resolver = Resolver(store, registry);

        var resolution = await resolver.ResolveAsync(Pin(entry), operation.Descriptor.OperationId);
        var read = await resolver.ReadAsync(8);

        Assert.Equal(GovernedActuatorCatalogResolutionStatus.CatalogUnavailable, resolution.Status);
        Assert.Equal(GovernedActuatorCatalogReadStatus.Unavailable, read.Status);
        Assert.Equal(0, store.ReadCalls);
        Assert.Equal(0, registry.TryResolveCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Incoherent_or_throwing_registry_snapshot_fails_closed_before_protected_ports(bool throws)
    {
        var entry = Entry("org.example/probe");
        var operation = Operation(entry);
        var store = new CountingCatalogStore(Page(null, 7, [entry], null));
        var descriptors = new HostileReadOnlyList<GovernedActuatorOperationDescriptor>(
            1,
            Enumerable.Repeat(operation.Descriptor, 257).ToArray(),
            throws);
        var registry = new TrackingRegistry(descriptors);
        var resolver = Resolver(store, registry);

        var resolution = await resolver.ResolveAsync(Pin(entry), operation.Descriptor.OperationId);
        var read = await resolver.ReadAsync(8);

        Assert.Equal(GovernedActuatorCatalogResolutionStatus.CatalogUnavailable, resolution.Status);
        Assert.Equal(GovernedActuatorCatalogReadStatus.Unavailable, read.Status);
        Assert.Equal(0, store.ReadCalls);
        Assert.Equal(0, registry.TryResolveCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Oversized_incoherent_or_throwing_catalog_page_fails_closed_with_bounded_evidence(int mode)
    {
        var entry = Entry("org.example/probe");
        var operation = Operation(entry);
        var repeated = Enumerable.Repeat(entry, 101).ToArray();
        IReadOnlyList<CapabilityCatalogEntry> entries = mode switch
        {
            0 => repeated,
            1 => new HostileReadOnlyList<CapabilityCatalogEntry>(1, repeated, false),
            _ => new HostileReadOnlyList<CapabilityCatalogEntry>(1, [entry], true),
        };
        var store = new DeferredPageCatalogStore(entries);
        var resolver = Resolver(store, new GovernedActuatorOperationRegistry([operation]));

        var resolution = await resolver.ResolveAsync(Pin(entry), operation.Descriptor.OperationId);
        var read = await resolver.ReadAsync(8);

        Assert.Equal(GovernedActuatorCatalogResolutionStatus.CatalogUnavailable, resolution.Status);
        Assert.Equal(GovernedActuatorCatalogReadStatus.Unavailable, read.Status);
        Assert.DoesNotContain("secret-canary", resolution.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-canary", read.Detail, StringComparison.Ordinal);
        Assert.Equal(2, store.ReadCalls);
    }

    [Fact]
    public async Task Malformed_pin_provenance_is_rejected_before_catalog_access()
    {
        var entry = Entry("org.example/probe");
        var operation = Operation(entry);
        var store = new CountingCatalogStore(Page(null, 7, [entry], null));
        var resolver = Resolver(store, new GovernedActuatorOperationRegistry([operation]));
        var malformed = Pin(entry) with { Provenance = null! };

        var result = await resolver.ResolveAsync(malformed, operation.Descriptor.OperationId);

        Assert.Equal(GovernedActuatorCatalogResolutionStatus.InvalidRequest, result.Status);
        Assert.Equal(0, store.ReadCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Public_pin_transport_rejects_valid_length_but_noncanonical_integrity_or_checksum(int field)
    {
        var entry = Entry("org.example/probe");
        var provenanceDigest = CapabilityIntegrityDigest.Compute("remote-provenance"u8);
        var artifactChecksum = CapabilityIntegrityDigest.Compute("package-checksum"u8);
        var pin = Pin(entry) with
        {
            Provenance = new CapabilityProvenance(
                CapabilityProvenanceKind.RemoteArtifact,
                "https://example.test/effects/probe-package",
                "revision-remote",
                provenanceDigest),
            Artifact = new CapabilityDependencyArtifactMetadata(artifactChecksum, "signature-evidence"),
        };
        var canonical = JsonSerializer.Serialize(pin);
        var selected = field == 0 ? provenanceDigest.Value : artifactChecksum.Value;
        var noncanonical = "sha256:" + selected["sha256:".Length..].ToUpperInvariant();
        var hostile = canonical.Replace(selected, noncanonical, StringComparison.Ordinal);

        Assert.NotEqual(canonical, hostile);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CapabilityAdmissionPin>(hostile));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Canonical_but_mismatched_parsed_identity_never_reaches_catalog_or_adapter_resolution(int field)
    {
        var entry = Entry("org.example/probe");
        var operation = Operation(entry);
        var original = Pin(entry);
        Assert.True(CapabilityId.TryParse("org.example/other", out var otherId, out _));
        Assert.True(CapabilityVersion.TryParse("2.0.0", out var otherVersion, out _));
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + new string('f', 64), out var otherHash, out _));
        var identity = field switch
        {
            0 => original.DescriptorIdentity with { Id = otherId! },
            1 => original.DescriptorIdentity with { Version = otherVersion! },
            _ => original.DescriptorIdentity with { Hash = otherHash! },
        };
        var pin = original with { DescriptorIdentity = identity };
        var store = new CountingCatalogStore(Page(null, 7, [entry], null));
        var registry = new TrackingRegistry([operation.Descriptor]);

        var result = await Resolver(store, registry).ResolveAsync(pin, operation.Descriptor.OperationId);

        Assert.Equal(GovernedActuatorCatalogResolutionStatus.OperationUnregistered, result.Status);
        Assert.Equal(0, store.ReadCalls);
        Assert.Equal(0, registry.TryResolveCalls);
    }

    [Fact]
    public void Registry_captures_descriptor_once_and_never_rereads_a_mutable_adapter_property()
    {
        var entry = Entry("org.example/probe");
        var descriptor = Operation(entry).Descriptor;
        var operation = new ReadOnceActuatorOperation(descriptor);

        var registry = new GovernedActuatorOperationRegistry([operation]);
        var captured = Assert.Single(registry.Descriptors);
        var resolved = registry.TryResolve(captured, out var exact);

        Assert.True(resolved);
        Assert.Same(operation, exact);
        Assert.Equal(1, operation.DescriptorReads);
        Assert.Equal(descriptor, captured);
    }

    [Fact]
    public void Any_platform_is_rejected_for_exact_actuator_resolution()
    {
        var entry = Entry("org.example/probe");
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var hostVersion, out _));

        Assert.Throws<ArgumentException>(() => new GovernedActuatorCatalogResolver(
            new CountingCatalogStore(Page(null, 7, [entry], null)),
            new GovernedActuatorOperationRegistry([Operation(entry)]),
            hostVersion!,
            CapabilityPlatform.Any));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public async Task Invalid_catalog_read_bound_is_rejected_without_accessing_protected_ports(int maximumCount)
    {
        var entry = Entry("org.example/probe");
        var store = new CountingCatalogStore(Page(null, 7, [entry], null));
        var resolver = Resolver(store, new GovernedActuatorOperationRegistry([Operation(entry)]));

        var result = await resolver.ReadAsync(maximumCount);

        Assert.Equal(GovernedActuatorCatalogReadStatus.InvalidRequest, result.Status);
        Assert.Equal(0, store.ReadCalls);
    }

    [Fact]
    public async Task Inactive_and_missing_catalog_entries_are_not_resolvable()
    {
        var entry = Entry("org.example/probe");
        var operation = Operation(entry);
        var missing = Entry("org.example/other");
        var inactive = entry with
        {
            Lifecycle = entry.Lifecycle with { Enablement = CapabilityEnablementState.Disabled },
        };
        var inactiveResult = await Resolver(
            new ScriptedCatalogStore([Page(null, 7, [inactive], null)]),
            new GovernedActuatorOperationRegistry([operation])).ResolveAsync(Pin(entry), operation.Descriptor.OperationId);
        var missingResult = await Resolver(
            new ScriptedCatalogStore([Page(null, 7, [missing], null)]),
            new GovernedActuatorOperationRegistry([operation])).ResolveAsync(Pin(entry), operation.Descriptor.OperationId);

        Assert.Equal(GovernedActuatorCatalogResolutionStatus.PinInactive, inactiveResult.Status);
        Assert.Equal(GovernedActuatorCatalogResolutionStatus.PinMissing, missingResult.Status);
    }

    [Fact]
    public async Task Cancelled_catalog_reads_are_not_translated_into_dependency_failures()
    {
        var entry = Entry("org.example/probe");
        var operation = Operation(entry);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var resolver = Resolver(
            new ScriptedCatalogStore([Page(null, 7, [entry], null)]),
            new GovernedActuatorOperationRegistry([operation]));

        await Assert.ThrowsAsync<OperationCanceledException>(() => resolver.ResolveAsync(Pin(entry), operation.Descriptor.OperationId, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => resolver.ReadAsync(8, cancellation.Token));
    }

    [Theory]
    [InlineData("https://example.test/effects/probe?query=secret", "provenance")]
    [InlineData("../probe", "implementation")]
    public async Task Noncanonical_pin_transport_values_are_rejected_before_catalog_access(string value, string field)
    {
        var entry = Entry("org.example/probe");
        var operation = Operation(entry);
        var store = new CountingCatalogStore(Page(null, 7, [entry], null));
        var resolver = Resolver(store, new GovernedActuatorOperationRegistry([operation]));
        var malformed = field == "provenance"
            ? Pin(entry) with { Provenance = Pin(entry).Provenance with { SourceUri = value } }
            : Pin(entry) with { Implementation = Pin(entry).Implementation with { ImplementationId = value } };

        var result = await resolver.ResolveAsync(malformed, operation.Descriptor.OperationId);

        Assert.Equal(GovernedActuatorCatalogResolutionStatus.InvalidRequest, result.Status);
        Assert.Equal(0, store.ReadCalls);
    }

    [Fact]
    public async Task Registry_key_collisions_and_malformed_page_shapes_fail_closed()
    {
        var entry = Entry("org.example/probe");
        var operation = Operation(entry);
        var duplicateRegistry = new TrackingRegistry([operation.Descriptor, operation.Descriptor]);
        var registryResult = await Resolver(
            new CountingCatalogStore(Page(null, 7, [entry], null)),
            duplicateRegistry).ResolveAsync(Pin(entry), operation.Descriptor.OperationId);
        Assert.Equal(GovernedActuatorCatalogResolutionStatus.CatalogUnavailable, registryResult.Status);
    }

    [Fact]
    public void Registry_rejects_null_oversized_malformed_and_duplicate_registrations()
    {
        var entry = Entry("org.example/probe");
        var operation = Operation(entry);

        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedActuatorOperationRegistry([null!]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedActuatorOperationRegistry(Enumerable.Repeat<IGovernedActuatorOperation>(operation, 257)));
        Assert.Throws<ArgumentException>(() => new GovernedActuatorOperationRegistry([operation, operation]));
        Assert.Throws<ArgumentException>(() => new GovernedActuatorOperationRegistry([
            new StubActuatorOperation(operation.Descriptor with { ContentHash = new string('a', 64) })]));

        var registry = new GovernedActuatorOperationRegistry([operation]);
        Assert.False(registry.TryResolve(operation.Descriptor with { ContentHash = new string('a', 64) }, out _));
    }

    [Fact]
    public async Task Null_registry_snapshot_fails_closed_before_catalog_access()
    {
        var entry = Entry("org.example/probe");
        var store = new CountingCatalogStore(Page(null, 7, [entry], null));
        var resolver = Resolver(store, new NullRegistry());

        var resolution = await resolver.ResolveAsync(Pin(entry), "probe/observe");
        var read = await resolver.ReadAsync(8);

        Assert.Equal(GovernedActuatorCatalogResolutionStatus.CatalogUnavailable, resolution.Status);
        Assert.Equal(GovernedActuatorCatalogReadStatus.Unavailable, read.Status);
        Assert.Equal(0, store.ReadCalls);
    }

    private static GovernedActuatorCatalogResolver Resolver(
        ICapabilityCatalogStore store,
        IGovernedActuatorOperationRegistry registry)
    {
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var hostVersion, out var versionError), versionError?.Message);
        Assert.True(CapabilityPlatform.TryParse("linux/x64", out var hostPlatform, out var platformError), platformError?.Message);
        return new GovernedActuatorCatalogResolver(store, registry, hostVersion!, hostPlatform!);
    }

    private static CapabilityCatalogReadResult Page(
        string? expectedCursor,
        long revision,
        IReadOnlyList<CapabilityCatalogEntry> entries,
        string? nextCursor)
        => new(
            CapabilityCatalogReadStatus.Available,
            new CapabilityCatalogPage(revision, entries, nextCursor),
            expectedCursor ?? "initial");

    private static CapabilityCatalogEntry Entry(string capabilityId)
    {
        Assert.True(CapabilityId.TryParse(capabilityId, out var id, out var idError), idError?.Message);
        Assert.True(CapabilityProviderId.TryParse("org.example", out var provider, out var providerError), providerError?.Message);
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var version, out var versionError), versionError?.Message);
        Assert.True(CapabilityVersionRange.TryParse("[1.0.0,2.0.0)", out var hostRange, out var rangeError), rangeError?.Message);
        Assert.True(CapabilityPlatform.TryParse("linux/x64", out var platform, out var platformError), platformError?.Message);
        Assert.True(CapabilityJsonSchema.TryCreate($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\"}}", out var schema, out var schemaError), schemaError?.Message);
        var descriptor = new CapabilityDescriptor(
            CapabilityDescriptor.CurrentSchemaVersion,
            id!,
            CapabilityKind.Actuator,
            version!,
            new CapabilityImplementationIdentity(provider!, "effects/probe"),
            new CapabilityProvenance(CapabilityProvenanceKind.BuiltIn, "https://example.test/effects/probe", "revision-1", null),
            new CapabilityCompatibility(hostRange!, [platform!]),
            "Deterministic public test actuator.",
            schema!,
            schema!,
            new CapabilityResourceLimits(1_000, 1_024, 1_024, 1),
            CapabilitySideEffectClass.LocalReversible,
            new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], []));
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out var identityError), identityError.Errors.FirstOrDefault()?.Message);
        var lifecycle = new CapabilityLifecycleSnapshot(
            CapabilityLifecycleSnapshot.CurrentSchemaVersion,
            identity!,
            CapabilityDeclarationState.Declared,
            CapabilityInstallationState.Installed,
            CapabilityEnablementState.Enabled,
            CapabilityHealthState.Healthy,
            CapabilityRetirementState.Active,
            CapabilityTrustState.Verified);
        return new CapabilityCatalogEntry(descriptor, lifecycle, 1, _now, "test-activate");
    }

    private static CapabilityAdmissionPin Pin(CapabilityCatalogEntry entry)
        => new(
            entry.Lifecycle.DescriptorIdentity,
            entry.Descriptor.Kind,
            entry.Descriptor.Implementation,
            entry.Descriptor.Provenance,
            new CapabilityDependencyArtifactMetadata(null, null),
            entry.Descriptor.Purpose);

    private static StubActuatorOperation Operation(CapabilityCatalogEntry entry)
        => new(GovernedActuatorOperationContract.Create(
            1,
            entry.Lifecycle.DescriptorIdentity,
            entry.Descriptor.Implementation,
            "probe/observe",
            "Produces deterministic value-free test evidence.",
            GovernedActuatorTargetSemantics.ExactOpaqueFingerprint,
            GovernedActuatorIdempotencyPosture.StableOperationIdentity,
            requiresOptimisticPrecondition: false,
            GovernedActuatorApprovalPosture.AuthorityOnly,
            unattendedEligible: true,
            GovernedActuatorCancellationPosture.BeforeBoundaryOnly,
            GovernedActuatorAmbiguityPosture.ReconciliationRequired,
            requiresBeforeEvidence: false,
            requiresAfterEvidence: false,
            requiresOutcomeEvidence: true));

    private sealed class ScriptedCatalogStore(IReadOnlyList<CapabilityCatalogReadResult> results) : ICapabilityCatalogStore
    {
        private int _index;

        public Task<CapabilityCatalogReadResult> ReadAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_index >= results.Count)
            {
                return Task.FromResult(new CapabilityCatalogReadResult(CapabilityCatalogReadStatus.Unavailable, null, "Unexpected read."));
            }
            var result = results[_index++];
            Assert.Equal(result.Detail == "initial" ? null : result.Detail, startAfterId);
            Assert.Equal(100, maximumCount);
            return Task.FromResult(result);
        }

        public Task<CapabilityCatalogMutationResult> MutateAsync(CapabilityCatalogMutation mutation, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class HostileCatalogStore(bool throws) : ICapabilityCatalogStore
    {
        public Task<CapabilityCatalogReadResult> ReadAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default)
            => throws
                ? throw new InvalidOperationException("secret-canary-catalog-port")
                : Task.FromResult<CapabilityCatalogReadResult>(null!);

        public Task<CapabilityCatalogMutationResult> MutateAsync(CapabilityCatalogMutation mutation, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class CountingCatalogStore(CapabilityCatalogReadResult result) : ICapabilityCatalogStore
    {
        internal int ReadCalls { get; private set; }

        public Task<CapabilityCatalogReadResult> ReadAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return Task.FromResult(result);
        }

        public Task<CapabilityCatalogMutationResult> MutateAsync(CapabilityCatalogMutation mutation, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class DeferredPageCatalogStore(IReadOnlyList<CapabilityCatalogEntry> entries) : ICapabilityCatalogStore
    {
        internal int ReadCalls { get; private set; }

        public Task<CapabilityCatalogReadResult> ReadAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return Task.FromResult(new CapabilityCatalogReadResult(
                CapabilityCatalogReadStatus.Available,
                new CapabilityCatalogPage(7, entries, null),
                "available"));
        }

        public Task<CapabilityCatalogMutationResult> MutateAsync(CapabilityCatalogMutation mutation, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class HostileRegistry : IGovernedActuatorOperationRegistry
    {
        public IReadOnlyList<GovernedActuatorOperationDescriptor> Descriptors
            => throw new InvalidOperationException("secret-canary-registry-port");

        public bool TryResolve(GovernedActuatorOperationDescriptor descriptor, out IGovernedActuatorOperation? operation)
            => throw new InvalidOperationException("secret-canary-registry-port");
    }

    private sealed class TrackingRegistry(IReadOnlyList<GovernedActuatorOperationDescriptor> descriptors) : IGovernedActuatorOperationRegistry
    {
        public IReadOnlyList<GovernedActuatorOperationDescriptor> Descriptors { get; } = descriptors;

        internal int TryResolveCalls { get; private set; }

        public bool TryResolve(GovernedActuatorOperationDescriptor descriptor, out IGovernedActuatorOperation? operation)
        {
            TryResolveCalls++;
            operation = null;
            return false;
        }
    }

    private sealed class HostileReadOnlyList<T>(int declaredCount, IReadOnlyList<T> items, bool throws) : IReadOnlyList<T>
    {
        public int Count => declaredCount;

        public T this[int index] => items[index];

        public IEnumerator<T> GetEnumerator()
            => throws
                ? throw new InvalidOperationException("secret-canary-list-enumeration")
                : items.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class StubActuatorOperation(GovernedActuatorOperationDescriptor descriptor) : IGovernedActuatorOperation
    {
        public GovernedActuatorOperationDescriptor Descriptor { get; } = descriptor;

        public string? ValidateInput(GovernedActuatorInputEvidence input) => null;

        public Task<GovernedActuatorPreparationEvidence?> PrepareAsync(
            GovernedActuatorInputEvidence input,
            CancellationToken cancellationToken = default)
            => Task.FromResult<GovernedActuatorPreparationEvidence?>(new(new string('1', 64), null, null));

        public Task<GovernedActuatorAdapterResult> ExecuteAsync(
            GovernedActuatorInvocation invocation,
            IGovernedActuatorDispatchBoundary dispatchBoundary,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.DispatchNotStarted, null));
    }

    private sealed class ReadOnceActuatorOperation(GovernedActuatorOperationDescriptor descriptor) : IGovernedActuatorOperation
    {
        internal int DescriptorReads { get; private set; }

        public GovernedActuatorOperationDescriptor Descriptor
            => ++DescriptorReads == 1
                ? descriptor
                : throw new InvalidOperationException("secret-canary-changing-descriptor");

        public string? ValidateInput(GovernedActuatorInputEvidence input) => null;

        public Task<GovernedActuatorPreparationEvidence?> PrepareAsync(
            GovernedActuatorInputEvidence input,
            CancellationToken cancellationToken = default)
            => Task.FromResult<GovernedActuatorPreparationEvidence?>(new(new string('1', 64), null, null));

        public Task<GovernedActuatorAdapterResult> ExecuteAsync(
            GovernedActuatorInvocation invocation,
            IGovernedActuatorDispatchBoundary dispatchBoundary,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.DispatchNotStarted, null));
    }
}
