using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Reports one safe public-reference lookup.</summary>
public sealed class CredentialReferenceLookupResult
{
    private CredentialReferenceLookupResult(CredentialReference? reference, CredentialFailure? failure)
    {
        Reference = reference;
        Failure = failure;
    }

    /// <summary>Gets safe public reference metadata when found.</summary>
    public CredentialReference? Reference { get; }

    /// <summary>Gets the closed value-free failure.</summary>
    public CredentialFailure? Failure { get; }

    /// <summary>Gets whether exactly one reference and no failure were returned.</summary>
    public bool Succeeded => Reference is not null && Failure is null;

    /// <summary>Creates a successful safe-reference result.</summary>
    public static CredentialReferenceLookupResult Found(CredentialReference reference) => new(reference ?? throw new ArgumentNullException(nameof(reference)), null);

    /// <summary>Creates a failed safe-reference result.</summary>
    public static CredentialReferenceLookupResult Failed(CredentialFailure failure) => new(null, failure ?? throw new ArgumentNullException(nameof(failure)));
}
