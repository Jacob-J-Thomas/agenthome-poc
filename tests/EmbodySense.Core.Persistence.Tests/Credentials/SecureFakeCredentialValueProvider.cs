using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Persistence.Tests.Credentials;

internal sealed class SecureFakeCredentialValueProvider : ICredentialValueProvider, IDisposable
{
    private const string ProviderIdentity = "org.embodysense.windows";
    private const string TargetPrefix = "EmbodySense:v1:";
    private static readonly CredentialProviderId _providerId = ParseProviderId();
    private readonly object _operationGate = new();

    internal SecureFakeCredentialValueProvider(bool isSupported = true, int maxValueByteLength = 2_560)
    {
        Store = new ScriptedWindowsCredentialStore(isSupported, maxValueByteLength);
    }

    internal ScriptedWindowsCredentialStore Store { get; }

    public ValueTask<CredentialProviderResult> CreateAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(Write(request, source, replace: false, cancellationToken));
    }

    public ValueTask<CredentialProviderResult> ReplaceAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(Write(request, source, replace: true, cancellationToken));
    }

    public ValueTask<CredentialProviderResult> UseAsync(CredentialProviderUseRequest request, ICredentialTrustedUseConsumer trustedConsumer, CancellationToken cancellationToken)
    {
        if (!IsValid(request) || trustedConsumer is null)
        {
            return ValueTask.FromResult(Failed(CredentialFailureCode.InvalidRequest));
        }

        if (!Store.IsSupported || cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(Failed(CredentialFailureCode.Unavailable));
        }

        lock (_operationGate)
        {
            using var stored = Store.Read(DeriveTarget(request.WorkspaceId, request.ReferenceId));
            if (stored.Status == ScriptedCredentialStoreStatus.Missing)
            {
                return ValueTask.FromResult(Failed(CredentialFailureCode.NotFound));
            }

            if (stored.Status != ScriptedCredentialStoreStatus.Success || cancellationToken.IsCancellationRequested)
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

    public ValueTask<CredentialProviderResult> DeleteAsync(CredentialProviderDeleteRequest request, CancellationToken cancellationToken)
    {
        if (!IsValid(request))
        {
            return ValueTask.FromResult(Failed(CredentialFailureCode.InvalidRequest));
        }

        if (!Store.IsSupported || cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(Failed(CredentialFailureCode.Unavailable));
        }

        lock (_operationGate)
        {
            var target = DeriveTarget(request.WorkspaceId, request.ReferenceId);
            var deleteStatus = Store.Delete(target);
            using var observed = Store.Read(target);
            if (observed.Status == ScriptedCredentialStoreStatus.Missing)
            {
                return ValueTask.FromResult(CredentialProviderResult.Success());
            }

            var code = deleteStatus == ScriptedCredentialStoreStatus.Unavailable && observed.Status == ScriptedCredentialStoreStatus.Success ? CredentialFailureCode.Unavailable : CredentialFailureCode.OutcomeUncertain;
            return ValueTask.FromResult(Failed(code));
        }
    }

    public ValueTask<CredentialProviderHealthResult> GetHealthAsync(CredentialProviderUseRequest request, CancellationToken cancellationToken)
    {
        if (!IsValid(request) || !Store.IsSupported || cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(UnavailableHealth());
        }

        lock (_operationGate)
        {
            return ValueTask.FromResult(Store.Probe(DeriveTarget(request.WorkspaceId, request.ReferenceId)) switch
            {
                ScriptedCredentialStoreStatus.Success => CredentialProviderHealthResult.Available(),
                ScriptedCredentialStoreStatus.Missing => CredentialProviderHealthResult.Missing(),
                ScriptedCredentialStoreStatus.Corrupt => CredentialProviderHealthResult.Failed(CredentialProviderHealthStatus.Corrupt, Failure(CredentialFailureCode.Unavailable)),
                _ => UnavailableHealth()
            });
        }
    }

    internal static string DeriveTarget(string workspaceId, CredentialReferenceId referenceId)
    {
        var workspaceBytes = Encoding.UTF8.GetBytes(workspaceId);
        var referenceBytes = Encoding.UTF8.GetBytes(referenceId.Value);
        var input = new byte[sizeof(int) + workspaceBytes.Length + referenceBytes.Length];
        BitConverter.GetBytes(workspaceBytes.Length).CopyTo(input, 0);
        workspaceBytes.CopyTo(input, sizeof(int));
        referenceBytes.CopyTo(input, sizeof(int) + workspaceBytes.Length);
        var digest = SHA256.HashData(input);

        CryptographicOperations.ZeroMemory(workspaceBytes);
        CryptographicOperations.ZeroMemory(referenceBytes);
        CryptographicOperations.ZeroMemory(input);
        var target = TargetPrefix + Convert.ToHexString(digest);
        CryptographicOperations.ZeroMemory(digest);
        return target;
    }

    public void Dispose() => Store.Dispose();

    private CredentialProviderResult Write(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, bool replace, CancellationToken cancellationToken)
    {
        if (!IsValid(request) || source is null)
        {
            return Failed(CredentialFailureCode.InvalidRequest);
        }

        if (!Store.IsSupported || cancellationToken.IsCancellationRequested)
        {
            return Failed(CredentialFailureCode.Unavailable);
        }

        if (request.ValueByteLength > Store.MaxValueByteLength)
        {
            return Failed(CredentialFailureCode.LimitExceeded);
        }

        lock (_operationGate)
        {
            var target = DeriveTarget(request.WorkspaceId, request.ReferenceId);
            using var prior = Store.Read(target);
            if (replace && prior.Status == ScriptedCredentialStoreStatus.Missing)
            {
                return Failed(CredentialFailureCode.NotFound);
            }

            if (!replace && prior.Status == ScriptedCredentialStoreStatus.Success)
            {
                return Failed(CredentialFailureCode.Conflict);
            }

            if (prior.Status is ScriptedCredentialStoreStatus.Unavailable or ScriptedCredentialStoreStatus.Corrupt)
            {
                return Failed(CredentialFailureCode.Unavailable);
            }

            return WriteCandidate(target, request.ValueByteLength, source, prior, replace, cancellationToken);
        }
    }

    private CredentialProviderResult WriteCandidate(string target, int valueByteLength, CredentialSecretWriteCallback source, ScriptedCredentialReadResult prior, bool replace, CancellationToken cancellationToken)
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

            var writeStatus = Store.Write(target, candidate);
            if (writeStatus == ScriptedCredentialStoreStatus.Success && StateMatches(target, candidate))
            {
                return CredentialProviderResult.Success();
            }

            var failureCode = writeStatus == ScriptedCredentialStoreStatus.LimitExceeded ? CredentialFailureCode.LimitExceeded : CredentialFailureCode.Unavailable;
            if ((replace && StateMatches(target, prior.Value)) || (!replace && StateIsMissing(target)))
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
        return Store.Write(target, prior) == ScriptedCredentialStoreStatus.Success && StateMatches(target, prior);
    }

    private bool DeleteAndProveMissing(string target)
    {
        var deleteStatus = Store.Delete(target);
        return deleteStatus is ScriptedCredentialStoreStatus.Success or ScriptedCredentialStoreStatus.Missing && StateIsMissing(target);
    }

    private bool StateMatches(string target, byte[] expected)
    {
        using var observed = Store.Read(target);
        return observed.Status == ScriptedCredentialStoreStatus.Success && CryptographicOperations.FixedTimeEquals(observed.Value, expected);
    }

    private bool StateIsMissing(string target)
    {
        using var observed = Store.Read(target);
        return observed.Status == ScriptedCredentialStoreStatus.Missing;
    }

    private static bool IsValid(CredentialProviderMutationRequest? request) => request is not null && CredentialPortContractValidator.Validate(request).IsValid && request.ProviderId.Equals(_providerId);
    private static bool IsValid(CredentialProviderUseRequest? request) => request is not null && CredentialPortContractValidator.Validate(request).IsValid && request.ProviderId.Equals(_providerId);
    private static bool IsValid(CredentialProviderDeleteRequest? request) => request is not null && CredentialPortContractValidator.Validate(request).IsValid && request.ProviderId.Equals(_providerId);
    private static CredentialProviderResult Failed(CredentialFailureCode code) => CredentialProviderResult.Failed(Failure(code));
    private static CredentialFailure Failure(CredentialFailureCode code) => CredentialFailure.FromCode(code);
    private static CredentialProviderHealthResult UnavailableHealth() => CredentialProviderHealthResult.Failed(CredentialProviderHealthStatus.Unavailable, Failure(CredentialFailureCode.Unavailable));

    private static CredentialProviderId ParseProviderId()
    {
        return CredentialProviderId.TryParse(ProviderIdentity, out var providerId, out _) ? providerId! : throw new InvalidOperationException("The secure fake credential provider identity is invalid.");
    }
}
