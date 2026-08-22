namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Identifies deterministic model-routing admission outcomes.</summary>
public enum GovernedModelRoutingAdmissionStatus
{
    /// <summary>A new exact snapshot was durably admitted.</summary>
    Admitted = 1,
    /// <summary>The request shape is invalid.</summary>
    Invalid = 2,
    /// <summary>At least one authored candidate is currently ineligible.</summary>
    Ineligible = 3,
    /// <summary>Complete trusted current evidence is unavailable.</summary>
    Unavailable = 4
}
