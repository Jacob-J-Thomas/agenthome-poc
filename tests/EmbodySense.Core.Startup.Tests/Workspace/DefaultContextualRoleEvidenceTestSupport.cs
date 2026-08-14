using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace EmbodySense.Core.Startup.Tests.Workspace;

internal static class DefaultContextualRoleEvidenceTestSupport
{
    public static async Task DamageAsync(TestWorkspace workspace, string damage)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        var roleStorePath = Path.Combine(paths.AgentPath, "contextual-roles");
        switch (damage)
        {
            case "missing":
                Directory.Delete(roleStorePath, recursive: true);
                return;
            case "corrupt":
                var revisionPath = Path.Combine(roleStorePath, "revisions", "default-assistant.1.json");
                var root = JsonNode.Parse(await File.ReadAllTextAsync(revisionPath))!.AsObject();
                root["integrityHash"] = new string('0', 64);
                await File.WriteAllTextAsync(revisionPath, root.ToJsonString());
                return;
            case "substituted":
                Directory.Delete(roleStorePath, recursive: true);
                await CreateSubstitutedRoleAsync(paths);
                return;
            case "inactive":
                await DisableDefaultRoleAsync(paths);
                return;
            case "wrong-workspace":
                using (var other = new TestWorkspace())
                {
                    await WorkspaceInitializer.ForFileCapabilityTrustRoot(other.ServerStatePath).InitializeAsync(other.RootPath);
                    Directory.Delete(roleStorePath, recursive: true);
                    CopyDirectory(Path.Combine(other.RootPath, ".agent", "contextual-roles"), roleStorePath);
                }

                return;
            case "source-ineligible":
                await File.WriteAllTextAsync(paths.RolePath, " \r\n\t");
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(damage), damage, "Unknown contextual-role damage case.");
        }
    }

    public static IReadOnlyDictionary<string, string> SnapshotFiles(string rootPath)
        => Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(rootPath, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal);

    private static async Task DisableDefaultRoleAsync(WorkspacePaths paths)
    {
        var identity = new ContextualRoleRevisionIdentity(DefaultContextualRoleSeeder.RoleId, DefaultContextualRoleSeeder.Revision);
        var request = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest(
            "disable-default-assistant",
            string.Empty,
            ContextualRoleRevisionMutationKind.Disable,
            identity.RoleId,
            "startup-test",
            null,
            identity,
            new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero)));
        using var store = new ContextualRoleRevisionStore(paths, CapabilityWorkspaceScopeId.Create(paths.RootPath));
        var result = await store.MutateAsync(request);
        if (result.Status != ContextualRoleRevisionMutationStatus.Accepted)
        {
            throw new InvalidOperationException($"Could not arrange disabled role evidence: {result.Status}.");
        }
    }

    private static async Task CreateSubstitutedRoleAsync(WorkspacePaths paths)
    {
        const string RoleId = "substituted-assistant";
        var timestamp = DateTimeOffset.UnixEpoch;
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var revision = ContextualRoleRevisionContentHash.Apply(new ContextualRoleRevision(
            1,
            new ContextualRoleRevisionIdentity(RoleId, 1),
            string.Empty,
            "Substituted assistant",
            "Represent an intentionally substituted role store.",
            ContextualRoleStatus.Published,
            new ContextualRoleProvenance("startup-test", timestamp, timestamp),
            new ContextualRoleWorkspaceApplicability(ImmutableArray.Create(workspaceId)),
            new ContextualRoleInstructionSourceReference(
                ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown,
                "role",
                ContextualRoleInstructionClassification.RoleInstruction),
            new ContextualRolePolicyMaxima(ImmutableArray<string>.Empty)));
        var request = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest(
            "create-substituted-assistant",
            string.Empty,
            ContextualRoleRevisionMutationKind.Create,
            RoleId,
            "startup-test",
            revision,
            null,
            timestamp));
        using var store = new ContextualRoleRevisionStore(paths, workspaceId);
        var result = await store.MutateAsync(request);
        if (result.Status != ContextualRoleRevisionMutationStatus.Accepted)
        {
            throw new InvalidOperationException($"Could not arrange substituted role evidence: {result.Status}.");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }
}
