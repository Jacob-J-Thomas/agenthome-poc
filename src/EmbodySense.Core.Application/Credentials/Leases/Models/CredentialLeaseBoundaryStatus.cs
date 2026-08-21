namespace EmbodySense.Core.Application.Credentials.Leases.Models;

/// <summary>Defines the closed result of the revocation-ordered credential redemption gate.</summary>
public enum CredentialLeaseBoundaryStatus
{
    /// <summary>The durable single-use boundary was committed while the exact registry state remained active.</summary>
    Entered = 1,
    /// <summary>Current registry state denied use before the boundary.</summary>
    NotRedeemed = 2,
    /// <summary>The expected attempt head, identity, or registry snapshot conflicted.</summary>
    Conflict = 3,
    /// <summary>Retained evidence was corrupt.</summary>
    Corrupt = 4,
    /// <summary>The gate, registry, or durable store was unavailable.</summary>
    Unavailable = 5,
}
