using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

internal sealed class CredentialRepairCrashTestAdapter : ICredentialActiveRunIndex, ICredentialProviderLocatorSource, ICredentialProviderLocatorVerifier, ICapabilityDependentIndexSource
{
    internal static CredentialRepairCrashTestAdapter Instance { get; } = new();

    public string Name => "credential-repair-crash";

    public Task<IReadOnlyList<string>> CaptureAsync(CredentialCapabilityBinding binding, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);

    public ValueTask<CredentialProviderLocator?> CreateAsync(string workspaceId, CredentialReferenceId referenceId, CredentialProviderId providerId, CancellationToken cancellationToken) => ValueTask.FromResult<CredentialProviderLocator?>(null);

    public Task<IReadOnlyList<CapabilityDependent>> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CapabilityDependent>>([]);

    public ValueTask<bool> VerifyAsync(string workspaceIdentity, CredentialReferenceId referenceId, CredentialProviderId providerId, CredentialProviderLocator locator, CancellationToken cancellationToken) => ValueTask.FromResult(true);
}
