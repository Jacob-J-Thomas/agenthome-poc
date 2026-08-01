using System.Security.Cryptography;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Persistence.Credentials;

/// <summary>Stores bounded local credential values in Windows Credential Manager under the current-user security boundary.</summary>
/// <remarks>This provider supplies storage, not broker authorization. It exposes values only through the trusted callback contract and returns stable unavailable failures on unsupported platforms.</remarks>
public sealed class WindowsCredentialValueProvider : ICredentialValueProvider
{
    private const string ProviderIdentity = "org.embodysense.windows";
    private static readonly CredentialProviderId _providerId = ParseProviderId();
    private readonly IWindowsCredentialStore _store;

    /// <summary>Creates a provider backed by the current user's Windows Credential Manager.</summary>
    public WindowsCredentialValueProvider() : this(new WindowsCredentialStore())
    {
    }

    internal WindowsCredentialValueProvider(IWindowsCredentialStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc />
    public ValueTask<CredentialProviderResult> CreateAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(Write(request, source, replace: false, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<CredentialProviderResult> ReplaceAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(Write(request, source, replace: true, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<CredentialProviderResult> UseAsync(CredentialProviderUseRequest request, ICredentialTrustedUseConsumer trustedConsumer, CancellationToken cancellationToken)
    {
        if (!IsValid(request) || trustedConsumer is null)
        {
            return ValueTask.FromResult(Failed(CredentialFailureCode.InvalidRequest));
        }

        if (!_store.IsSupported || cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(Failed(CredentialFailureCode.Unavailable));
        }

        try
        {
            var target = CredentialProviderTarget.Derive(request.WorkspaceId, request.ReferenceId);
            if (!CredentialOperationMutex.TryAcquire(target, cancellationToken, out var operationLock))
            {
                return ValueTask.FromResult(Failed(CredentialFailureCode.Unavailable));
            }

            using (operationLock)
            using (var stored = _store.Read(target))
            {
                if (stored.Status == WindowsCredentialStoreStatus.Missing)
                {
                    return ValueTask.FromResult(Failed(CredentialFailureCode.NotFound));
                }

                if (stored.Status != WindowsCredentialStoreStatus.Success || cancellationToken.IsCancellationRequested)
                {
                    return ValueTask.FromResult(Failed(CredentialFailureCode.Unavailable));
                }

                try
                {
                    trustedConsumer.Use(stored.Value);
                    return ValueTask.FromResult(CredentialProviderResult.Success());
                }
                catch (Exception)
                {
                    return ValueTask.FromResult(Failed(CredentialFailureCode.CallbackFailed));
                }
            }
        }
        catch (Exception)
        {
            return ValueTask.FromResult(Failed(CredentialFailureCode.Unavailable));
        }
    }

    /// <inheritdoc />
    public ValueTask<CredentialProviderResult> DeleteAsync(CredentialProviderDeleteRequest request, CancellationToken cancellationToken)
    {
        if (!IsValid(request))
        {
            return ValueTask.FromResult(Failed(CredentialFailureCode.InvalidRequest));
        }

        if (!_store.IsSupported || cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(Failed(CredentialFailureCode.Unavailable));
        }

        try
        {
            var target = CredentialProviderTarget.Derive(request.WorkspaceId, request.ReferenceId);
            if (!CredentialOperationMutex.TryAcquire(target, cancellationToken, out var operationLock))
            {
                return ValueTask.FromResult(Failed(CredentialFailureCode.Unavailable));
            }

            using (operationLock)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return ValueTask.FromResult(Failed(CredentialFailureCode.Unavailable));
                }

                var deleteStatus = _store.Delete(target);
                using var observed = _store.Read(target);
                if (observed.Status == WindowsCredentialStoreStatus.Missing)
                {
                    return ValueTask.FromResult(CredentialProviderResult.Success());
                }

                var code = deleteStatus == WindowsCredentialStoreStatus.Unavailable && observed.Status == WindowsCredentialStoreStatus.Success ? CredentialFailureCode.Unavailable : CredentialFailureCode.OutcomeUncertain;
                return ValueTask.FromResult(Failed(code));
            }
        }
        catch (Exception)
        {
            return ValueTask.FromResult(Failed(CredentialFailureCode.OutcomeUncertain));
        }
    }

    /// <inheritdoc />
    public ValueTask<CredentialProviderHealthResult> GetHealthAsync(CredentialProviderUseRequest request, CancellationToken cancellationToken)
    {
        if (!IsValid(request) || !_store.IsSupported || cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(UnavailableHealth());
        }

        try
        {
            var target = CredentialProviderTarget.Derive(request.WorkspaceId, request.ReferenceId);
            if (!CredentialOperationMutex.TryAcquire(target, cancellationToken, out var operationLock))
            {
                return ValueTask.FromResult(UnavailableHealth());
            }

            using (operationLock)
            {
                return ValueTask.FromResult(_store.Probe(target) switch
                {
                    WindowsCredentialStoreStatus.Success => CredentialProviderHealthResult.Available(),
                    WindowsCredentialStoreStatus.Missing => CredentialProviderHealthResult.Missing(),
                    WindowsCredentialStoreStatus.Corrupt => CredentialProviderHealthResult.Failed(CredentialProviderHealthStatus.Corrupt, Failure(CredentialFailureCode.Unavailable)),
                    _ => UnavailableHealth()
                });
            }
        }
        catch (Exception)
        {
            return ValueTask.FromResult(UnavailableHealth());
        }
    }

    private CredentialProviderResult Write(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, bool replace, CancellationToken cancellationToken)
    {
        if (!IsValid(request) || source is null)
        {
            return Failed(CredentialFailureCode.InvalidRequest);
        }

        if (!_store.IsSupported || cancellationToken.IsCancellationRequested)
        {
            return Failed(CredentialFailureCode.Unavailable);
        }

        if (request.ValueByteLength > _store.MaxValueByteLength)
        {
            return Failed(CredentialFailureCode.LimitExceeded);
        }

        try
        {
            var target = CredentialProviderTarget.Derive(request.WorkspaceId, request.ReferenceId);
            if (!CredentialOperationMutex.TryAcquire(target, cancellationToken, out var operationLock))
            {
                return Failed(CredentialFailureCode.Unavailable);
            }

            using (operationLock)
            using (var prior = _store.Read(target))
            {
                if (replace && prior.Status == WindowsCredentialStoreStatus.Missing)
                {
                    return Failed(CredentialFailureCode.NotFound);
                }

                if (!replace && prior.Status == WindowsCredentialStoreStatus.Success)
                {
                    return Failed(CredentialFailureCode.Conflict);
                }

                if (prior.Status is WindowsCredentialStoreStatus.Unavailable or WindowsCredentialStoreStatus.Corrupt)
                {
                    return Failed(CredentialFailureCode.Unavailable);
                }

                return WriteCandidate(target, request.ValueByteLength, source, prior, replace, cancellationToken);
            }
        }
        catch (Exception)
        {
            return Failed(CredentialFailureCode.Unavailable);
        }
    }

    private CredentialProviderResult WriteCandidate(string target, int valueByteLength, CredentialSecretWriteCallback source, WindowsCredentialReadResult prior, bool replace, CancellationToken cancellationToken)
    {
        var candidate = new byte[valueByteLength];
        try
        {
            int bytesWritten;
            try
            {
                bytesWritten = source(candidate);
            }
            catch (Exception)
            {
                return Failed(CredentialFailureCode.CallbackFailed);
            }

            if (bytesWritten != candidate.Length)
            {
                return Failed(CredentialFailureCode.CallbackFailed);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Failed(CredentialFailureCode.Unavailable);
            }

            var writeStatus = SafeWrite(target, candidate);
            if (writeStatus == WindowsCredentialStoreStatus.Success && StateMatches(target, candidate))
            {
                return CredentialProviderResult.Success();
            }

            var failureCode = writeStatus == WindowsCredentialStoreStatus.LimitExceeded ? CredentialFailureCode.LimitExceeded : CredentialFailureCode.Unavailable;
            if (replace && StateMatches(target, prior.Value) || !replace && StateIsMissing(target))
            {
                return Failed(failureCode);
            }

            var rollbackProved = replace ? RestoreAndProve(target, prior.Value) : DeleteAndProveMissing(target);
            return rollbackProved ? Failed(failureCode) : Failed(CredentialFailureCode.OutcomeUncertain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    private bool RestoreAndProve(string target, byte[] prior)
    {
        return SafeWrite(target, prior) == WindowsCredentialStoreStatus.Success && StateMatches(target, prior);
    }

    private bool DeleteAndProveMissing(string target)
    {
        WindowsCredentialStoreStatus deleteStatus;
        try
        {
            deleteStatus = _store.Delete(target);
        }
        catch (Exception)
        {
            deleteStatus = WindowsCredentialStoreStatus.Unavailable;
        }

        return deleteStatus is WindowsCredentialStoreStatus.Success or WindowsCredentialStoreStatus.Missing && StateIsMissing(target);
    }

    private bool StateMatches(string target, byte[] expected)
    {
        try
        {
            using var observed = _store.Read(target);
            return observed.Status == WindowsCredentialStoreStatus.Success && CryptographicOperations.FixedTimeEquals(observed.Value, expected);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool StateIsMissing(string target)
    {
        try
        {
            using var observed = _store.Read(target);
            return observed.Status == WindowsCredentialStoreStatus.Missing;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private WindowsCredentialStoreStatus SafeWrite(string target, byte[] value)
    {
        try
        {
            return _store.Write(target, value);
        }
        catch (Exception)
        {
            return WindowsCredentialStoreStatus.Unavailable;
        }
    }

    private static bool IsValid(CredentialProviderMutationRequest? request) => request is not null && CredentialPortContractValidator.Validate(request).IsValid && request.ProviderId.Equals(_providerId);
    private static bool IsValid(CredentialProviderUseRequest? request) => request is not null && CredentialPortContractValidator.Validate(request).IsValid && request.ProviderId.Equals(_providerId);
    private static bool IsValid(CredentialProviderDeleteRequest? request) => request is not null && CredentialPortContractValidator.Validate(request).IsValid && request.ProviderId.Equals(_providerId);
    private static CredentialProviderResult Failed(CredentialFailureCode code) => CredentialProviderResult.Failed(Failure(code));
    private static CredentialFailure Failure(CredentialFailureCode code) => CredentialFailure.FromCode(code);
    private static CredentialProviderHealthResult UnavailableHealth() => CredentialProviderHealthResult.Failed(CredentialProviderHealthStatus.Unavailable, Failure(CredentialFailureCode.Unavailable));

    private static CredentialProviderId ParseProviderId()
    {
        return CredentialProviderId.TryParse(ProviderIdentity, out var providerId, out _) ? providerId! : throw new InvalidOperationException("The Windows credential provider identity is invalid.");
    }
}
