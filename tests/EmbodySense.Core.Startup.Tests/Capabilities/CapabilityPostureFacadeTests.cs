using System.Text.Json;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Capabilities.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Capabilities;

public sealed class CapabilityPostureFacadeTests
{
    [Fact]
    public async Task Administrative_facade_returns_bounded_redacted_catalog_and_exact_posture_without_conferring_authority()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var filesBefore = SnapshotFiles(workspace);
        var facade = CapabilityPostureFacade.ForFileCapabilityTrustRoot(workspace.RootPath, workspace.ServerStatePath);

        var catalog = await facade.ReadCatalogAsync(null, 50);
        var inference = await facade.ReadAsync("org.embodysense/model-inference");
        var exact = await facade.ReadAsync("org.embodysense/workspace-command");

        Assert.Equal("available", catalog.Status);
        Assert.Equal(
            ["org.embodysense/conversation-turn", "org.embodysense/model-inference", "org.embodysense/workspace-command"],
            catalog.Capabilities.Select(item => item.Id));
        Assert.Equal("available", inference.Status);
        var inferencePosture = Assert.IsType<CapabilityPostureSnapshot>(inference.Capability);
        Assert.Equal("graph-node", inferencePosture.Kind);
        Assert.Equal("none", inferencePosture.SideEffectClass);
        Assert.Equal("available", inferencePosture.State);
        Assert.Empty(inferencePosture.Dependents);
        Assert.Equal("available", exact.Status);
        var posture = Assert.IsType<CapabilityPostureSnapshot>(exact.Capability);
        Assert.Equal("actuator", posture.Kind);
        Assert.Equal("built-in", posture.ProvenanceKind);
        Assert.Equal("local-reversible", posture.SideEffectClass);
        Assert.Equal("available", posture.State);
        Assert.True(posture.IsCurrentHostCompatible);
        Assert.False(posture.IsRemoved);
        var dependent = Assert.Single(posture.Dependents);
        Assert.Equal("loop", dependent.Kind);
        Assert.Equal("default-conversation", dependent.Identity);
        Assert.Equal("required", dependent.RequirementKind);
        Assert.Equal("assigned-definition", dependent.AuthorityPosture);
        Assert.DoesNotContain(workspace.RootPath, JsonSerializer.Serialize(new { catalog, inference, exact }), StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.ServerStatePath, JsonSerializer.Serialize(new { catalog, inference, exact }), StringComparison.Ordinal);
        Assert.DoesNotContain("secretValue", JsonSerializer.Serialize(new { catalog, inference, exact }), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(filesBefore, SnapshotFiles(workspace));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => facade.ReadCatalogAsync(null, 10, cancellation.Token));
    }

    [Fact]
    public async Task Model_facade_intersects_assignment_and_current_authority_and_fails_closed_across_workspaces()
    {
        using var workspace = new TestWorkspace();
        using var otherWorkspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(otherWorkspace.ServerStatePath).InitializeAsync(otherWorkspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var admissionService = CapabilityAdmissionFactory.Create(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath));
        var requirements = LoopDefinition.CreateDefaultConversation().CapabilityRequirements;
        var assignedIds = LoopCapabilityRequirements.GetAssignedCapabilityIds(requirements);
        var admitted = await admissionService.AdmitAsync(requirements, assignedIds);
        var snapshot = Assert.IsType<CapabilityAdmissionSnapshot>(admitted.Snapshot);
        var facade = CapabilityPostureFacade.ForFileCapabilityTrustRoot(workspace.RootPath, workspace.ServerStatePath);

        var result = await facade.ReadModelContextAsync(
            snapshot,
            ["org.embodysense/workspace-command", "org.embodysense/conversation-turn"],
            ["org.embodysense/workspace-command"]);
        var crossWorkspace = await CapabilityPostureFacade.ForFileCapabilityTrustRoot(otherWorkspace.RootPath, otherWorkspace.ServerStatePath).ReadModelContextAsync(snapshot, ["org.embodysense/workspace-command"], ["org.embodysense/workspace-command"]);
        var mismatchedTrust = await CapabilityPostureFacade.ForFileCapabilityTrustRoot(workspace.RootPath, otherWorkspace.ServerStatePath).ReadCatalogAsync(null, 10);
        var forgedScope = snapshot with { WorkspaceScopeId = CapabilityWorkspaceScopeId.Create(new WorkspacePaths(otherWorkspace.RootPath).RootPath) };
        var forged = await facade.ReadModelContextAsync(forgedScope, ["org.embodysense/workspace-command"], ["org.embodysense/workspace-command"]);

        Assert.Equal("available", result.Status);
        var capability = Assert.Single(result.Capabilities);
        Assert.Equal("org.embodysense/workspace-command", capability.Id);
        Assert.Equal("actuator", capability.Kind);
        Assert.Equal("{\"schemaVersion\":1,\"capabilities\":[{\"id\":\"org.embodysense/workspace-command\",\"version\":\"1.0.0\",\"kind\":\"actuator\",\"description\":\"Expose governed workspace commands through the runtime tool broker.\"}]}", result.CanonicalJson);
        Assert.DoesNotContain("conversation-turn", result.CanonicalJson, StringComparison.Ordinal);
        Assert.Equal("unavailable", crossWorkspace.Status);
        Assert.Equal("capability_posture_unavailable", crossWorkspace.Error?.Code);
        Assert.Equal("unavailable", mismatchedTrust.Status);
        Assert.Equal("capability_posture_unavailable", mismatchedTrust.Error?.Code);
        Assert.Equal(crossWorkspace.Error, forged.Error);
        Assert.Empty(crossWorkspace.Capabilities);
        Assert.Empty(forged.Capabilities);
        Assert.DoesNotContain(workspace.RootPath, JsonSerializer.Serialize(new { crossWorkspace, mismatchedTrust, forged }), StringComparison.Ordinal);
        Assert.DoesNotContain(otherWorkspace.RootPath, JsonSerializer.Serialize(new { crossWorkspace, mismatchedTrust, forged }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lifecycle_facade_preview_reports_impact_without_persisting_preview_or_mutation_state()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var filesBefore = SnapshotFiles(workspace);
        var facade = CapabilityPostureFacade.ForFileCapabilityTrustRoot(workspace.RootPath, workspace.ServerStatePath);

        var preview = await facade.PreviewAsync("org.embodysense/workspace-command", "disable");
        var invalid = await facade.PreviewAsync("org.embodysense/workspace-command", "activate");
        var missing = await facade.PreviewAsync("org.example/missing", "disable");
        var absentRollback = await facade.PreviewAsync("org.embodysense/workspace-command", "rollback");

        Assert.Equal("available", preview.Status);
        var projection = Assert.IsType<CapabilityPosturePreviewSnapshot>(preview.Preview);
        Assert.Equal("disable", projection.Operation);
        Assert.True(projection.IsBlocked);
        Assert.False(projection.HasDegradation);
        Assert.Single(projection.Impacts);
        Assert.Equal("blocked", projection.Impacts[0].Outcome);
        Assert.Equal("invalid", invalid.Status);
        Assert.Equal("invalid_capability_posture_request", invalid.Error?.Code);
        Assert.Equal("not-found", missing.Status);
        Assert.Equal("capability_posture_unavailable", missing.Error?.Code);
        Assert.Equal("not-found", absentRollback.Status);
        Assert.Equal(filesBefore, SnapshotFiles(workspace));
    }

    [Fact]
    public async Task Facade_rejects_invalid_and_cancelled_requests_with_stable_results()
    {
        using var workspace = new TestWorkspace();
        var facade = CapabilityPostureFacade.ForFileCapabilityTrustRoot(workspace.RootPath, workspace.ServerStatePath);
        var admission = TestCapabilityAdmissionFactory.Create(LoopDefinition.CreateDefaultConversation().CapabilityRequirements);

        var invalidId = await facade.ReadAsync("Not Canonical");
        var invalidPage = await facade.ReadCatalogAsync(null, 0);
        var invalidUpgrade = await facade.PreviewAsync("org.embodysense/workspace-command", "upgrade");
        var invalidModel = await facade.ReadModelContextAsync(admission, ["Not Canonical"], ["org.embodysense/workspace-command"]);
        Assert.Equal("invalid", invalidId.Status);
        Assert.Equal("invalid", invalidPage.Status);
        Assert.Equal("invalid", invalidUpgrade.Status);
        Assert.Equal("invalid", invalidModel.Status);
        Assert.All([invalidId.Error, invalidPage.Error, invalidUpgrade.Error, invalidModel.Error], error => Assert.Equal("invalid_capability_posture_request", error?.Code));
        Assert.NotNull(new CapabilityPostureFacade(workspace.RootPath));
        Assert.Throws<ArgumentException>(() => CapabilityPostureFacade.ForFileCapabilityTrustRoot(workspace.RootPath, " "));
    }

    [Fact]
    public async Task Uninitialized_facade_fails_closed_without_creating_workspace_or_trust_state()
    {
        using var workspace = new TestWorkspace();
        var filesBefore = SnapshotFiles(workspace);
        var facade = CapabilityPostureFacade.ForFileCapabilityTrustRoot(workspace.RootPath, workspace.ServerStatePath);

        var catalog = await facade.ReadCatalogAsync(null, 10);
        var exact = await facade.ReadAsync("org.embodysense/workspace-command");
        var preview = await facade.PreviewAsync("org.embodysense/workspace-command", "disable");

        Assert.Equal("available", catalog.Status);
        Assert.Empty(catalog.Capabilities);
        Assert.Equal("not-found", exact.Status);
        Assert.Equal("not-found", preview.Status);
        Assert.Equal("capability_posture_unavailable", exact.Error?.Code);
        Assert.Equal("capability_posture_unavailable", preview.Error?.Code);
        Assert.Equal(filesBefore, SnapshotFiles(workspace));
    }

    private static async Task<CapabilityAdmissionSnapshot> CreateAdmissionAsync(TestWorkspace workspace)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        var service = CapabilityAdmissionFactory.Create(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath));
        var requirements = LoopDefinition.CreateDefaultConversation().CapabilityRequirements;
        var result = await service.AdmitAsync(requirements, LoopCapabilityRequirements.GetAssignedCapabilityIds(requirements));
        return Assert.IsType<CapabilityAdmissionSnapshot>(result.Snapshot);
    }

    private static IReadOnlyList<string> SnapshotFiles(TestWorkspace workspace)
    {
        return SnapshotRoot("workspace", workspace.RootPath).Concat(SnapshotRoot("server", workspace.ServerStatePath)).Order(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> SnapshotRoot(string label, string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return [];
        }
        return Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(path => $"{label}:{Path.GetRelativePath(rootPath, path)}:{Convert.ToBase64String(File.ReadAllBytes(path))}")
            .ToArray();
    }
}
