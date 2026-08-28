namespace EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;

/// <summary>Identifies the closed responsibility of one immutable Human Input policy revision.</summary>
public enum HumanInputPolicyKind
{
    /// <summary>No supported policy kind was supplied.</summary>
    Unknown = 0,

    /// <summary>Defines the finite response-window duration measured from trusted resolution time.</summary>
    ResponseWindow = 1,

    /// <summary>Defines the one terminal disposition reached when the finite response window elapses.</summary>
    DeadlineDisposition = 2,
}
