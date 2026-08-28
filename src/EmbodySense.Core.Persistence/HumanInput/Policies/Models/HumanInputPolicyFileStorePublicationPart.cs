namespace EmbodySense.Core.Persistence.HumanInput.Policies.Models;

/// <summary>Identifies the exact schema-1 policy-store file participating in an observable physical persistence boundary.</summary>
public enum HumanInputPolicyFileStorePublicationPart
{
    /// <summary>No policy-store publication part was selected.</summary>
    Unknown = 0,

    /// <summary>The canonical publication intent that binds one exact policy and expected generation.</summary>
    PublicationIntent = 1,

    /// <summary>The immutable canonical policy artifact.</summary>
    PolicyArtifact = 2,

    /// <summary>The mutable catalog generation.</summary>
    Generation = 3,

    /// <summary>One exact derived interrupted temporary artifact being retired.</summary>
    InterruptedTemporary = 4
}
