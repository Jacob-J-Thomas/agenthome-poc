using System.Text.Json;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Capabilities.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Capabilities;

public sealed class CapabilityCatalogFacadeTests
{
    [Fact]
    public async Task Facade_creates_and_replays_server_owned_preview_without_exposing_trusted_artifact_fields()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var capabilityId = await InstallTestCapabilityAsync(workspace);
        var facade = CapabilityCatalogFacade.ForFileCapabilityTrustRoot(workspace.RootPath, workspace.ServerStatePath);
        var selection = new CapabilityLifecycleSelectionInput(
            "web-capability-preview",
            "disable",
            capabilityId.Value,
            null);

        var created = await facade.PreviewAsync(selection);
        var replayed = await facade.PreviewAsync(selection);
        var conflictingSelection = await facade.PreviewAsync(selection with { Operation = "rollback" });

        Assert.True(created.Status == "ready", $"{created.Status}: {created.Error?.Message}");
        var preview = Assert.IsType<CapabilityLifecyclePreviewSnapshot>(created.Preview);
        Assert.Equal("web-capability-preview", preview.OperationId);
        Assert.Equal("disable", preview.Operation);
        Assert.Equal(capabilityId.Value, preview.CapabilityId);
        Assert.False(preview.IsBlocked);
        Assert.Empty(preview.Impacts);
        var replayedPreview = Assert.IsType<CapabilityLifecyclePreviewSnapshot>(replayed.Preview);
        Assert.Equal(preview.OperationId, replayedPreview.OperationId);
        Assert.Equal(preview.LifecycleRevision, replayedPreview.LifecycleRevision);
        Assert.Equal(preview.DependentSetRevision, replayedPreview.DependentSetRevision);
        Assert.Equal(preview.DependentSetHash, replayedPreview.DependentSetHash);
        Assert.Equal(preview.PreviewHash, replayedPreview.PreviewHash);
        Assert.Equal("conflict", conflictingSelection.Status);
        var json = JsonSerializer.Serialize(created);
        Assert.DoesNotContain("artifactDigest", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("targetDescriptor", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(workspace.RootPath, json, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.ServerStatePath, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirmation_requires_explicit_exact_preview_identity_and_preserves_blocked_state()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var capabilityId = await InstallTestCapabilityAsync(workspace);
        var facade = CapabilityCatalogFacade.ForFileCapabilityTrustRoot(workspace.RootPath, workspace.ServerStatePath);
        var selection = new CapabilityLifecycleSelectionInput(
            "web-capability-confirm",
            "disable",
            capabilityId.Value,
            null);
        var preview = Assert.IsType<CapabilityLifecyclePreviewSnapshot>((await facade.PreviewAsync(selection)).Preview);

        var declined = await facade.ConfirmAsync(Confirmation(selection, preview) with { Confirmed = false });
        var malformed = await facade.ConfirmAsync(Confirmation(selection, preview) with { PreviewHash = "sha256:not-a-digest" });
        var stale = await facade.ConfirmAsync(Confirmation(selection, preview) with { PreviewHash = ChangeDigest(preview.PreviewHash) });
        var applied = await facade.ConfirmAsync(Confirmation(selection, preview));
        var replayed = await facade.ConfirmAsync(Confirmation(selection, preview));
        var posture = await facade.ReadAsync(selection.CapabilityId);

        Assert.Equal("invalid", declined.Status);
        Assert.False(declined.IsCommitted);
        Assert.Equal("invalid", malformed.Status);
        Assert.False(malformed.IsCommitted);
        Assert.Equal("conflict", stale.Status);
        Assert.False(stale.IsCommitted);
        Assert.Equal("applied", applied.Status);
        Assert.True(applied.IsCommitted);
        Assert.Equal("replayed", replayed.Status);
        Assert.True(replayed.IsCommitted);
        Assert.Equal("applied", replayed.ReplayedOutcome);
        Assert.False(posture.Capability!.IsLifecycleEnabled);
    }

    [Fact]
    public async Task Discard_retires_exact_preview_without_mutation_and_allows_a_new_operation()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var capabilityId = await InstallTestCapabilityAsync(workspace);
        var facade = CapabilityCatalogFacade.ForFileCapabilityTrustRoot(workspace.RootPath, workspace.ServerStatePath);
        var selection = new CapabilityLifecycleSelectionInput("web-capability-discard", "disable", capabilityId.Value, null);
        var preview = Assert.IsType<CapabilityLifecyclePreviewSnapshot>((await facade.PreviewAsync(selection)).Preview);

        var stale = await facade.DiscardAsync(Discard(selection, preview) with { PreviewHash = ChangeDigest(preview.PreviewHash) });
        var discarded = await facade.DiscardAsync(Discard(selection, preview));
        var replayed = await facade.DiscardAsync(Discard(selection, preview));
        var replacement = await facade.PreviewAsync(selection with { OperationId = "web-capability-after-discard" });
        var posture = await facade.ReadAsync(selection.CapabilityId);

        Assert.Equal("conflict", stale.Status);
        Assert.Equal("discarded", discarded.Status);
        Assert.False(discarded.IsCommitted);
        Assert.Equal("discarded", replayed.Status);
        Assert.Equal("ready", replacement.Status);
        Assert.True(posture.Capability!.IsLifecycleEnabled);
    }

    [Fact]
    public async Task Facade_rejects_malformed_selection_shapes_and_cancelled_reads()
    {
        using var workspace = new TestWorkspace();
        var facade = CapabilityCatalogFacade.ForFileCapabilityTrustRoot(workspace.RootPath, workspace.ServerStatePath);

        Assert.Equal("invalid", (await facade.PreviewAsync(new CapabilityLifecycleSelectionInput("bad", "activate", "Not Canonical", null))).Status);
        Assert.Equal("invalid", (await facade.PreviewAsync(new CapabilityLifecycleSelectionInput("bad-upgrade", "upgrade", "org.embodysense/workspace-command", null))).Status);
        Assert.Equal("invalid", (await facade.PreviewAsync(new CapabilityLifecycleSelectionInput("bad-disable", "disable", "org.embodysense/workspace-command", "2.0.0"))).Status);
        Assert.Equal("invalid", (await facade.PreviewAsync(new CapabilityLifecycleSelectionInput(new string('a', CapabilityArtifactManifestValidator.MaximumOperationIdCharacters + 1), "disable", "org.embodysense/workspace-command", null))).Status);
        Assert.Equal("invalid", (await facade.PreviewAsync(new CapabilityLifecycleSelectionInput("oversized-capability", "disable", $"org.example/{new string('a', CapabilityContractLimits.MaxCapabilityIdCharacters)}", null))).Status);
        Assert.Equal("invalid", (await facade.PreviewAsync(new CapabilityLifecycleSelectionInput("oversized-version", "upgrade", "org.embodysense/workspace-command", new string('1', CapabilityContractLimits.MaxVersionCharacters + 1)))).Status);
        Assert.Equal("invalid", (await facade.DiscardAsync(new CapabilityLifecycleDiscardInput("bad-discard", "disable", "org.embodysense/workspace-command", null, 0, 0, 0, 0, "bad", "bad"))).Status);
        foreach (var operation in new[] { "enable", "rollback", "remove" })
        {
            Assert.NotEqual("invalid", (await facade.PreviewAsync(new CapabilityLifecycleSelectionInput($"valid-{operation}", operation, "org.embodysense/workspace-command", null))).Status);
        }
        Assert.NotNull(new CapabilityCatalogFacade(workspace.RootPath));
        Assert.Throws<ArgumentException>(() => CapabilityCatalogFacade.ForFileCapabilityTrustRoot(workspace.RootPath, " "));
        await Assert.ThrowsAsync<ArgumentNullException>(() => facade.DiscardAsync(null!));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => facade.ReadCatalogAsync(null, 10, cancellation.Token));
    }

