using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Reports whether a trusted authority verifier accepted the exact current request.</summary>
public sealed class CredentialAuthorityVerificationResult
{
    private CredentialAuthorityVerificationResult(bool accepted, CredentialFailure? failure)
    {
        Accepted = accepted;
        Failure = failure;
    }

    /// <summary>Gets whether the exact proof was accepted under current authority.</summary>
    public bool Accepted { get; }

    /// <summary>Gets the closed value-free rejection.</summary>
    public CredentialFailure? Failure { get; }

    /// <summary>Creates an accepted result.</summary>
    public static CredentialAuthorityVerificationResult Accept() => new(true, null);

    /// <summary>Creates a rejected result.</summary>
    public static CredentialAuthorityVerificationResult Reject(CredentialFailure failure) => new(false, failure ?? throw new ArgumentNullException(nameof(failure)));
}
