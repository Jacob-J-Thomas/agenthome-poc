using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Common.Credentials;

/// <summary>Describes a closed, value-free failure suitable for public results and evidence.</summary>
public sealed class CredentialFailure
{
    private CredentialFailure(CredentialFailureCode code) => Code = code;

    /// <summary>Gets the stable failure category.</summary>
    public CredentialFailureCode Code { get; }

    /// <summary>Creates a failure from a supported closed category.</summary>
    public static CredentialFailure FromCode(CredentialFailureCode code)
    {
        return Enum.IsDefined(code) ? new CredentialFailure(code) : throw new ArgumentOutOfRangeException(nameof(code));
    }
}
