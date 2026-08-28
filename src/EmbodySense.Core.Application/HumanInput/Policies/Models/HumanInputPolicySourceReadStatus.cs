namespace EmbodySense.Core.Application.HumanInput.Policies.Models;

/// <summary>Identifies the closed outcome of one exact Human Input policy source lookup.</summary>
public enum HumanInputPolicySourceReadStatus
{
    /// <summary>No supported source outcome was supplied.</summary>
    Unknown = 0,

    /// <summary>The exact immutable policy revision was read and detached.</summary>
    Ready = 1,

    /// <summary>No artifact matches the exact requested policy and revision identity.</summary>
    NotFound = 2,

    /// <summary>The source could not prove a safe exact lookup result.</summary>
    Unavailable = 3,
}
