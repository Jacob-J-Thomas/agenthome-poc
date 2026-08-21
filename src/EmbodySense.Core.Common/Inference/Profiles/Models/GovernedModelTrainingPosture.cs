namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Identifies the declared provider training-use posture from strictest to broadest.</summary>
public enum GovernedModelTrainingPosture
{
    /// <summary>The posture is unknown and therefore ineligible.</summary>
    Unknown = 0,
    /// <summary>Provider training use is prohibited.</summary>
    Prohibited = 1,
    /// <summary>Training use occurs only after an explicit opt-in.</summary>
    OptInOnly = 2,
    /// <summary>Training use may occur.</summary>
    Allowed = 3
}
