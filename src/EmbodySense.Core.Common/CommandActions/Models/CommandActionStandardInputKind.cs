namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Identifies the non-interactive standard-input contract.</summary>
public enum CommandActionStandardInputKind
{
    /// <summary>No input posture was supplied.</summary>
    Unknown = 0,
    /// <summary>Standard input is closed without writing data.</summary>
    Closed = 1,
    /// <summary>One bounded text slot is written as exact UTF-8.</summary>
    SlotUtf8 = 2,
    /// <summary>One bounded JSON slot is written as canonical UTF-8 JSON.</summary>
    SlotJson = 3,
}
