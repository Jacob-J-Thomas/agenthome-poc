using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Tests.Credentials;

internal sealed class StubCredentialProviderLocatorSource : ICredentialProviderLocatorSource, ICredentialProviderLocatorVerifier
{
    internal bool Available { get; set; } = true;
    internal bool CancelAfterNextEffect { get; set; }
    internal bool FailAfterNextEffect { get; set; }
    internal int CreateCount { get; private set; }
    internal Action? BeforeCreate { get; set; }

    public ValueTask<CredentialProviderLocator?> CreateAsync(string workspaceId, CredentialReferenceId referenceId, CredentialProviderId providerId, CancellationToken cancellationToken)
    {
        BeforeCreate?.Invoke();
        CreateCount++;
        if (CancelAfterNextEffect)
        {
            CancelAfterNextEffect = false;
            throw new OperationCanceledException("Injected cancellation after provider locator effect.");
        }
        if (FailAfterNextEffect)
        {
            FailAfterNextEffect = false;
            throw new InvalidOperationException("Injected failure after provider locator effect.");
        }
        if (!Available)
        {
            return ValueTask.FromResult<CredentialProviderLocator?>(null);
        }
        Assert.True(CredentialProviderLocator.TryParse("loc_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", out var locator));
        return ValueTask.FromResult(locator);
    }

    public ValueTask<bool> VerifyAsync(string workspaceIdentity, CredentialReferenceId referenceId, CredentialProviderId providerId, CredentialProviderLocator locator, CancellationToken cancellationToken) => ValueTask.FromResult(true);
}
