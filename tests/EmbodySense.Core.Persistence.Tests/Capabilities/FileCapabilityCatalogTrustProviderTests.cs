using System.Text;
using EmbodySense.Core.Common.Capabilities;
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
    public void Workspace_identity_rejects_a_workspace_that_does_not_exist()
    {
        var missingWorkspace = Path.Combine(Directory.GetCurrentDirectory(), ".embodysense-missing-workspace-" + Guid.NewGuid().ToString("N"));

        Assert.False(Directory.Exists(missingWorkspace));
        Assert.Throws<DirectoryNotFoundException>(() => CapabilityCatalogWorkspaceIdentity.Create(missingWorkspace));
    }

    [Fact]
    public void Workspace_identity_changes_when_the_directory_is_recreated_at_the_same_path()
    {
        using var workspace = new TestWorkspace();
        var originalIdentity = CapabilityCatalogWorkspaceIdentity.Create(workspace.RootPath);

        Directory.Delete(workspace.RootPath, recursive: true);
        Directory.CreateDirectory(workspace.RootPath);
        var replacementIdentity = CapabilityCatalogWorkspaceIdentity.Create(workspace.RootPath);

        Assert.NotEqual(originalIdentity, replacementIdentity);
    }

    [Fact]
    public async Task Unix_workspace_lifetime_identity_rejects_retained_anchor_when_device_and_inode_are_reused()
    {
        using var trustRoot = new TestWorkspace();
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var oldMaterial = CapabilityCatalogWorkspaceIdentity.CreateUnixPhysicalIdentityMaterial("macos", 1, 0, 3, 4, 0x65f1a2b3, 0);
        var replacementMaterial = CapabilityCatalogWorkspaceIdentity.CreateUnixPhysicalIdentityMaterial("macos", 1, 0, 3, 5, 0x65f1a2b3, 0);
        var oldWorkspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(oldMaterial);
        var reusedWorkspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(replacementMaterial);
        var digest = Digest("reused-unix-workspace-artifact");

        Assert.NotEqual(oldWorkspaceIdentity, reusedWorkspaceIdentity);
        await provider.InitializeAsync(oldWorkspaceIdentity, 0, digest);
        var copiedArtifactTag = await provider.AuthenticateArtifactAsync(oldWorkspaceIdentity, 0, digest);

        await Assert.ThrowsAsync<IOException>(() => provider.VerifyArtifactAsync(reusedWorkspaceIdentity, 0, digest, copiedArtifactTag));
    }

    [Fact]
    public void Unix_workspace_lifetime_identity_changes_when_only_inode_generation_changes()
    {
        var originalMaterial = CapabilityCatalogWorkspaceIdentity.CreateUnixPhysicalIdentityMaterial("macos", 1, 0, 3, 4, 0x65f1a2b3, 0);
        var replacementMaterial = CapabilityCatalogWorkspaceIdentity.CreateUnixPhysicalIdentityMaterial("macos", 1, 0, 3, 5, 0x65f1a2b3, 0);
        var originalLifetime = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(originalMaterial);
        var replacementLifetime = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(replacementMaterial);

        Assert.NotEqual(originalMaterial, replacementMaterial);
        Assert.NotEqual(originalLifetime, replacementLifetime);
    }

    [Fact]
    public void Workspace_identity_digest_rejects_missing_physical_identity_material()
    {
        Assert.Throws<ArgumentException>(() => CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(" "));
    }

    [Fact]
    public void Unix_workspace_identity_material_requires_stable_identity_and_lifetime_availability()
    {
        Assert.Equal(
            "macos:00000001:0000000000000003:generation-00000004:0000000065f1a2b3:0000000000000000",
            CapabilityCatalogWorkspaceIdentity.CreateUnixPhysicalIdentityMaterial("macos", 1, 0, 3, 4, 0x65f1a2b3, 0));
        Assert.Equal(
            "macos:00000001:0000000000000003:nonrecycled-inode:0000000065f1a2b3:0000000000000000",
            CapabilityCatalogWorkspaceIdentity.CreateUnixPhysicalIdentityMaterial("macos", 1, 0, 3, 0, 0x65f1a2b3, 0, inodeIsNonRecycled: true));
        var linuxFailure = Assert.Throws<PlatformNotSupportedException>(() => CapabilityCatalogWorkspaceIdentity.CreateUnixPhysicalIdentityMaterial("linux", 1, 2, 3, 4, 0x65f1a2b3, 0));
        Assert.Equal("Capability catalog Linux workspace identity is unsupported because no non-owner-writable directory-lifetime identity is available.", linuxFailure.Message);
        Assert.Throws<IOException>(() => CapabilityCatalogWorkspaceIdentity.CreateUnixPhysicalIdentityMaterial("macos", 1, 0, null, 4, 0x65f1a2b3, 0));
        Assert.Throws<IOException>(() => CapabilityCatalogWorkspaceIdentity.CreateUnixPhysicalIdentityMaterial("macos", 1, 0, 3, null, 0x65f1a2b3, 0));
        Assert.Throws<IOException>(() => CapabilityCatalogWorkspaceIdentity.CreateUnixPhysicalIdentityMaterial("macos", 1, 0, 3, 0, 0x65f1a2b3, 0));
        Assert.Throws<IOException>(() => CapabilityCatalogWorkspaceIdentity.CreateUnixPhysicalIdentityMaterial("macos", 1, 0, 3, 4, null, null));
        Assert.Throws<IOException>(() => CapabilityCatalogWorkspaceIdentity.CreateUnixPhysicalIdentityMaterial("macos", 1, 0, 3, 4, 0, 0));
        Assert.Throws<ArgumentException>(() => CapabilityCatalogWorkspaceIdentity.CreateUnixPhysicalIdentityMaterial("freebsd", 1, 2, 3, 4, 0x65f1a2b3, 0));
    }

    [Fact]
    public void Native_workspace_identity_read_mapping_preserves_failure_details()
    {
        CapabilityCatalogWorkspaceIdentity.RequireNativePhysicalIdentityRead(0, 0);
        var readFailure = Assert.Throws<IOException>(() => CapabilityCatalogWorkspaceIdentity.RequireNativePhysicalIdentityRead(-1, 5));
        Assert.Equal(unchecked((int)0x80070005), readFailure.HResult);
    }

    [Fact]
    public void Mac_workspace_volume_capability_mapping_requires_complete_valid_enabled_persistent_object_id_evidence()
    {
        const uint CapabilityBufferLength = 36;
        const uint PersistentObjectIds = 0x00000001;
        const uint PathFromId = 0x00004000;
        Assert.True(CapabilityCatalogWorkspaceIdentity.MacVolumeCapabilitiesProveNonRecycledObjectIdentity(0, 0, CapabilityBufferLength, CapabilityBufferLength, PersistentObjectIds, PersistentObjectIds));
        Assert.False(CapabilityCatalogWorkspaceIdentity.MacVolumeCapabilitiesProveNonRecycledObjectIdentity(0, 0, CapabilityBufferLength - 1, CapabilityBufferLength, PersistentObjectIds, PersistentObjectIds));
        Assert.False(CapabilityCatalogWorkspaceIdentity.MacVolumeCapabilitiesProveNonRecycledObjectIdentity(0, 0, CapabilityBufferLength, CapabilityBufferLength, 0, PersistentObjectIds));
        Assert.False(CapabilityCatalogWorkspaceIdentity.MacVolumeCapabilitiesProveNonRecycledObjectIdentity(0, 0, CapabilityBufferLength, CapabilityBufferLength, PersistentObjectIds, 0));
        Assert.False(CapabilityCatalogWorkspaceIdentity.MacVolumeCapabilitiesProveNonRecycledObjectIdentity(0, 0, CapabilityBufferLength, CapabilityBufferLength, PathFromId, PathFromId));
        Assert.Throws<ArgumentOutOfRangeException>(() => CapabilityCatalogWorkspaceIdentity.MacVolumeCapabilitiesProveNonRecycledObjectIdentity(0, 0, 0, 0, PersistentObjectIds, PersistentObjectIds));
        var failure = Assert.Throws<IOException>(() => CapabilityCatalogWorkspaceIdentity.MacVolumeCapabilitiesProveNonRecycledObjectIdentity(-1, 5, CapabilityBufferLength, CapabilityBufferLength, PersistentObjectIds, PersistentObjectIds));
        Assert.Equal(unchecked((int)0x80070005), failure.HResult);
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
