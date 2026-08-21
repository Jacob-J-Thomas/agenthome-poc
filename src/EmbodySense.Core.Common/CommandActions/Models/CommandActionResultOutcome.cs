namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Classifies the bounded conclusive outcome exposed by a graph command Action.</summary>
public enum CommandActionResultOutcome
{
    /// <summary>The outcome is unknown and invalid.</summary>
    Unknown = 0,

    /// <summary>The exact command completed successfully.</summary>
    Succeeded = 1,

    /// <summary>The exact command completed with a conclusive failure.</summary>
    Failed = 2,
}
