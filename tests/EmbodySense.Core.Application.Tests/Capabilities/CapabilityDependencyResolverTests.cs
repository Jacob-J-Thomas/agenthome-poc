using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Capabilities;

public sealed class CapabilityDependencyResolverTests
{
    [Fact]
    public void Resolver_selects_highest_verified_exact_pin_independently_of_candidate_order()
    {
        var manifest = Manifest("org.example/skill", [Dependency("org.example/a", "[1.0.0,3.0.0)")]);
        var first = Candidate("org.example/a", "1.0.0");
        var second = Candidate("org.example/a", "2.0.0");
        var resolver = new CapabilityDependencyResolver();

        var forward = resolver.Resolve(manifest, [first, second]);
        var reverse = resolver.Resolve(manifest, [second, first]);

        Assert.True(forward.IsResolved);
        Assert.True(reverse.IsResolved);
        Assert.Equal("2.0.0", Assert.Single(forward.Selected).DescriptorIdentity.Version.Value);
        Assert.Equal(forward.Selected.Select(item => item.DescriptorIdentity.Hash.Value), reverse.Selected.Select(item => item.DescriptorIdentity.Hash.Value));
        Assert.Equal(forward.Evidence.Select(item => (item.DependencyId.Value, item.Outcome, item.Pin?.DescriptorIdentity.Hash.Value)), reverse.Evidence.Select(item => (item.DependencyId.Value, item.Outcome, item.Pin?.DescriptorIdentity.Hash.Value)));

        var random = new Random(1729);
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var shuffled = new[] { first, second }.OrderBy(_ => random.Next()).ToArray();
            var result = resolver.Resolve(manifest, shuffled);
            Assert.Equal(forward.Selected.Select(item => item.DescriptorIdentity.Hash.Value), result.Selected.Select(item => item.DescriptorIdentity.Hash.Value));
            Assert.Equal(forward.Evidence.Select(item => (item.DependencyId.Value, item.Outcome, item.Pin?.DescriptorIdentity.Hash.Value)), result.Evidence.Select(item => (item.DependencyId.Value, item.Outcome, item.Pin?.DescriptorIdentity.Hash.Value)));
        }
    }

    [Fact]
    public void Resolver_visibly_omits_missing_optional_without_hiding_missing_required()
    {
        var resolver = new CapabilityDependencyResolver();
        var manifest = Manifest("org.example/skill", [Dependency("org.example/required", "*")], [Dependency("org.example/optional", "*")]);

        var result = resolver.Resolve(manifest, []);

        Assert.False(result.IsResolved);
        Assert.Contains(result.Evidence, item => item.DependencyId.Value == "org.example/required" && item.Outcome == CapabilityDependencyResolutionOutcome.Missing);
        Assert.Contains(result.Evidence, item => item.DependencyId.Value == "org.example/optional" && item.Outcome == CapabilityDependencyResolutionOutcome.OmittedOptional);
    }

    [Fact]
    public void Resolver_fails_closed_for_cycles_untrusted_candidates_and_provenance_conflicts()
    {
        var cycleA = Manifest("org.example/a", [Dependency("org.example/b", "*")]);
        var cycleB = Manifest("org.example/b", [Dependency("org.example/a", "*")]);
        var cycle = new CapabilityDependencyResolver().Resolve(Manifest("org.example/skill", [Dependency("org.example/a", "*")]), [Candidate("org.example/a", "1.0.0", cycleA), Candidate("org.example/b", "1.0.0", cycleB)]);
        var untrusted = new CapabilityDependencyResolver().Resolve(Manifest("org.example/skill", [Dependency("org.example/a", "*")]), [Candidate("org.example/a", "1.0.0", trust: CapabilityTrustState.Unverified)]);
        var conflict = new CapabilityDependencyResolver().Resolve(Manifest("org.example/skill", [Dependency("org.example/a", "*")]), [Candidate("org.example/a", "1.0.0", source: "file:///a"), Candidate("org.example/a", "1.0.0", source: "file:///b")]);

        Assert.Contains(cycle.Evidence, item => item.Outcome == CapabilityDependencyResolutionOutcome.Cyclic);
        Assert.Contains(untrusted.Evidence, item => item.Outcome == CapabilityDependencyResolutionOutcome.Untrusted);
        Assert.Contains(conflict.Evidence, item => item.Outcome == CapabilityDependencyResolutionOutcome.Conflict);
        Assert.False(cycle.IsResolved);
        Assert.False(untrusted.IsResolved);
        Assert.False(conflict.IsResolved);
    }

    [Theory]
    [InlineData(CapabilityHealthState.Unavailable)]
    [InlineData(CapabilityHealthState.Unknown)]
    public void Resolver_fails_closed_with_structured_evidence_for_unacceptable_candidate_health(CapabilityHealthState health)
    {
        var result = new CapabilityDependencyResolver().Resolve(Manifest("org.example/skill", [Dependency("org.example/a", "*")]), [Candidate("org.example/a", "1.0.0", health: health)]);

        Assert.False(result.IsResolved);
        Assert.Empty(result.Selected);
        var evidence = Assert.Single(result.Evidence);
        Assert.Equal(CapabilityDependencyResolutionOutcome.Untrusted, evidence.Outcome);
        Assert.Null(evidence.Pin);
    }

    [Fact]
    public void Resolver_accepts_degraded_candidate_health()
    {
        var result = new CapabilityDependencyResolver().Resolve(Manifest("org.example/skill", [Dependency("org.example/a", "*")]), [Candidate("org.example/a", "1.0.0", health: CapabilityHealthState.Degraded)]);

        Assert.True(result.IsResolved);
        Assert.Equal("org.example/a", Assert.Single(result.Selected).DescriptorIdentity.Id.Value);
    }

    [Fact]
    public void Resolver_returns_invalid_evidence_for_a_candidate_with_a_malformed_dependency_manifest()
    {
        var malformed = Manifest("org.example/a", []) with { SubjectId = null! };

        var result = new CapabilityDependencyResolver().Resolve(Manifest("org.example/skill", [Dependency("org.example/a", "*")]), [Candidate("org.example/a", "1.0.0", malformed)]);

        Assert.False(result.IsResolved);
        Assert.Empty(result.Selected);
        var evidence = Assert.Single(result.Evidence);
        Assert.Equal(CapabilityDependencyResolutionOutcome.Invalid, evidence.Outcome);
        Assert.Null(evidence.Pin);
    }

    [Fact]
    public void Resolver_returns_bounded_invalid_evidence_for_a_root_manifest_without_a_subject()
    {
        var malformed = Manifest("org.example/skill", []) with { SubjectId = null! };

        var result = new CapabilityDependencyResolver().Resolve(malformed, []);

        Assert.False(result.IsResolved);
        Assert.Empty(result.Selected);
        var evidence = Assert.Single(result.Evidence);
        Assert.Equal(CapabilityDependencyResolutionOutcome.Invalid, evidence.Outcome);
        Assert.Equal("org.embodysense/invalid-root-manifest", evidence.SubjectId.Value);
        Assert.Equal(evidence.SubjectId, evidence.DependencyId);
        Assert.Null(evidence.Pin);
    }

    [Fact]
    public void Resolver_intersects_transitive_ranges_before_retaining_the_final_exact_pin()
    {
        var requiresBroad = Manifest("org.example/a", [Dependency("org.example/shared", "[1.0.0,3.0.0)")]);
        var requiresNarrow = Manifest("org.example/b", [Dependency("org.example/shared", "[1.0.0,2.0.0)")]);
        var result = new CapabilityDependencyResolver().Resolve(
            Manifest("org.example/skill", [Dependency("org.example/a", "*"), Dependency("org.example/b", "*")]),
            [Candidate("org.example/a", "1.0.0", requiresBroad), Candidate("org.example/b", "1.0.0", requiresNarrow), Candidate("org.example/shared", "1.5.0"), Candidate("org.example/shared", "2.5.0")]);

        Assert.True(result.IsResolved);
        Assert.Equal("1.5.0", result.Selected.Single(item => item.DescriptorIdentity.Id.Value == "org.example/shared").DescriptorIdentity.Version.Value);
    }

    [Fact]
    public void Resolver_recomputes_the_transitive_closure_after_a_later_range_narrows_a_selected_pin()
    {
        var broadParent = Manifest("org.example/a", [Dependency("org.example/shared", "[1.0.0,3.0.0)")]);
        var narrowParent = Manifest("org.example/b", [Dependency("org.example/shared", "[1.0.0,2.0.0)")]);
        var highShared = Manifest("org.example/shared", [Dependency("org.example/y", "*")]);
        var lowShared = Manifest("org.example/shared", [Dependency("org.example/z", "*")]);
        var candidates = new[]
        {
            Candidate("org.example/a", "1.0.0", broadParent),
            Candidate("org.example/b", "1.0.0", narrowParent),
            Candidate("org.example/shared", "2.5.0", highShared),
            Candidate("org.example/shared", "1.5.0", lowShared),
            Candidate("org.example/y", "1.0.0"),
            Candidate("org.example/z", "1.0.0")
        };
        var root = Manifest("org.example/skill", [Dependency("org.example/a", "*"), Dependency("org.example/b", "*")]);
        var expected = new CapabilityDependencyResolver().Resolve(root, candidates);

        Assert.True(expected.IsResolved);
        Assert.Equal("1.5.0", expected.Selected.Single(item => item.DescriptorIdentity.Id.Value == "org.example/shared").DescriptorIdentity.Version.Value);
        Assert.DoesNotContain(expected.Selected, item => item.DescriptorIdentity.Id.Value == "org.example/y");
        Assert.Contains(expected.Selected, item => item.DescriptorIdentity.Id.Value == "org.example/z");
        Assert.DoesNotContain(expected.Evidence, item => item.DependencyId.Value == "org.example/y");

        var random = new Random(314159);
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var actual = new CapabilityDependencyResolver().Resolve(root, candidates.OrderBy(_ => random.Next()).ToArray());

            Assert.Equal(expected.Selected.Select(item => item.DescriptorIdentity.Hash.Value), actual.Selected.Select(item => item.DescriptorIdentity.Hash.Value));
            Assert.Equal(expected.Evidence.Select(item => (item.SubjectId.Value, item.DependencyId.Value, item.Outcome, item.Pin?.DescriptorIdentity.Hash.Value)), actual.Evidence.Select(item => (item.SubjectId.Value, item.DependencyId.Value, item.Outcome, item.Pin?.DescriptorIdentity.Hash.Value)));
        }
    }

    [Fact]
    public void Resolver_treats_maximum_depth_as_the_dependency_edge_bound()
    {
        var depth = new CapabilityDependencyResolver(new CapabilityDependencyResolutionLimits(1, 8, 8));
        var child = Manifest("org.example/a", [Dependency("org.example/b", "*")]);
        var result = depth.Resolve(Manifest("org.example/skill", [Dependency("org.example/a", "*")]), [Candidate("org.example/a", "1.0.0", child), Candidate("org.example/b", "1.0.0")]);

        Assert.False(result.IsResolved);
        var evidence = Assert.Single(result.Evidence, item => item.Outcome == CapabilityDependencyResolutionOutcome.LimitExceeded);
        Assert.Contains("depth", evidence.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Selected, item => item.DescriptorIdentity.Id.Value == "org.example/a");
        Assert.DoesNotContain(result.Selected, item => item.DescriptorIdentity.Id.Value == "org.example/b");
    }

    [Fact]
    public void Resolver_enforces_the_dependency_count_bound_independently_of_depth()
    {
        var resolver = new CapabilityDependencyResolver(new CapabilityDependencyResolutionLimits(8, 1, 8));
        var child = Manifest("org.example/a", [Dependency("org.example/b", "*")]);

        var result = resolver.Resolve(Manifest("org.example/skill", [Dependency("org.example/a", "*")]), [Candidate("org.example/a", "1.0.0", child), Candidate("org.example/b", "1.0.0")]);

        Assert.False(result.IsResolved);
        var evidence = Assert.Single(result.Evidence, item => item.Outcome == CapabilityDependencyResolutionOutcome.LimitExceeded);
        Assert.Contains("count", evidence.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static CapabilityDependencyCatalogCandidate Candidate(string id, string version, CapabilityDependencyManifest? dependencies = null, CapabilityTrustState trust = CapabilityTrustState.Verified, CapabilityHealthState health = CapabilityHealthState.Healthy, string source = "file:///catalog")
    {
        var descriptor = Descriptor(id, version, source);
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _));
        var lifecycle = new CapabilityLifecycleSnapshot(1, identity!, CapabilityDeclarationState.Declared, CapabilityInstallationState.Installed, CapabilityEnablementState.Disabled, health, CapabilityRetirementState.Active, trust);
        return new CapabilityDependencyCatalogCandidate(new CapabilityCatalogEntry(descriptor, lifecycle, 1, DateTimeOffset.UnixEpoch, "test"), dependencies, new CapabilityDependencyArtifactMetadata(null, null));
    }

    private static CapabilityDescriptor Descriptor(string id, string version, string source)
    {
        Assert.True(CapabilityId.TryParse(id, out var capabilityId, out _));
        Assert.True(CapabilityProviderId.TryParse("org.example", out var provider, out _));
        Assert.True(CapabilityVersion.TryParse(version, out var exact, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var range, out _));
        Assert.True(CapabilityJsonSchema.TryCreate($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\"}}", out var schema, out _));
        return new CapabilityDescriptor(1, capabilityId!, CapabilityKind.Skill, exact!, new CapabilityImplementationIdentity(provider!, id[(id.IndexOf('/') + 1)..]), new CapabilityProvenance(CapabilityProvenanceKind.LocalSource, source, null, null), new CapabilityCompatibility(range!, [CapabilityPlatform.Any]), "A test capability.", schema!, schema!, new CapabilityResourceLimits(1, 1, 1, 1), CapabilitySideEffectClass.None, new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], []));
    }

    private static CapabilityDependencyManifest Manifest(string id, IReadOnlyList<CapabilityDependency> required, IReadOnlyList<CapabilityDependency>? optional = null) => new(1, CapabilityDependencyManifestKind.Skill, Id(id), required, optional ?? [], new CapabilityDependencyArtifactMetadata(null, null));

    private static CapabilityDependency Dependency(string id, string range) => new(Id(id), Range(range));

    private static CapabilityId Id(string value)
    {
        Assert.True(CapabilityId.TryParse(value, out var id, out _));
        return id!;
    }

    private static CapabilityVersionRange Range(string value)
    {
        Assert.True(CapabilityVersionRange.TryParse(value, out var range, out _));
        return range!;
    }
}