    [Fact]
    public async Task Facade_catalog_is_confined_to_its_exact_workspace()
    {
        using var first = new TestWorkspace();
        using var second = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(first.ServerStatePath).InitializeAsync(first.RootPath);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(second.ServerStatePath).InitializeAsync(second.RootPath);
        var firstOnlyCapability = await InstallTestCapabilityAsync(first);
        var firstFacade = CapabilityCatalogFacade.ForFileCapabilityTrustRoot(first.RootPath, first.ServerStatePath);
        var secondFacade = CapabilityCatalogFacade.ForFileCapabilityTrustRoot(second.RootPath, second.ServerStatePath);

        var firstPage = await firstFacade.ReadCatalogAsync(null, 50);
        var secondPage = await secondFacade.ReadCatalogAsync(null, 50);

        Assert.Contains(firstPage.Capabilities, item => item.Id == firstOnlyCapability.Value);
        Assert.DoesNotContain(secondPage.Capabilities, item => item.Id == firstOnlyCapability.Value);
        Assert.Equal("not-found", (await secondFacade.PreviewAsync(new CapabilityLifecycleSelectionInput("missing-workspace-capability", "disable", firstOnlyCapability.Value))).Status);
        Assert.DoesNotContain(first.RootPath, JsonSerializer.Serialize(secondPage), StringComparison.Ordinal);
        Assert.DoesNotContain(first.ServerStatePath, JsonSerializer.Serialize(secondPage), StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_snapshot_defensively_copies_bounded_dependent_impacts()
    {
        var mutableImpacts = new List<CapabilityLifecycleImpactSnapshot>
        {
            new("loop", "default-conversation", "definition-v1", "required", "*", false, "assigned-definition", "blocked")
        };

        var snapshot = new CapabilityLifecyclePreviewSnapshot(
            "preview-model-copy",
            "disable",
            "org.example/runtime",
            null,
            1,
            1,
            1,
            1,
            CapabilityIntegrityDigest.Compute("dependents"u8).Value,
            CapabilityIntegrityDigest.Compute("preview"u8).Value,
            true,
            false,
            mutableImpacts,
            "Blocked.");
        mutableImpacts.Clear();

        var impact = Assert.Single(snapshot.Impacts);
        Assert.Equal("default-conversation", impact.DependentIdentity);
        Assert.True(snapshot.IsBlocked);
    }

    private static CapabilityLifecycleConfirmationInput Confirmation(
        CapabilityLifecycleSelectionInput selection,
        CapabilityLifecyclePreviewSnapshot preview) => new(
            selection.OperationId,
            selection.Operation,
            selection.CapabilityId,
            selection.TargetVersion,
            preview.BaselineCatalogRevision,
            preview.BaselineActivationRevision,
            preview.LifecycleRevision,
            preview.DependentSetRevision,
            preview.DependentSetHash,
            preview.PreviewHash,
            true);

    private static CapabilityLifecycleDiscardInput Discard(
        CapabilityLifecycleSelectionInput selection,
        CapabilityLifecyclePreviewSnapshot preview) => new(
            selection.OperationId,
            selection.Operation,
            selection.CapabilityId,
            selection.TargetVersion,
            preview.BaselineCatalogRevision,
            preview.BaselineActivationRevision,
            preview.LifecycleRevision,
            preview.DependentSetRevision,
            preview.DependentSetHash,
            preview.PreviewHash);

    private static string ChangeDigest(string digest)
    {
        var replacement = digest[^1] == '0' ? '1' : '0';
        return $"{digest[..^1]}{replacement}";
    }

    private static async Task<CapabilityId> InstallTestCapabilityAsync(TestWorkspace workspace)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        var catalogTrust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
        var artifactTrust = new FileCapabilityArtifactStateTrustProvider(workspace.ServerStatePath);
        var stage = CapabilityAdmissionLifecycleTestData.Stage();
        var catalog = new CapabilityCatalogService(new CapabilityCatalogStore(paths, catalogTrust));
        var revision = (await catalog.ReadAsync(null, 1)).Page!.CatalogRevision;
        revision = (await catalog.DeclareAsync(stage.Manifest.Descriptor, revision, "declare-web-capability")).CatalogRevision!.Value;
        revision = (await catalog.InstallAsync(stage.Manifest.Descriptor.Id, revision, "install-web-capability")).CatalogRevision!.Value;
        revision = (await catalog.VerifyAsync(stage.Manifest.Descriptor.Id, revision, "verify-web-capability")).CatalogRevision!.Value;
        revision = (await catalog.EnableAsync(stage.Manifest.Descriptor.Id, revision, "enable-web-capability")).CatalogRevision!.Value;
        Assert.Equal(CapabilityCatalogMutationStatus.Applied, (await catalog.MarkHealthyAsync(stage.Manifest.Descriptor.Id, revision, "healthy-web-capability")).Status);
        var artifacts = new CapabilityArtifactStore(paths, artifactTrust, new AlwaysTrustedLifecycleArtifactVerifier());
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await artifacts.StageAsync(stage)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await artifacts.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate-web-capability"))).Status);
        return stage.Manifest.Descriptor.Id;
    }
}
