using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Capabilities;

public sealed class CapabilityDependentIndexTests
{
    [Fact]
    public async Task Capture_orders_all_registered_domains_and_hashes_the_exact_snapshot()
    {
        var loop = Dependent(CapabilityDependentKind.Loop, "loop-z", CapabilityAuthorityPosture.AssignedDefinition);
        var skill = Dependent(CapabilityDependentKind.Skill, "skill-a", CapabilityAuthorityPosture.MetadataOnly);
        var role = Dependent(CapabilityDependentKind.Role, "role-a", CapabilityAuthorityPosture.AssignedDefinition);
        var schedule = Dependent(CapabilityDependentKind.Schedule, "schedule-a", CapabilityAuthorityPosture.MetadataOnly);
        var first = new CapabilityDependentIndex(
            [new Source("z-source", [loop]), new Source("a-source", [skill])],
            new RoleSource("role-source", [role]),
            new ScheduleSource("schedule-source", [schedule]));
        var second = new CapabilityDependentIndex(
            [new Source("a-source", [skill]), new Source("z-source", [loop])],
            new RoleSource("role-source", [role]),
            new ScheduleSource("schedule-source", [schedule]));

        var captured = await first.CaptureAsync();
        var reordered = await second.CaptureAsync();

        Assert.Equal(CapabilityDependentIndexStatus.Available, captured.Status);
        Assert.StartsWith("sha256:", captured.Hash, StringComparison.Ordinal);
        Assert.Equal(captured.Hash, reordered.Hash);
        Assert.Equal(
            [CapabilityDependentKind.Loop, CapabilityDependentKind.Role, CapabilityDependentKind.Schedule, CapabilityDependentKind.Skill],
            captured.Dependents.Select(item => item.Kind));
        Assert.Equal(["loop-z", "role-a", "schedule-a", "skill-a"], captured.Dependents.Select(item => item.Identity));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("blank")]
    [InlineData("duplicate")]
    public void Constructor_rejects_an_unidentifiable_source_set(string shape)
    {
        IEnumerable<ICapabilityDependentIndexSource> sources = shape switch
        {
            "empty" => [],
            "blank" => [new Source(" ", [])],
            _ => [new Source("same", []), new Source("same", [])],
        };

        Assert.Throws<ArgumentException>(() => new CapabilityDependentIndex(sources));
    }

    [Fact]
    public void Constructor_rejects_a_null_source_collection()
    {
        Assert.Throws<ArgumentNullException>(() => new CapabilityDependentIndex(null!));
    }

    [Fact]
    public async Task Capture_fails_closed_for_null_oversized_cross_domain_and_duplicate_evidence()
    {
        var loop = Dependent(CapabilityDependentKind.Loop, "same", CapabilityAuthorityPosture.AssignedDefinition);
        var role = Dependent(CapabilityDependentKind.Role, "same", CapabilityAuthorityPosture.AssignedDefinition);
        var oversized = Enumerable.Repeat(loop, 2_049).ToArray();
        var cases = new CapabilityDependentIndex[]
        {
            new([new Source("null", null!)]),
            new([new Source("oversized", oversized)]),
            new([new Source("forged-role", [role])]),
            new([new Source("first", [loop]), new Source("second", [loop])]),
        };

        foreach (var index in cases)
        {
            var captured = await index.CaptureAsync();

            Assert.Equal(CapabilityDependentIndexStatus.Unavailable, captured.Status);
            Assert.Empty(captured.Dependents);
            Assert.Empty(captured.Hash);
        }
    }

    [Fact]
    public async Task Capture_maps_an_expected_source_failure_to_unavailable()
    {
        var index = new CapabilityDependentIndex([new Source("unavailable", [], new IOException("offline"))]);

        var captured = await index.CaptureAsync();

        Assert.Equal(CapabilityDependentIndexStatus.Unavailable, captured.Status);
        Assert.Contains("unavailable", captured.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Capture_propagates_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var index = new CapabilityDependentIndex([new Source("cancelled", [])]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.CaptureAsync(cancellation.Token));
    }

    private static CapabilityDependent Dependent(
        CapabilityDependentKind kind,
        string identity,
        CapabilityAuthorityPosture posture)
    {
        Assert.True(CapabilityId.TryParse($"org.example/{identity}", out var subject, out _));
        var manifest = new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subject!,
            [],
            [],
            new CapabilityDependencyArtifactMetadata(null, null));
        return new CapabilityDependent(kind, identity, "revision-1", manifest, posture);
    }

    private class Source(
        string name,
        IReadOnlyList<CapabilityDependent> dependents,
        Exception? failure = null) : ICapabilityDependentIndexSource
    {
        public string Name => name;

        public Task<IReadOnlyList<CapabilityDependent>> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return failure is null
                ? Task.FromResult(dependents)
                : Task.FromException<IReadOnlyList<CapabilityDependent>>(failure);
        }
    }

    private sealed class RoleSource(string name, IReadOnlyList<CapabilityDependent> dependents)
        : Source(name, dependents), IRoleCapabilityDependentIndexSource;

    private sealed class ScheduleSource(string name, IReadOnlyList<CapabilityDependent> dependents)
        : Source(name, dependents), IScheduleCapabilityDependentIndexSource;
}
