using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

internal sealed class CredentialCreateCrashTestAdapter(string locatorMarker) : ICredentialActiveRunIndex, ICredentialProviderLocatorSource, ICredentialProviderLocatorVerifier, ICapabilityDependentIndexSource
{
    public string Name => "credential-create-crash";

    public Task<IReadOnlyList<string>> CaptureAsync(CredentialCapabilityBinding binding, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);

    public ValueTask<CredentialProviderLocator?> CreateAsync(string workspaceId, CredentialReferenceId referenceId, CredentialProviderId providerId, CancellationToken cancellationToken)
    {
        using var marker = new FileStream(locatorMarker, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        marker.Write(Encoding.UTF8.GetBytes(referenceId.Value));
        marker.Flush(flushToDisk: true);
        return ValueTask.FromResult<CredentialProviderLocator?>(CredentialProviderLocator.TryParse("loc_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", out var locator) ? locator : throw new InvalidOperationException());
    }

    public Task<IReadOnlyList<CapabilityDependent>> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CapabilityDependent>>([]);

    public ValueTask<bool> VerifyAsync(string workspaceIdentity, CredentialReferenceId referenceId, CredentialProviderId providerId, CredentialProviderLocator locator, CancellationToken cancellationToken) => ValueTask.FromResult(true);
}
