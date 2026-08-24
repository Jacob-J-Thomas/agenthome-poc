namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Identifies how one exact argument token is supplied.</summary>
public enum CommandActionArgumentPartKind
{
    /// <summary>No argument-part kind was supplied.</summary>
    Unknown = 0,
    /// <summary>The complete token is fixed by server registration.</summary>
    Fixed = 1,
    /// <summary>The complete token comes from one validated named slot.</summary>
    Slot = 2,
}
