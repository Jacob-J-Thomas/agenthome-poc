namespace EmbodySense.Core.Common.Loops.Failures.Models;

/// <summary>Describes only what retained evidence proves about external effect dispatch.</summary>
public enum GovernedLoopFailureEffectCertainty
{
    /// <summary>No trustworthy effect certainty is available.</summary>
    Unknown = 0,
    /// <summary>The failure does not arise from an external effect boundary.</summary>
    NotApplicable,
    /// <summary>Dispatch is proved not to have started.</summary>
    DispatchProvedNotStarted,
    /// <summary>Dispatch may have started but an external effect is proved absent.</summary>
    EffectProvedAbsent,
    /// <summary>An external effect is proved committed.</summary>
    EffectProvedCommitted,
    /// <summary>An external effect may exist and cannot be classified safely.</summary>
    Ambiguous,
}
