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
    public void File_backed_catalog_rejects_Windows_case_extended_device_and_available_short_path_aliases()
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

        var deviceProvider = new FileCapabilityCatalogTrustProvider(@"\\.\" + root.RootPath);
        Assert.Throws<InvalidOperationException>(() => new CapabilityCatalogStore(new WorkspacePaths(root.RootPath), deviceProvider));

        var shortPath = WindowsPathAliases.TryGetShortPath(root.RootPath);
        if (shortPath is not null && !string.Equals(shortPath, root.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            var shortPathProvider = new FileCapabilityCatalogTrustProvider(shortPath);
            Assert.Throws<InvalidOperationException>(() => new CapabilityCatalogStore(new WorkspacePaths(root.RootPath), shortPathProvider));
        }
    }

    [Fact]
    public void File_backed_catalog_rejects_Windows_volume_GUID_alias_for_nested_trust_root()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TestWorkspace();
        var workspaceRoot = root.File("workspace");
        var nestedTrustRoot = Path.Combine(workspaceRoot, "server-trust");
        Directory.CreateDirectory(nestedTrustRoot);
        var volumeGuidTrustRoot = WindowsPathAliases.TryGetVolumeGuidPath(nestedTrustRoot);
        Assert.NotNull(volumeGuidTrustRoot);
        var provider = new FileCapabilityCatalogTrustProvider(volumeGuidTrustRoot);

        Assert.Throws<InvalidOperationException>(() => new CapabilityCatalogStore(new WorkspacePaths(workspaceRoot), provider));
        Assert.False(File.Exists(Path.Combine(nestedTrustRoot, "capability-catalog-root.key")));
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
    public void File_backed_catalog_rejects_Windows_link_target_under_workspace_with_nonexistent_tail()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TestWorkspace();
        var workspaceRoot = root.File("workspace");
        var linkedTrustTarget = Path.Combine(workspaceRoot, "trust-target");
        var trustAlias = root.File("trust-alias");
        Directory.CreateDirectory(linkedTrustTarget);
        try
        {
            Directory.CreateSymbolicLink(trustAlias, linkedTrustTarget);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        var provider = new FileCapabilityCatalogTrustProvider(Path.Combine(trustAlias, "not-created"));

        Assert.Throws<InvalidOperationException>(() => new CapabilityCatalogStore(new WorkspacePaths(workspaceRoot), provider));
        Assert.False(File.Exists(Path.Combine(linkedTrustTarget, "not-created", "capability-catalog-root.key")));
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
    public void File_backed_catalog_fails_closed_without_disclosing_an_unavailable_Windows_UNC_root()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TestWorkspace();
        var workspaceRoot = root.File("workspace");
        Directory.CreateDirectory(workspaceRoot);
        var privateCanary = $"embodysense-private-{Guid.NewGuid():N}";
        var provider = new FileCapabilityCatalogTrustProvider($@"\\?\UNC\localhost\{privateCanary}\trust");

        var exception = Assert.Throws<IOException>(() => new CapabilityCatalogStore(new WorkspacePaths(workspaceRoot), provider));

        Assert.DoesNotContain(privateCanary, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root.RootPath, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void File_backed_catalog_fails_closed_without_disclosing_a_Windows_device_metadata_failure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TestWorkspace();
        var workspaceRoot = root.File("workspace");
        Directory.CreateDirectory(workspaceRoot);
        var provider = new FileCapabilityCatalogTrustProvider(@"\\.\NUL");

        var exception = Assert.Throws<IOException>(() => new CapabilityCatalogStore(new WorkspacePaths(workspaceRoot), provider));

        Assert.DoesNotContain(root.RootPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NUL", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task File_backed_catalog_treats_an_unavailable_Windows_volume_device_root_as_unavailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TestWorkspace();
        var workspaceRoot = root.File("workspace");
        Directory.CreateDirectory(workspaceRoot);
        var trustRoot = @"\\?\Volume{00000000-0000-0000-0000-000000000000}\trust";
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot);
        var store = new CapabilityCatalogStore(new WorkspacePaths(workspaceRoot), provider);

        var result = await store.ReadAsync(null, 1);

        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, result.Status);
        Assert.False(File.Exists(provider.AuthenticationKeyPath));
    }

    [Fact]
    public void File_backed_catalog_fails_closed_when_a_Windows_nonexistent_tail_exceeds_the_topology_bound()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TestWorkspace();
        var workspaceRoot = root.File("workspace");
        Directory.CreateDirectory(workspaceRoot);
        var trustRoot = root.File("missing-trust");
        for (var index = 0; index < 34; index++)
        {
            trustRoot = Path.Combine(trustRoot, "x");
        }

        var provider = new FileCapabilityCatalogTrustProvider(trustRoot);
        var exception = Assert.Throws<IOException>(() => new CapabilityCatalogStore(new WorkspacePaths(workspaceRoot), provider));

        Assert.Equal("Capability catalog root topology exceeded its bounded filesystem-link resolution depth.", exception.Message);
    }

    [Fact]
    public void File_backed_catalog_fails_closed_when_a_Windows_existing_ancestor_walk_exceeds_the_topology_bound()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TestWorkspace();
        var workspaceRoot = root.File("deep-workspace");
        for (var index = 0; index < 34; index++)
        {
            workspaceRoot = Path.Combine(workspaceRoot, "x");
        }

        Directory.CreateDirectory(workspaceRoot);
        var provider = new FileCapabilityCatalogTrustProvider(root.File("server-trust"));
        var exception = Assert.Throws<IOException>(() => new CapabilityCatalogStore(new WorkspacePaths(workspaceRoot), provider));

        Assert.Equal("Capability catalog root topology exceeded its bounded filesystem-link resolution depth.", exception.Message);
        Assert.False(File.Exists(provider.AuthenticationKeyPath));
    }

    [Fact]
    public void File_backed_catalog_shares_one_Windows_probe_budget_between_missing_and_existing_ancestors()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TestWorkspace();
        var workspaceRoot = root.File("workspace");
        Directory.CreateDirectory(workspaceRoot);
        var trustRoot = root.File("trust-existing");
        for (var index = 0; index < 20; index++)
        {
            trustRoot = Path.Combine(trustRoot, $"existing-{index:D2}");
        }

        Directory.CreateDirectory(trustRoot);
        for (var index = 0; index < 20; index++)
        {
            trustRoot = Path.Combine(trustRoot, $"missing-{index:D2}");
        }

        var provider = new FileCapabilityCatalogTrustProvider(trustRoot);
        var exception = Assert.Throws<IOException>(() => new CapabilityCatalogStore(new WorkspacePaths(workspaceRoot), provider));

        Assert.Equal("Capability catalog root topology exceeded its bounded filesystem-link resolution depth.", exception.Message);
        Assert.False(File.Exists(provider.AuthenticationKeyPath));
    }

    [Fact]
    public async Task File_backed_catalog_treats_a_disjoint_existing_file_trust_root_as_unavailable()
    {
        using var root = new TestWorkspace();
        var workspaceRoot = root.File("workspace");
        var trustRoot = root.File("server-trust");
        Directory.CreateDirectory(workspaceRoot);
        await File.WriteAllTextAsync(trustRoot, "untrusted-file-root");
        var store = new CapabilityCatalogStore(new WorkspacePaths(workspaceRoot), new FileCapabilityCatalogTrustProvider(trustRoot));

        var result = await store.ReadAsync(null, 1);

        Assert.Equal(CapabilityCatalogReadStatus.Unavailable, result.Status);
        Assert.Equal("untrusted-file-root", await File.ReadAllTextAsync(trustRoot));
    }

    [Fact]
    public async Task Provider_fails_closed_when_a_Windows_UNC_root_is_unavailable_at_startup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var privateCanary = $"embodysense-private-{Guid.NewGuid():N}";
        var provider = new FileCapabilityCatalogTrustProvider($@"\\?\UNC\localhost\{privateCanary}\trust");

        _ = await Assert.ThrowsAsync<IOException>(() => provider.ReadAsync(Identity("unavailable-unc-root")));
    }

    [Fact]
    public async Task Provider_fails_closed_when_the_Windows_trust_lock_is_a_directory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var trustRoot = new TestWorkspace();
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        Directory.CreateDirectory(provider.TrustLockPath);

        _ = await Assert.ThrowsAsync<IOException>(() => provider.ReadAsync(Identity("directory-trust-lock")));

        Assert.True(Directory.Exists(provider.TrustLockPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(provider.TrustLockPath));
    }

    [Fact]
    public async Task Provider_removes_Windows_staging_state_when_startup_is_rejected_before_move()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TestWorkspace();
        var barrier = new RecordingCapabilityCatalogDurabilityBarrier { BeforeDirectoryMoveFailure = new IOException("Injected pre-move durability failure.") };
        var provider = new FileCapabilityCatalogTrustProvider(root.File("server-trust"), barrier);

        _ = await Assert.ThrowsAsync<IOException>(() => provider.InitializeAsync(Identity("pre-move-failure"), 0, Digest("pre-move-failure")));

        Assert.False(Directory.Exists(provider.RootPath));
        Assert.False(File.Exists(provider.AuthenticationKeyPath));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(root.RootPath), path => Path.GetFileName(path).StartsWith(".server-trust.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Provider_releases_the_Windows_staging_handle_when_exact_cleanup_disposition_fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TestWorkspace();
        string? stagingPath = null;
        var barrier = new RecordingCapabilityCatalogDurabilityBarrier
        {
            BeforeDirectoryMoveAction = (path, _) =>
            {
                stagingPath = path;
                File.WriteAllText(Path.Combine(path, "cleanup-blocker"), "retained");
                throw new IOException("Injected pre-move durability failure.");
            }
        };
        var provider = new FileCapabilityCatalogTrustProvider(root.File("server-trust"), barrier);

        var exception = await Assert.ThrowsAsync<IOException>(() => provider.InitializeAsync(Identity("cleanup-disposition-failure"), 0, Digest("cleanup-disposition-failure")));

        Assert.NotNull(stagingPath);
        Assert.Contains("could not be marked for exact cleanup", exception.Message, StringComparison.Ordinal);
        File.Delete(Path.Combine(stagingPath, "cleanup-blocker"));
        if (Directory.Exists(stagingPath))
        {
            Directory.Delete(stagingPath);
        }
        Assert.False(Directory.Exists(stagingPath));
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
