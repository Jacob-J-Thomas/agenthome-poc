namespace EmbodySense.Core.Application.Loops.Compatibility.Models;

/// <summary>Identifies the legacy execution evidence source inspected by a read-only compatibility projection.</summary>
public enum GovernedLoopCompatibilitySource
{
    /// <summary>No supported source was selected.</summary>
    Unknown = 0,
    /// <summary>The transitional default-conversation turn protocol.</summary>
    DefaultConversation = 1,
    /// <summary>The first-wave ordered custom-loop run protocol.</summary>
    CustomLoop = 2
}
