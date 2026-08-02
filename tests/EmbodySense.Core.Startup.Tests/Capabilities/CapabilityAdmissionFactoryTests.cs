using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Capabilities;

public sealed class CapabilityAdmissionFactoryTests
{
    [Fact]
    public async Task Production_admission_projection_rejects_capabilities_after_lifecycle_disable_and_remove()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var catalogTrust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
        var artifactTrust = new FileCapabilityArtifactStateTrustProvider(workspace.ServerStatePath);
        var verifier = new AlwaysTrustedLifecycleArtifactVerifier();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var stage = CapabilityAdmissionLifecycleTestData.Stage();
        var catalog = new CapabilityCatalogService(new CapabilityCatalogStore(paths, catalogTrust));
        await MakeEffectReadyAsync(catalog, stage.Manifest.Descriptor);
        var artifacts = new CapabilityArtifactStore(paths, artifactTrust, verifier);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await artifacts.StageAsync(stage)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await artifacts.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate-runtime-lifecycle"))).Status);
        var baseline = await new CapabilityLifecycleBaselineSource(new CapabilityCatalogStore(paths, catalogTrust), new CapabilityArtifactStore(paths, artifactTrust, verifier)).ReadAsync(stage.Manifest.Descriptor.Id);
        Assert.NotNull(baseline);
        var lifecycle = CapabilityLifecycleFactory.Create(paths, catalogTrust, artifactTrust, verifier, new AuditLog(paths));
        var admission = CapabilityAdmissionFactory.Create(paths, catalogTrust);
        var requirements = CapabilityAdmissionLifecycleTestData.Requirements(stage.Manifest.Descriptor.Id);
        Assert.True((await admission.AdmitAsync(requirements, [stage.Manifest.Descriptor.Id])).IsAdmitted);

        var disable = await lifecycle.PreviewAsync(new CapabilityLifecyclePreviewRequest("runtime-disable", CapabilityLifecycleOperationKind.Disable, stage.Manifest.Descriptor.Id));
        Assert.Equal(CapabilityLifecyclePreviewStatus.Ready, disable.Status);
        var disabled = await lifecycle.MutateAsync(disable);
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, disabled.Status);
        Assert.False((await admission.AdmitAsync(requirements, [stage.Manifest.Descriptor.Id])).IsAdmitted);

        var rollback = await lifecycle.PreviewAsync(new CapabilityLifecyclePreviewRequest("runtime-rollback-disable", CapabilityLifecycleOperationKind.Rollback, stage.Manifest.Descriptor.Id));
        Assert.Equal(CapabilityLifecyclePreviewStatus.Ready, rollback.Status);
        var rolledBack = await lifecycle.MutateAsync(rollback);
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, rolledBack.Status);
        Assert.True((await admission.AdmitAsync(requirements, [stage.Manifest.Descriptor.Id])).IsAdmitted);

        var remove = await lifecycle.PreviewAsync(new CapabilityLifecyclePreviewRequest("runtime-remove", CapabilityLifecycleOperationKind.Remove, stage.Manifest.Descriptor.Id));
        Assert.Equal(CapabilityLifecyclePreviewStatus.Ready, remove.Status);
        var removed = await lifecycle.MutateAsync(remove);
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, removed.Status);
        Assert.False((await admission.AdmitAsync(requirements, [stage.Manifest.Descriptor.Id])).IsAdmitted);
    }

    private static async Task MakeEffectReadyAsync(CapabilityCatalogService catalog, EmbodySense.Core.Common.Capabilities.Models.CapabilityDescriptor descriptor)
    {
        var revision = (await catalog.ReadAsync(null, 1)).Page!.CatalogRevision;
        revision = (await catalog.DeclareAsync(descriptor, revision, "declare-runtime-lifecycle")).CatalogRevision!.Value;
        revision = (await catalog.InstallAsync(descriptor.Id, revision, "install-runtime-lifecycle")).CatalogRevision!.Value;
        revision = (await catalog.VerifyAsync(descriptor.Id, revision, "verify-runtime-lifecycle")).CatalogRevision!.Value;
        revision = (await catalog.EnableAsync(descriptor.Id, revision, "enable-runtime-lifecycle")).CatalogRevision!.Value;
        Assert.Equal(CapabilityCatalogMutationStatus.Applied, (await catalog.MarkHealthyAsync(descriptor.Id, revision, "healthy-runtime-lifecycle")).Status);
    }
}
