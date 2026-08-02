using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class DefaultConversationTurnLeaseHardLinkTests
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode ExecutableByAll = OwnerOnly
        | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead
        | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead
        | UnixFileMode.OtherExecute;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Store_rejects_preexisting_hard_linked_unix_lease_without_mutating_external_inode(bool ownerOnlyMode)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        var externalPath = workspace.File("external-lease-target");
        const string ExternalContent = "external inode must remain unchanged";
        await File.WriteAllTextAsync(externalPath, ExternalContent);
        var expectedMode = ownerOnlyMode ? OwnerOnly : ExecutableByAll;
        File.SetUnixFileMode(externalPath, expectedMode);
        var leasePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, ".active-set.lock");
        UnixHardLink.Create(leasePath, externalPath);
        var coordination = new HardLinkingTurnStoreCoordination(leasePath, workspace.File("unexpected-pre-lock-alias"));
        var turns = new DefaultConversationTurnStore(paths, coordination);

        var operation = turns.ListIncompleteAsync();
        var completed = await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(operation, completed);
        var exception = await Assert.ThrowsAsync<IOException>(() => operation);
        Assert.Contains("exclusive owner-only file posture", exception.Message, StringComparison.Ordinal);
        Assert.Equal(ExternalContent, await File.ReadAllTextAsync(externalPath));
        Assert.Equal(expectedMode, File.GetUnixFileMode(externalPath));
        Assert.Equal(0, coordination.LinkCount);
        File.Delete(leasePath);
        Assert.Empty(await new DefaultConversationTurnStore(paths).ListIncompleteAsync());
    }

    [Fact]
    public async Task Store_rejects_existing_unix_lease_with_non_owner_only_permissions_without_normalizing_it()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        var leasePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, ".active-set.lock");
        await File.WriteAllTextAsync(leasePath, string.Empty);
        File.SetUnixFileMode(leasePath, ExecutableByAll);
        var turns = new DefaultConversationTurnStore(paths);

        var exception = await Assert.ThrowsAsync<IOException>(() => turns.ListIncompleteAsync());

        Assert.Contains("exclusive owner-only file posture", exception.Message, StringComparison.Ordinal);
        Assert.Equal(ExecutableByAll, File.GetUnixFileMode(leasePath));
    }

    [Fact]
    public async Task Store_revalidates_unix_lease_link_count_immediately_before_exclusive_lock()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        var trustedStore = new DefaultConversationTurnStore(paths);
        Assert.Empty(await trustedStore.ListIncompleteAsync());
        var leasePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, ".active-set.lock");
        var aliasPath = workspace.File("late-lease-hard-link");
        var coordination = new HardLinkingTurnStoreCoordination(leasePath, aliasPath);
        var racedStore = new DefaultConversationTurnStore(paths, coordination);

        var exception = await Assert.ThrowsAsync<IOException>(() => racedStore.ListIncompleteAsync());

        Assert.Contains("exclusive owner-only file posture", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(aliasPath));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(aliasPath));
        File.Delete(aliasPath);
        Assert.Empty(await new DefaultConversationTurnStore(paths).ListIncompleteAsync());
    }

    [Fact]
    public async Task Store_rejects_a_lease_path_replacement_before_exclusive_lock_and_preserves_both_inodes()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        Assert.Empty(await new DefaultConversationTurnStore(paths).ListIncompleteAsync());
        var leasePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, ".active-set.lock");
        var displacedPath = workspace.File("displaced-active-set-lease");
        const string ReplacementContent = "replacement lease must remain unchanged";
        var coordination = new ReplacingTurnStoreCoordination(leasePath, displacedPath, ReplacementContent);
        var racedStore = new DefaultConversationTurnStore(paths, coordination);

        var exception = await Assert.ThrowsAsync<IOException>(() => racedStore.ListIncompleteAsync());

        Assert.Contains("no longer names the validated file", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(displacedPath));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(displacedPath));
        Assert.Equal(ReplacementContent, await File.ReadAllTextAsync(leasePath));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(leasePath));
        File.Delete(leasePath);
        File.Move(displacedPath, leasePath);
        Assert.Empty(await new DefaultConversationTurnStore(paths).ListIncompleteAsync());
    }

    [Fact]
    public async Task Store_releases_the_exclusive_lock_when_final_link_count_validation_fails()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        Assert.Empty(await new DefaultConversationTurnStore(paths).ListIncompleteAsync());
        var leasePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, ".active-set.lock");
        var aliasPath = workspace.File("post-lock-lease-hard-link");
        var coordination = new HardLinkingTurnStoreCoordination(
            leasePath,
            aliasPath,
            DefaultConversationTurnLeasePhase.AfterExclusiveLockBeforeFinalValidation);

        var exception = await Assert.ThrowsAsync<IOException>(() => new DefaultConversationTurnStore(paths, coordination).ListIncompleteAsync());

        Assert.Contains("exclusive owner-only file posture", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(aliasPath));
        File.Delete(aliasPath);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.Empty(await new DefaultConversationTurnStore(paths).ListIncompleteAsync(cancellation.Token));
    }
}
