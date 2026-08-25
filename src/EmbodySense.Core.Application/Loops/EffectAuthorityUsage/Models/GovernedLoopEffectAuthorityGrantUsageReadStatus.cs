namespace EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;

/// <summary>Classifies whether authenticated first-bound-run completion evidence leaves an exact grant usable.</summary>
public enum GovernedLoopEffectAuthorityGrantUsageReadStatus
{
    /// <summary>No first-bound-run completion claim exists for the exact grant.</summary>
    Unconsumed = 1,

    /// <summary>A first-bound-run completion is durably complete, so the exact grant is ineffective.</summary>
    Consumed = 2,

    /// <summary>A first-bound-run completion is pending and cannot safely be treated as usable.</summary>
    Pending = 3,

    /// <summary>The canonical completion evidence could not be authenticated or read.</summary>
    Unavailable = 4,

    /// <summary>The canonical completion evidence admits more than one safe interpretation.</summary>
    Ambiguous = 5,
}
