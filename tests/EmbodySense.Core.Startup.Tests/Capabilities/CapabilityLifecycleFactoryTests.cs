using EmbodySense.Core.Clients.Capabilities;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Capabilities;

public sealed class CapabilityLifecycleFactoryTests
{
    [Fact]
    public void Factory_composes_real_adapters_without_mutating_workspace_during_construction()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var catalogTrust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
        var artifactTrust = new FileCapabilityArtifactStateTrustProvider(workspace.ServerStatePath);
        using var verifier = new ConfiguredCapabilityArtifactTrustVerifier(new Dictionary<string, string>());

        var service = CapabilityLifecycleFactory.Create(paths, catalogTrust, artifactTrust, verifier, new AuditLog(paths));
        var selection = CapabilityLifecycleFactory.CreateSelection(paths, catalogTrust, artifactTrust, verifier, new AuditLog(paths));

        Assert.NotNull(service);
        Assert.NotNull(selection);
        Assert.False(Directory.Exists(paths.CapabilityCatalogPath));
        Assert.False(Directory.Exists(workspace.ServerStatePath));
    }
}
