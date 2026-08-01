using System.Text;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

public sealed class FileCapabilityCatalogTrustProviderTests
{
    [Fact]
    public async Task Provider_read_of_an_existing_empty_root_is_absent_without_initializing_trust()
    {
        using var trustRoot = new TestWorkspace();
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);

        Assert.Null(await provider.ReadAsync(Identity("empty-root")));
        Assert.False(File.Exists(provider.AuthenticationKeyPath));
        Assert.False(Directory.Exists(provider.AnchorsPath));
    }

    [Fact]
    public void File_backed_catalog_rejects_equal_or_nested_workspace_and_trust_roots_before_use()
    {
        using var root = new TestWorkspace();
        var equalProvider = new FileCapabilityCatalogTrustProvider(root.RootPath);
        Assert.Throws<InvalidOperationException>(() => new CapabilityCatalogStore(new WorkspacePaths(root.RootPath), equalProvider));

        var workspaceRoot = root.File("workspace");
        var nestedTrustRoot = Path.Combine(workspaceRoot, "server-trust");
        var nestedTrustProvider = new FileCapabilityCatalogTrustProvider(Path.Combine(workspaceRoot, ".", "server-trust"));
        Assert.Throws<InvalidOperationException>(() => new CapabilityCatalogStore(new WorkspacePaths(workspaceRoot), nestedTrustProvider));

        var outerTrustProvider = new FileCapabilityCatalogTrustProvider(root.RootPath);
        Assert.Throws<InvalidOperationException>(() => new CapabilityCatalogStore(new WorkspacePaths(root.File("governed", "workspace")), outerTrustProvider));
        Assert.False(File.Exists(equalProvider.AuthenticationKeyPath));
        Assert.False(File.Exists(Path.Combine(nestedTrustRoot, "capability-catalog-root.key")));
    }

    [Fact]
    public void File_backed_catalog_rejects_overlap_through_a_delegating_trust_provider()
    {
        using var root = new TestWorkspace();
        var wrapped = new FailingCapabilityCatalogTrustProvider(new FileCapabilityCatalogTrustProvider(root.RootPath));

        Assert.Throws<InvalidOperationException>(() => new CapabilityCatalogStore(new WorkspacePaths(root.RootPath), wrapped));
        Assert.False(File.Exists(Path.Combine(root.RootPath, "capability-catalog-root.key")));
    }

    [Fact]
    public void File_backed_catalog_rejects_Windows_case_and_extended_path_aliases()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TestWorkspace();
        var upperCaseProvider = new FileCapabilityCatalogTrustProvider(root.RootPath.ToUpperInvariant());
        Assert.Throws<InvalidOperationException>(() => new CapabilityCatalogStore(new WorkspacePaths(root.RootPath.ToLowerInvariant()), upperCaseProvider));

        var extendedProvider = new FileCapabilityCatalogTrustProvider(@"\\?\" + root.RootPath);
        Assert.Throws<InvalidOperationException>(() => new CapabilityCatalogStore(new WorkspacePaths(root.RootPath), extendedProvider));
    }

    [Fact]
    public void File_backed_catalog_resolves_existing_directory_links_before_topology_comparison()
    {
        using var root = new TestWorkspace();
        var workspaceRoot = root.File("workspace");
        var linkedTrustTarget = Path.Combine(workspaceRoot, "trust-target");
        var trustAlias = root.File("trust-alias");
        Directory.CreateDirectory(linkedTrustTarget);
        try
        {
            Directory.CreateSymbolicLink(trustAlias, linkedTrustTarget);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var provider = new FileCapabilityCatalogTrustProvider(trustAlias);
        Assert.Throws<InvalidOperationException>(() => new CapabilityCatalogStore(new WorkspacePaths(workspaceRoot), provider));
        Assert.False(File.Exists(Path.Combine(linkedTrustTarget, "capability-catalog-root.key")));
    }

    [Fact]
    public async Task File_backed_catalog_accepts_disjoint_sibling_roots()
    {
        using var root = new TestWorkspace();
        var workspaceRoot = root.File("workspace");
        var trustRoot = root.File("server-trust");
        Directory.CreateDirectory(workspaceRoot);
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot);
        var store = new CapabilityCatalogStore(new WorkspacePaths(workspaceRoot), provider);

        var result = await store.ReadAsync(null, 1);

        Assert.Equal(CapabilityCatalogReadStatus.Available, result.Status);
    }

    [Fact]
    public async Task Provider_enforces_initialization_successor_and_compare_exchange_contracts()
    {
        using var trustRoot = new TestWorkspace();
        Directory.Delete(trustRoot.RootPath);
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var firstIdentity = Identity("first");
        var secondIdentity = Identity("second");
        var initialDigest = Digest("initial");
        var nextDigest = Digest("next");

        Assert.Null(await provider.ReadAsync(firstIdentity));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.InitializeAsync(firstIdentity, 1, initialDigest));
        var initialized = await provider.InitializeAsync(firstIdentity, 0, initialDigest);
        var replayed = await provider.InitializeAsync(firstIdentity, 0, initialDigest);
        Assert.Equal(initialized, replayed);
        await Assert.ThrowsAsync<IOException>(() => provider.InitializeAsync(firstIdentity, 0, Digest("different")));
        await Assert.ThrowsAsync<IOException>(() => provider.AuthenticateArtifactAsync(firstIdentity, 2, nextDigest));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.AdvanceAsync(firstIdentity, 0, initialDigest, 2, nextDigest));
        await Assert.ThrowsAsync<IOException>(() => provider.AdvanceAsync(firstIdentity, 1, initialDigest, 2, nextDigest));
        await Assert.ThrowsAsync<IOException>(() => provider.VerifyArtifactAsync(secondIdentity, 0, initialDigest, "hmac-sha256:" + new string('0', 64)));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.AuthenticateArtifactAsync(firstIdentity, -1, initialDigest));
    }

    [Fact]
    public async Task Anchor_advance_does_not_complete_before_the_trust_durability_barrier()
    {
        using var trustRoot = new TestWorkspace();
        var identity = Identity("durability-order");
        var initialDigest = Digest("durability-initial");
        var nextDigest = Digest("durability-next");
        await new FileCapabilityCatalogTrustProvider(trustRoot.RootPath).InitializeAsync(identity, 0, initialDigest);
        var barrier = new BlockingCapabilityCatalogDurabilityBarrier();
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath, barrier);

        var advance = provider.AdvanceAsync(identity, 0, initialDigest, 1, nextDigest);
        await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(advance.IsCompleted);
        barrier.Release();
        Assert.Equal(1, (await advance).CurrentGeneration);
        Assert.Equal(1, barrier.CallCount);
    }

    [Fact]
    public async Task Fresh_trust_directory_chain_is_committed_before_key_or_anchor_renames()
    {
        using var trustRoot = new TestWorkspace();
        Directory.Delete(trustRoot.RootPath);
        var barrier = new RecordingCapabilityCatalogDurabilityBarrier();
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath, barrier);

        _ = await provider.InitializeAsync(Identity("fresh-trust-order"), 0, Digest("fresh-trust-order"));

        Assert.Equal("directory:" + provider.RootPath, barrier.Events[0]);
        Assert.Equal("directory:" + provider.AnchorsPath, barrier.Events[1]);
        Assert.StartsWith("rename:" + provider.AuthenticationKeyPath, barrier.Events[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fresh_trust_directory_barrier_failure_aborts_before_key_or_anchor_writes()
    {
        using var trustRoot = new TestWorkspace();
        Directory.Delete(trustRoot.RootPath);
        var barrier = new RecordingCapabilityCatalogDurabilityBarrier { FailDirectoryCreateAt = 2 };
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath, barrier);

        await Assert.ThrowsAsync<IOException>(() => provider.InitializeAsync(Identity("fresh-trust-failure"), 0, Digest("fresh-trust-failure")));

        Assert.DoesNotContain(barrier.Events, entry => entry.StartsWith("rename:", StringComparison.Ordinal));
        Assert.False(File.Exists(provider.AuthenticationKeyPath));
        Assert.Empty(Directory.EnumerateFiles(provider.AnchorsPath));
    }

    [Fact]
    public async Task Unix_fifo_anchor_is_rejected_without_blocking()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var trustRoot = new TestWorkspace();
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var identity = Identity("fifo-anchor");
        Directory.CreateDirectory(provider.AnchorsPath);
        Assert.True(CapabilityCatalogUnixFifo.TryCreate(provider.GetAnchorPath(identity)));

        await Assert.ThrowsAnyAsync<IOException>(() => provider.ReadAsync(identity).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Provider_never_regenerates_missing_or_empty_key_over_existing_anchors()
    {
        using var trustRoot = new TestWorkspace();
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var firstIdentity = Identity("first-existing");
        var secondIdentity = Identity("second-existing");
        await provider.InitializeAsync(firstIdentity, 0, Digest("initial-existing"));
        File.Delete(provider.AuthenticationKeyPath);
        await File.WriteAllTextAsync(Path.Combine(provider.AnchorsPath, "unrelated-evidence"), "retained");

        await Assert.ThrowsAsync<IOException>(() => provider.InitializeAsync(secondIdentity, 0, Digest("second-initial")));
        Assert.False(File.Exists(provider.AuthenticationKeyPath));

        await File.WriteAllBytesAsync(provider.AuthenticationKeyPath, []);
        await Assert.ThrowsAsync<FormatException>(() => provider.ReadAsync(firstIdentity));
    }

    [Fact]
    public async Task Provider_refuses_authentication_when_the_server_trust_root_is_missing()
    {
        using var trustRoot = new TestWorkspace();
        var root = trustRoot.RootPath;
        Directory.Delete(root, recursive: true);
        var provider = new FileCapabilityCatalogTrustProvider(root);

        await Assert.ThrowsAsync<IOException>(() => provider.AuthenticateArtifactAsync(Identity("missing-root"), 0, Digest("missing-root")));
    }

    [Fact]
    public void Workspace_identity_uses_physical_directory_identity_and_rejects_link_aliases()
    {
        using var workspace = new TestWorkspace();
        var identity = CapabilityCatalogWorkspaceIdentity.Create(workspace.RootPath);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(identity, CapabilityCatalogWorkspaceIdentity.Create(@"\\?\" + workspace.RootPath));
        }

        using var holder = new TestWorkspace();
        var alias = holder.File("workspace-alias");
        try
        {
            Directory.CreateSymbolicLink(alias, workspace.RootPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        Assert.Throws<IOException>(() => CapabilityCatalogWorkspaceIdentity.Create(alias));
    }

    [Fact]
    public async Task Provider_retains_anchors_without_eviction_and_rejects_a_root_over_its_byte_quota()
    {
        using var trustRoot = new TestWorkspace();
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var firstIdentity = Identity("quota-first");
        var firstAnchor = provider.GetAnchorPath(firstIdentity);
        await provider.InitializeAsync(firstIdentity, 0, Digest("quota-first"));
        var filler = new byte[CapabilityCatalogLimits.MaximumTrustAnchorUtf8Bytes];
        for (var index = 0; index < 256; index++)
        {
            var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"quota-{index}"))).ToLowerInvariant() + ".json";
            await File.WriteAllBytesAsync(Path.Combine(provider.AnchorsPath, name), filler);
        }

        await Assert.ThrowsAsync<IOException>(() => provider.InitializeAsync(Identity("quota-second"), 0, Digest("quota-second")));
        Assert.True(File.Exists(firstAnchor));
    }

    [Fact]
    public async Task Provider_stops_anchor_enumeration_at_count_plus_one_and_fails_closed()
    {
        using var trustRoot = new TestWorkspace();
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var identity = Identity("count-quota");
        await provider.InitializeAsync(identity, 0, Digest("count-quota"));
        for (var index = 0; index < CapabilityCatalogLimits.MaximumTrustAnchors; index++)
        {
            var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"count-{index}"))).ToLowerInvariant() + ".json";
            await File.WriteAllBytesAsync(Path.Combine(provider.AnchorsPath, name), [0]);
        }

        await Assert.ThrowsAsync<IOException>(() => provider.ReadAsync(identity));
    }

    private static string Identity(string value) => Digest("workspace:" + value);

    private static string Digest(string value) => CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(value)).Value;
}
