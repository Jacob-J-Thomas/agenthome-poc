using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Reports safe provider health without exposing a locator, handle, envelope, or value.</summary>
public sealed class CredentialProviderHealthResult
{
    private CredentialProviderHealthResult(CredentialProviderHealthStatus status, CredentialFailure? failure)
    {
        Status = status;
        Failure = failure;
    }

    /// <summary>Gets safe provider posture.</summary>
    public CredentialProviderHealthStatus Status { get; }

    /// <summary>Gets the closed value-free failure for unavailable or corrupt posture.</summary>
    public CredentialFailure? Failure { get; }

    /// <summary>Creates an available posture.</summary>
    public static CredentialProviderHealthResult Available() => new(CredentialProviderHealthStatus.Available, null);

    /// <summary>Creates a missing posture.</summary>
    public static CredentialProviderHealthResult Missing() => new(CredentialProviderHealthStatus.Missing, null);

    /// <summary>Creates an unavailable or corrupt posture.</summary>
    public static CredentialProviderHealthResult Failed(CredentialProviderHealthStatus status, CredentialFailure failure)
    {
        return status is CredentialProviderHealthStatus.Unavailable or CredentialProviderHealthStatus.Corrupt ? new CredentialProviderHealthResult(status, failure ?? throw new ArgumentNullException(nameof(failure))) : throw new ArgumentOutOfRangeException(nameof(status));
    }
}
