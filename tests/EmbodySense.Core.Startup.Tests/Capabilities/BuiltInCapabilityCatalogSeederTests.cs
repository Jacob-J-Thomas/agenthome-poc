using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Capabilities;

public sealed class BuiltInCapabilityCatalogSeederTests
{
    [Fact]
    public async Task Concurrent_seeders_converge_without_ambient_enablement_trust_assignment_or_authority()
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var seeders = Enumerable.Range(0, 8).Select(_ => new BuiltInCapabilityCatalogSeeder(provider).SeedAsync(paths));

        await Task.WhenAll(seeders);

        var entries = await ReadBuiltInsAsync(new CapabilityCatalogStore(paths, provider));
        Assert.Equal(BuiltInCapabilityCatalog.Descriptors.Count, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.Equal(CapabilityDeclarationState.Declared, entry.Lifecycle.Declaration);
            Assert.Equal(CapabilityInstallationState.Installed, entry.Lifecycle.Installation);
            Assert.Equal(CapabilityEnablementState.Disabled, entry.Lifecycle.Enablement);
            Assert.Equal(CapabilityTrustState.Unverified, entry.Lifecycle.Trust);
        });
        var artifact = await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath);
        Assert.DoesNotContain("assignment", artifact, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authority", artifact, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Seeder_finds_existing_built_ins_beyond_the_first_bounded_page()
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var service = new CapabilityCatalogService(new CapabilityCatalogStore(paths, provider));
        var template = BuiltInCapabilityCatalog.Descriptors[0];
        var revision = 0L;
        for (var index = 0; index <= CapabilityCatalogLimits.MaximumPageSize; index++)
        {
            _ = CapabilityId.TryParse($"aaa.example/capability-{index:D3}", out var id, out _);
            var result = await service.DeclareAsync(template with { Id = id! }, revision, $"declare-prefill-{index:D3}");
            Assert.Equal(CapabilityCatalogMutationStatus.Applied, result.Status);
            revision = result.CatalogRevision!.Value;
        }

        await new BuiltInCapabilityCatalogSeeder(provider).SeedAsync(paths);
        var firstArtifact = await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath);
        await new BuiltInCapabilityCatalogSeeder(provider).SeedAsync(paths);

        Assert.Equal(firstArtifact, await File.ReadAllTextAsync(paths.CapabilityCatalogDocumentPath));
        var builtIns = await ReadBuiltInsAsync(new CapabilityCatalogStore(paths, provider));
        Assert.Equal(BuiltInCapabilityCatalog.Descriptors.Count, builtIns.Count);
        Assert.All(builtIns, entry =>
        {
            Assert.Equal(CapabilityInstallationState.Installed, entry.Lifecycle.Installation);
            Assert.Equal(CapabilityEnablementState.Disabled, entry.Lifecycle.Enablement);
            Assert.Equal(CapabilityTrustState.Unverified, entry.Lifecycle.Trust);
        });
    }

    private static async Task<IReadOnlyList<CapabilityCatalogEntry>> ReadBuiltInsAsync(CapabilityCatalogStore store)
    {
        var entries = new List<CapabilityCatalogEntry>();
        string? cursor = null;
        do
        {
            var read = await store.ReadAsync(cursor, CapabilityCatalogLimits.MaximumPageSize);
            Assert.Equal(CapabilityCatalogReadStatus.Available, read.Status);
            entries.AddRange(read.Page!.Entries.Where(entry => entry.Descriptor.Id.Value.StartsWith("org.embodysense/", StringComparison.Ordinal)));
            cursor = read.Page.NextCursor;
        }
        while (cursor is not null);

        return entries;
    }
}
