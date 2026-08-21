using EmbodySense.Core.Application.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Startup.Credentials;
using EmbodySense.Core.Startup.Credentials.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Credentials;

public sealed class CredentialBrokerFactoryTests
{
    [Fact]
    public void Factory_composes_without_workspace_mutation_and_rejects_missing_authority()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
        var provider = new CredentialLifecycleFactoryTestAdapter();
        var authority = new CredentialBrokerFactoryTestAuthority();
        var providerId = ProviderId("org.embodysense.test-secure");

        var broker = CredentialBrokerFactory.Create(
            paths,
            trust,
            provider,
            authority,
            authority,
            [new CredentialValueProviderRegistration(providerId, provider)]);

        Assert.NotNull(broker);
        Assert.False(File.Exists(paths.CredentialRegistryDocumentPath));
        Assert.False(Directory.Exists(paths.CredentialLeaseAttemptsPath));
        Assert.Throws<ArgumentNullException>(() => CredentialBrokerFactory.Create(paths, trust, provider, authority, null!, [new CredentialValueProviderRegistration(providerId, provider)]));
    }

    [Fact]
    public async Task Configured_resolver_has_no_unconfigured_provider_fallback()
    {
        var provider = new CredentialLifecycleFactoryTestAdapter();
        var configuredId = ProviderId("org.embodysense.test-secure");
        var resolver = new ConfiguredCredentialValueProviderResolver([new CredentialValueProviderRegistration(configuredId, provider)]);

        var resolved = await resolver.ResolveAsync("workspace-1", ReferenceId(), configuredId);
        var unconfigured = await resolver.ResolveAsync("workspace-1", ReferenceId(), ProviderId("org.embodysense.other"));

        Assert.Equal(CredentialValueProviderResolutionStatus.Resolved, resolved.Status);
        Assert.Same(provider, resolved.Provider);
        Assert.Equal(CredentialValueProviderResolutionStatus.NotConfigured, unconfigured.Status);
        Assert.Null(unconfigured.Provider);
    }

    private static CredentialProviderId ProviderId(string value)
        => CredentialProviderId.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();

    private static CredentialReferenceId ReferenceId()
        => CredentialReferenceId.TryParse("credential-factory", out var parsed, out _) ? parsed! : throw new InvalidOperationException();
}
