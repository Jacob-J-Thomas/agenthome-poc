namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Declares whether any argument token can enter an executable-owned secondary grammar.</summary>
public enum CommandActionSecondaryGrammarPolicy
{
    /// <summary>No posture was declared.</summary>
    Unknown = 0,
    /// <summary>The registered executable attests that every supplied argument remains one opaque argument token.</summary>
    None = 1,
}
