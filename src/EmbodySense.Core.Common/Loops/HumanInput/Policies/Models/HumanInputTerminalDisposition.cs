namespace EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;

/// <summary>Identifies the closed non-authorizing terminal result of a Human Input response window.</summary>
public enum HumanInputTerminalDisposition
{
    /// <summary>No supported terminal disposition was supplied.</summary>
    Unknown = 0,

    /// <summary>The request becomes expired without selecting or authorizing any response.</summary>
    Expired = 1,
}
