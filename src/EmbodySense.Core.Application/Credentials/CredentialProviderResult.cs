using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Reports a value-free provider mutation or callback result.</summary>
public sealed class CredentialProviderResult
{
    private CredentialProviderResult(bool succeeded, CredentialFailure? failure)
    {
        Succeeded = succeeded;
        Failure = failure;
    }

    /// <summary>Gets whether the provider operation succeeded.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the closed value-free failure.</summary>
    public CredentialFailure? Failure { get; }

    /// <summary>Creates a successful value-free result.</summary>
    public static CredentialProviderResult Success() => new(true, null);

    /// <summary>Creates a failed value-free result.</summary>
    public static CredentialProviderResult Failed(CredentialFailure failure) => new(false, failure ?? throw new ArgumentNullException(nameof(failure)));
}
