using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Persistence.Tests.Credentials;

internal sealed class CoordinatedCredentialCreateAdapter(bool blockLocator = false) : ICredentialActiveRunIndex, ICredentialProviderLocatorSource, ICredentialProviderLocatorVerifier, ICapabilityDependentIndexSource
{
    private readonly TaskCompletionSource _locatorEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _locatorRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal int CreateCount { get; private set; }
    public string Name => "credential-create-coordination";

    public Task<IReadOnlyList<string>> CaptureAsync(CredentialCapabilityBinding binding, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);

    public async ValueTask<CredentialProviderLocator?> CreateAsync(string workspaceId, CredentialReferenceId referenceId, CredentialProviderId providerId, CancellationToken cancellationToken)
    {
        CreateCount++;
        _locatorEntered.TrySetResult();
        if (blockLocator)
        {
            await _locatorRelease.Task.WaitAsync(cancellationToken);
        }
        return CredentialProviderLocator.TryParse("loc_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", out var locator) ? locator : throw new InvalidOperationException();
    }

    public Task<IReadOnlyList<CapabilityDependent>> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CapabilityDependent>>([]);
    public ValueTask<bool> VerifyAsync(string workspaceIdentity, CredentialReferenceId referenceId, CredentialProviderId providerId, CredentialProviderLocator locator, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    internal Task WaitForLocatorAsync(CancellationToken cancellationToken) => _locatorEntered.Task.WaitAsync(cancellationToken);
    internal void ReleaseLocator() => _locatorRelease.TrySetResult();
}
