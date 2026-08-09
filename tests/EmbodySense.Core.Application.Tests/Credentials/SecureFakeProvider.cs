using System.Security.Cryptography;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Tests.Credentials;

internal sealed class SecureFakeProvider : ICredentialValueProvider
{
    private readonly Dictionary<CredentialReferenceId, byte[]> _values = [];

    internal int CreateCount { get; private set; }
    internal int ReplaceCount { get; private set; }
    internal int UseCount { get; private set; }
    internal int DeleteCount { get; private set; }
    internal int HealthCount { get; private set; }
    internal CredentialFailureCode? NextReplaceFailure { get; set; }
    internal CredentialFailureCode? NextDeleteFailure { get; set; }
    internal bool CancelAfterNextCreateEffect { get; set; }
    internal bool CancelAfterNextReplaceEffect { get; set; }
    internal bool CancelAfterNextDeleteEffect { get; set; }
    internal bool CancelNextHealth { get; set; }
    internal CredentialProviderHealthStatus? NextHealthFailure { get; set; }
    internal bool ReturnNullHealth { get; set; }
    internal Action? BeforeMutation { get; set; }

    public ValueTask<CredentialProviderResult> CreateAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken)
    {
        BeforeMutation?.Invoke();
        CreateCount++;
        var result = WriteAsync(request, source, replace: false, cancellationToken);
        if (CancelAfterNextCreateEffect && result.Result.Succeeded)
        {
            CancelAfterNextCreateEffect = false;
            throw new OperationCanceledException("Injected cancellation after provider create effect.");
        }
        return result;
    }

    public ValueTask<CredentialProviderResult> ReplaceAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken)
    {
        BeforeMutation?.Invoke();
        ReplaceCount++;
        if (NextReplaceFailure is { } failure)
        {
            NextReplaceFailure = null;
            return ValueTask.FromResult(Failed(failure));
        }
        var result = WriteAsync(request, source, replace: true, cancellationToken);
        if (CancelAfterNextReplaceEffect && result.Result.Succeeded)
        {
            CancelAfterNextReplaceEffect = false;
            throw new OperationCanceledException("Injected cancellation after provider replace effect.");
        }
        return result;
    }

    public ValueTask<CredentialProviderResult> UseAsync(CredentialProviderUseRequest request, ICredentialTrustedUseConsumer trustedConsumer, CancellationToken cancellationToken)
    {
        UseCount++;
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(Failed(CredentialFailureCode.Unavailable));
        }

        if (!CredentialPortContractValidator.Validate(request).IsValid || trustedConsumer is null)
        {
            return ValueTask.FromResult(Failed(CredentialFailureCode.InvalidRequest));
        }

        if (!_values.TryGetValue(request.ReferenceId, out var value))
        {
            return ValueTask.FromResult(Failed(CredentialFailureCode.NotFound));
        }

        try
        {
            trustedConsumer.Use(value);
            return ValueTask.FromResult(CredentialProviderResult.Success());
        }
        catch
        {
            return ValueTask.FromResult(Failed(CredentialFailureCode.CallbackFailed));
        }
    }

    public ValueTask<CredentialProviderResult> DeleteAsync(CredentialProviderDeleteRequest request, CancellationToken cancellationToken)
    {
        BeforeMutation?.Invoke();
        DeleteCount++;
        if (NextDeleteFailure is { } failure)
        {
            NextDeleteFailure = null;
            return ValueTask.FromResult(Failed(failure));
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(Failed(CredentialFailureCode.Unavailable));
        }

        if (!CredentialPortContractValidator.Validate(request).IsValid)
        {
            return ValueTask.FromResult(Failed(CredentialFailureCode.InvalidRequest));
        }

        if (_values.Remove(request.ReferenceId, out var removed))
        {
            CryptographicOperations.ZeroMemory(removed);
        }

        if (CancelAfterNextDeleteEffect)
        {
            CancelAfterNextDeleteEffect = false;
            throw new OperationCanceledException("Injected cancellation after provider delete effect.");
        }

        return ValueTask.FromResult(CredentialProviderResult.Success());
    }

    public ValueTask<CredentialProviderHealthResult> GetHealthAsync(CredentialProviderUseRequest request, CancellationToken cancellationToken)
    {
        HealthCount++;
        if (CancelNextHealth)
        {
            CancelNextHealth = false;
            return ValueTask.FromResult(CredentialProviderHealthResult.Failed(CredentialProviderHealthStatus.Unavailable, CredentialFailure.FromCode(CredentialFailureCode.Unavailable)));
        }
        if (NextHealthFailure is { } failedHealth)
        {
            NextHealthFailure = null;
            return ValueTask.FromResult(CredentialProviderHealthResult.Failed(failedHealth, CredentialFailure.FromCode(CredentialFailureCode.Unavailable)));
        }
        if (ReturnNullHealth)
        {
            return ValueTask.FromResult<CredentialProviderHealthResult>(null!);
        }
        if (cancellationToken.IsCancellationRequested || !CredentialPortContractValidator.Validate(request).IsValid)
        {
            return ValueTask.FromResult(CredentialProviderHealthResult.Failed(CredentialProviderHealthStatus.Unavailable, CredentialFailure.FromCode(CredentialFailureCode.Unavailable)));
        }

        var status = _values.ContainsKey(request.ReferenceId) ? CredentialProviderHealthStatus.Available : CredentialProviderHealthStatus.Missing;
        return ValueTask.FromResult(status == CredentialProviderHealthStatus.Available ? CredentialProviderHealthResult.Available() : CredentialProviderHealthResult.Missing());
    }

    private ValueTask<CredentialProviderResult> WriteAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, bool replace, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(Failed(CredentialFailureCode.Unavailable));
        }

        if (!CredentialPortContractValidator.Validate(request).IsValid || source is null || replace != _values.ContainsKey(request.ReferenceId))
        {
            return ValueTask.FromResult(Failed(CredentialFailureCode.InvalidRequest));
        }

        var candidate = new byte[request.ValueByteLength];
        try
        {
            var bytesWritten = source(candidate);
            if (bytesWritten != request.ValueByteLength)
            {
                return ValueTask.FromResult(Failed(CredentialFailureCode.CallbackFailed));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(Failed(CredentialFailureCode.Unavailable));
            }

            if (_values.Remove(request.ReferenceId, out var previous))
            {
                CryptographicOperations.ZeroMemory(previous);
            }

            _values.Add(request.ReferenceId, candidate);
            candidate = [];
            return ValueTask.FromResult(CredentialProviderResult.Success());
        }
        catch
        {
            return ValueTask.FromResult(Failed(CredentialFailureCode.CallbackFailed));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    private static CredentialProviderResult Failed(CredentialFailureCode code) => CredentialProviderResult.Failed(CredentialFailure.FromCode(code));
}
