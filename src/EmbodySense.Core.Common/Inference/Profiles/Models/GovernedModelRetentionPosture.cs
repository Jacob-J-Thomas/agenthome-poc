namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Identifies the declared provider retention posture from strictest to broadest.</summary>
public enum GovernedModelRetentionPosture
{
    /// <summary>The posture is unknown and therefore ineligible.</summary>
    Unknown = 0,
    /// <summary>No provider retention.</summary>
    None = 1,
    /// <summary>Ephemeral processing retention only.</summary>
    Ephemeral = 2,
    /// <summary>Bounded provider retention.</summary>
    Limited = 3,
    /// <summary>Indefinite or otherwise unbounded retention.</summary>
    Indefinite = 4
}
