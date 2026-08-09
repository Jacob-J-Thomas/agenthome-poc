using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Credentials;
using EmbodySense.Core.Startup.Credentials;

namespace EmbodySense.IntegrationTests.Architecture;

public sealed class CredentialReconciliationPublicSurfaceTests
{
    private static readonly string[] _forbiddenReconciliationInjectionTerms = ["ReconciliationPolicy", "ReconciliationAuthorizer", "ReconciliationProof", "ReconciliationVerifier", "ReconciliationCapability", "ReconciliationClaim"];

    [Fact]
    public void PublicCredentialLifecycleCompositionIsClosedToRegistryAndActorAuthorityInjection()
    {
        var exportedTypeNames = typeof(CredentialLifecycleService).Assembly.ExportedTypes
            .Concat(typeof(CredentialRegistryStore).Assembly.ExportedTypes)
            .Concat(typeof(CredentialLifecycleFactory).Assembly.ExportedTypes)
            .Select(type => type.FullName ?? type.Name)
            .ToArray();
        var registrySurface = typeof(CredentialRegistryStore).GetMembers().Select(member => member.ToString() ?? member.Name).ToArray();
        var mutationSurface = typeof(CredentialRegistryMutation).GetMembers().Select(member => member.ToString() ?? member.Name).ToArray();
        var serviceSurface = typeof(CredentialLifecycleService).GetMembers().Select(member => member.ToString() ?? member.Name).ToArray();
        var startupFactoryMembers = typeof(CredentialLifecycleFactory).GetMembers().Where(member => string.Equals(member.Name, nameof(CredentialLifecycleFactory.Create), StringComparison.Ordinal)).ToArray();
        var persistenceFactoryMembers = typeof(CredentialLifecyclePersistenceFactory).GetMembers().Where(member => string.Equals(member.Name, nameof(CredentialLifecyclePersistenceFactory.Create), StringComparison.Ordinal)).ToArray();
        var persistenceFactorySurface = typeof(CredentialLifecyclePersistenceFactory).GetMembers().Where(member => member.Name.StartsWith("Create", StringComparison.Ordinal)).ToArray();
        var factorySurface = startupFactoryMembers.Concat(persistenceFactorySurface).Select(member => member.ToString() ?? member.Name).ToArray();
        Func<WorkspacePaths, FileCapabilityCatalogTrustProvider, ICredentialProviderLocatorVerifier, ICredentialValueProvider, ICredentialProviderLocatorSource, ICapabilityDependentIndex, ICredentialActiveRunIndex, IAuditLog, TimeProvider?, CredentialLifecycleService> startupFactory = CredentialLifecycleFactory.Create;
        Func<WorkspacePaths, FileCapabilityCatalogTrustProvider, ICredentialProviderLocatorVerifier, ICredentialValueProvider, ICredentialProviderLocatorSource, ICapabilityDependentIndex, ICredentialActiveRunIndex, IAuditLog, TimeProvider?, CredentialLifecycleService> persistenceFactory = CredentialLifecyclePersistenceFactory.Create;

        foreach (var term in _forbiddenReconciliationInjectionTerms)
        {
            Assert.DoesNotContain(exportedTypeNames, name => name.Contains(term, StringComparison.Ordinal));
            Assert.DoesNotContain(serviceSurface, signature => signature.Contains(term, StringComparison.Ordinal));
            Assert.DoesNotContain(factorySurface, signature => signature.Contains(term, StringComparison.Ordinal));
        }
        Assert.Contains(serviceSurface, signature => signature.Contains("Void .ctor(EmbodySense.Core.Application.Credentials.ICredentialRegistryStore", StringComparison.Ordinal));
        Assert.Single(startupFactoryMembers);
        Assert.Single(persistenceFactoryMembers);
        Assert.NotNull(startupFactory);
        Assert.NotNull(persistenceFactory);
        Assert.DoesNotContain(registrySurface, signature => signature.Contains("MutateLifecycle", StringComparison.Ordinal));
        Assert.DoesNotContain(factorySurface, signature => signature.Contains("Authenticate", StringComparison.Ordinal));
        Assert.DoesNotContain(factorySurface, signature => signature.Contains(nameof(ICapabilityCatalogTrustProvider), StringComparison.Ordinal));
        Assert.DoesNotContain(mutationSurface, signature => signature.Contains("ReconciliationProof", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostileAuthenticatedRepairRegistryDoesNotExposePersistenceMutationAuthority()
    {
        var registry = new HostileCredentialRegistryStore();
        var provider = new CountingCredentialValueProvider();
        var state = await registry.ReadAsync();
        var authentication = await registry.AuthenticateActorAsync(Environment.UserName, default);
        var interruptedRepair = Assert.Single(state.Operations, operation => operation.LifecycleOperation == (int)CredentialLifecycleOperationKind.Repair && operation.LifecyclePhase == CredentialLifecycleMutationPhase.Intent);

        Assert.True(state.Succeeded);
        Assert.Equal(CredentialActorAuthentication.AuthenticatedUser, authentication);
        Assert.Equal(CredentialProviderHealthStatus.NeedsRepair, Assert.Single(state.Entries).Health);
        Assert.Equal(interruptedRepair.OperationId, interruptedRepair.LifecycleIntentOperationId);
        Assert.DoesNotContain(typeof(CredentialLifecycleFactory).GetMembers(), member => (member.ToString() ?? member.Name).Contains(nameof(ICredentialRegistryStore), StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(CredentialLifecyclePersistenceFactory).GetMembers(), member => (member.ToString() ?? member.Name).Contains(nameof(ICredentialRegistryStore), StringComparison.Ordinal));
        Assert.Equal(0, registry.MutationCount);
        Assert.Equal(0, provider.DeleteCount);
    }
}
