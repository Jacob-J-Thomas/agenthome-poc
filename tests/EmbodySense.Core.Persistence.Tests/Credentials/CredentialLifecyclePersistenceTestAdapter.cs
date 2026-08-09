using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Persistence.Tests.Credentials;

internal sealed class CredentialLifecyclePersistenceTestAdapter : ICredentialActiveRunIndex, ICredentialProviderLocatorSource, ICredentialProviderLocatorVerifier
{
    internal static CredentialLifecyclePersistenceTestAdapter Instance { get; } = new();

    public Task<IReadOnlyList<string>> CaptureAsync(CredentialCapabilityBinding binding, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);

    public ValueTask<CredentialProviderLocator?> CreateAsync(string workspaceId, CredentialReferenceId referenceId, CredentialProviderId providerId, CancellationToken cancellationToken) => ValueTask.FromResult<CredentialProviderLocator?>(null);

    public ValueTask<bool> VerifyAsync(string workspaceIdentity, CredentialReferenceId referenceId, CredentialProviderId providerId, CredentialProviderLocator locator, CancellationToken cancellationToken) => ValueTask.FromResult(true);
}
