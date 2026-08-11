namespace EmbodySense.Core.Common.Loops.Execution.Authority.Models;

/// <summary>Identifies the closed outcome of an immediate effect-authority evaluation.</summary>
public enum GovernedLoopEffectAuthorityDisposition
{
    /// <summary>The exact effect may cross the named boundary immediately.</summary>
    Direct = 1,

    /// <summary>The effect must pause without crossing the boundary because authority is temporarily indeterminate.</summary>
    Pause = 2,

    /// <summary>The effect is definitively denied at the named boundary.</summary>
    Deny = 3
}
