namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Identifies the exact structured standard-output contract.</summary>
public enum CommandActionOutputKind
{
    /// <summary>No output contract was supplied.</summary>
    Unknown = 0,
    /// <summary>Successful standard output must contain one complete bounded JSON value.</summary>
    Json = 1,
}
