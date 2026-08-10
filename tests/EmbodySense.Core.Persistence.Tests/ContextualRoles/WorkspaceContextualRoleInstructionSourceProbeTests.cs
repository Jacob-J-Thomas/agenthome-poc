using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Tests.Support;
using System.Runtime.InteropServices;
using System.Text;

namespace EmbodySense.Core.Persistence.Tests.ContextualRoles;

public sealed class WorkspaceContextualRoleInstructionSourceProbeTests
{
    [Fact]
    public async Task Registered_sources_are_missing_without_creating_or_returning_workspace_content()
    {
        using var workspace = new TestWorkspace();
        var probe = new WorkspaceContextualRoleInstructionSourceProbe(new WorkspacePaths(workspace.RootPath));

        var agents = await probe.ProbeAsync(Source(ContextualRoleInstructionSourceKind.AgentsMarkdown, "nearest-agents"));
        var role = await probe.ProbeAsync(Source(ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role"));

        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Missing, agents.Status);
        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Missing, role.Status);
        Assert.False(Directory.Exists(Path.Combine(workspace.RootPath, ".agent")));
    }

    [Fact]
    public async Task Registered_sources_validate_nearest_precedence_and_discard_secret_content()
    {
        using var parent = new TestWorkspace();
        var workspaceRoot = Path.Combine(parent.RootPath, "nested", "workspace");
        Directory.CreateDirectory(Path.Combine(workspaceRoot, ".agent"));
        await File.WriteAllTextAsync(Path.Combine(parent.RootPath, "AGENTS.md"), "parent instructions");
        await File.WriteAllTextAsync(Path.Combine(parent.RootPath, "nested", "AGENTS.md"), "nearest secret canary 91d22e");
        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, ".agent", "ROLE.md"), "role secret canary b882a1");
        var probe = new WorkspaceContextualRoleInstructionSourceProbe(new WorkspacePaths(workspaceRoot));

        var agents = await probe.ProbeAsync(Source(ContextualRoleInstructionSourceKind.AgentsMarkdown, "nearest-agents"));
        var role = await probe.ProbeAsync(Source(ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role"));

        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Ready, agents.Status);
        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Ready, role.Status);
        Assert.DoesNotContain("91d22e", agents.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("b882a1", role.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(parent.RootPath, agents.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ContextualRoleInstructionSourceKind.RoleArtifact, "role")]
    [InlineData(ContextualRoleInstructionSourceKind.AgentsMarkdown, "role")]
    [InlineData(ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "nearest-agents")]
    [InlineData(ContextualRoleInstructionSourceKind.Unknown, "role")]
    public async Task Unregistered_kind_and_identity_pairs_fail_closed(ContextualRoleInstructionSourceKind kind, string sourceId)
    {
        using var workspace = new TestWorkspace();
        var probe = new WorkspaceContextualRoleInstructionSourceProbe(new WorkspacePaths(workspace.RootPath));

        var result = await probe.ProbeAsync(Source(kind, sourceId));
        var untrusted = await probe.ProbeAsync(new ContextualRoleInstructionSourceReference(ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role", ContextualRoleInstructionClassification.UntrustedContext));

        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Unsupported, result.Status);
        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Unsupported, untrusted.Status);
    }

    [Fact]
    public async Task Empty_and_malformed_utf8_sources_are_ambiguous()
    {
        using var workspace = new TestWorkspace();
        var agent = Path.Combine(workspace.RootPath, ".agent");
        Directory.CreateDirectory(agent);
        var rolePath = Path.Combine(agent, "ROLE.md");
        var probe = new WorkspaceContextualRoleInstructionSourceProbe(new WorkspacePaths(workspace.RootPath));
        await File.WriteAllTextAsync(rolePath, " \r\n\t");

        var empty = await probe.ProbeAsync(Source(ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role"));
        await File.WriteAllBytesAsync(rolePath, [0xff, 0xfe, 0xfd]);
        var malformed = await probe.ProbeAsync(Source(ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role"));

        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Ambiguous, empty.Status);
        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Ambiguous, malformed.Status);
    }

    [Fact]
    public async Task Oversized_sources_fail_closed_at_the_exact_byte_boundary()
    {
        using var workspace = new TestWorkspace();
        var agent = Path.Combine(workspace.RootPath, ".agent");
        Directory.CreateDirectory(agent);
        var rolePath = Path.Combine(agent, "ROLE.md");
        var probe = new WorkspaceContextualRoleInstructionSourceProbe(new WorkspacePaths(workspace.RootPath));
        await File.WriteAllBytesAsync(rolePath, Encoding.UTF8.GetBytes(new string('a', WorkspaceContextualRoleInstructionSourceProbe.MaximumInstructionSourceBytes)));

        var boundary = await probe.ProbeAsync(Source(ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role"));
        await File.WriteAllBytesAsync(rolePath, Encoding.UTF8.GetBytes(new string('a', WorkspaceContextualRoleInstructionSourceProbe.MaximumInstructionSourceBytes + 1)));
        var oversized = await probe.ProbeAsync(Source(ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role"));

        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Ready, boundary.Status);
        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Oversized, oversized.Status);
    }

    [Fact]
    public async Task Symbolic_file_and_directory_substitutions_are_rejected_without_reading_targets()
    {
        using var workspace = new TestWorkspace();
        using var outside = new TestWorkspace();
        var outsideFile = Path.Combine(outside.RootPath, "ROLE.md");
        await File.WriteAllTextAsync(outsideFile, "outside secret canary 87a4d2");
        var agent = Path.Combine(workspace.RootPath, ".agent");
        Directory.CreateDirectory(agent);
        var rolePath = Path.Combine(agent, "ROLE.md");
        File.CreateSymbolicLink(rolePath, outsideFile);
        var probe = new WorkspaceContextualRoleInstructionSourceProbe(new WorkspacePaths(workspace.RootPath));

        var fileLink = await probe.ProbeAsync(Source(ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role"));
        File.Delete(rolePath);
        Directory.Delete(agent);
        Directory.CreateSymbolicLink(agent, outside.RootPath);
        var directoryLink = await probe.ProbeAsync(Source(ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role"));

        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Substituted, fileLink.Status);
        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Substituted, directoryLink.Status);
        Assert.DoesNotContain("87a4d2", fileLink.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Intermediate_directory_links_and_hard_linked_sources_are_rejected()
    {
        using var workspace = new TestWorkspace();
        using var outside = new TestWorkspace();
        var physicalRoot = Path.Combine(outside.RootPath, "physical-workspace");
        var physicalAgent = Path.Combine(physicalRoot, ".agent");
        Directory.CreateDirectory(physicalAgent);
        await File.WriteAllTextAsync(Path.Combine(physicalAgent, "ROLE.md"), "outside role instructions");
        var linkedParent = Path.Combine(workspace.RootPath, "linked-parent");
        Directory.CreateSymbolicLink(linkedParent, outside.RootPath);
        var linkedWorkspace = Path.Combine(linkedParent, "physical-workspace");
        var linkedProbe = new WorkspaceContextualRoleInstructionSourceProbe(new WorkspacePaths(linkedWorkspace));

        var directoryLink = await linkedProbe.ProbeAsync(Source(ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role"));

        var localPaths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(localPaths.AgentPath);
        await File.WriteAllTextAsync(localPaths.RolePath, "local role instructions");
        CreateHardLink(Path.Combine(workspace.RootPath, "role-hardlink.md"), localPaths.RolePath);
        var localProbe = new WorkspaceContextualRoleInstructionSourceProbe(localPaths);

        var hardLink = await localProbe.ProbeAsync(Source(ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role"));

        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Substituted, directoryLink.Status);
        Assert.Equal(ContextualRoleInstructionSourceProbeStatus.Substituted, hardLink.Status);
    }

    [Fact]
    public async Task Cancellation_and_constructor_validation_are_explicit()
    {
        using var workspace = new TestWorkspace();
        var probe = new WorkspaceContextualRoleInstructionSourceProbe(new WorkspacePaths(workspace.RootPath));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => probe.ProbeAsync(Source(ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role"), cancellation.Token));
        Assert.Throws<ArgumentNullException>(() => new WorkspaceContextualRoleInstructionSourceProbe(null!));
    }

    private static ContextualRoleInstructionSourceReference Source(ContextualRoleInstructionSourceKind kind, string sourceId)
        => new(kind, sourceId, ContextualRoleInstructionClassification.RoleInstruction);

    private static void CreateHardLink(string linkPath, string existingPath)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(WindowsCreateHardLink(linkPath, existingPath, IntPtr.Zero));
            return;
        }

        Assert.Equal(0, UnixCreateHardLink(existingPath, linkPath));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WindowsCreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    private static extern int UnixCreateHardLink(string existingPath, string newPath);
}
