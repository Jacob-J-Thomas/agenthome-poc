using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Persistence.Credentials;

namespace EmbodySense.Core.Persistence.Tests.Credentials;

internal sealed class SecureFakeCredentialValueProvider : ICredentialValueProvider, IDisposable
{
    private readonly WindowsCredentialValueProvider _provider;

    internal SecureFakeCredentialValueProvider(bool isSupported = true, int maxValueByteLength = 2_560)
    {
        Store = new ScriptedWindowsCredentialStore(isSupported, maxValueByteLength);
        _provider = new WindowsCredentialValueProvider(Store);
    }

    internal ScriptedWindowsCredentialStore Store { get; }

    public ValueTask<CredentialProviderResult> CreateAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken) => _provider.CreateAsync(request, source, cancellationToken);
    public ValueTask<CredentialProviderResult> ReplaceAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken) => _provider.ReplaceAsync(request, source, cancellationToken);
    public ValueTask<CredentialProviderResult> UseAsync(CredentialProviderUseRequest request, ICredentialTrustedUseConsumer trustedConsumer, CancellationToken cancellationToken) => _provider.UseAsync(request, trustedConsumer, cancellationToken);
    public ValueTask<CredentialProviderResult> DeleteAsync(CredentialProviderDeleteRequest request, CancellationToken cancellationToken) => _provider.DeleteAsync(request, cancellationToken);
    public ValueTask<CredentialProviderHealthResult> GetHealthAsync(CredentialProviderUseRequest request, CancellationToken cancellationToken) => _provider.GetHealthAsync(request, cancellationToken);
    public void Dispose() => Store.Dispose();
}
