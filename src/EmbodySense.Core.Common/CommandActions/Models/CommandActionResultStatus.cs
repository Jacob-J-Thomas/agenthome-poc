namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Classifies whether one conclusive command outcome was first committed or replayed.</summary>
public enum CommandActionResultStatus
{
    /// <summary>The status is unknown and invalid.</summary>
    Unknown = 0,

    /// <summary>The conclusive outcome was committed by this execution.</summary>
    Committed = 1,

    /// <summary>The exact retained conclusive outcome was replayed without another launch.</summary>
    Replayed = 2,
}
