namespace EmbodySense.Core.Common.Credentials.Leases.Models;

/// <summary>Describes whether one credential redemption is bound to an admitted model profile.</summary>
public enum CredentialLeaseProfileApplicability
{
    /// <summary>The consuming actuator is not a model-provider operation.</summary>
    NotApplicable = 1,

    /// <summary>The consuming operation is bound to one exact admitted model profile.</summary>
    Applicable = 2,
}
