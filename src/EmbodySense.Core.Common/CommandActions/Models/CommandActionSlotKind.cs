namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Identifies one closed typed command-template slot.</summary>
public enum CommandActionSlotKind
{
    /// <summary>No slot kind was supplied.</summary>
    Unknown = 0,
    /// <summary>A bounded canonical identifier.</summary>
    Identifier = 1,
    /// <summary>A canonical base-10 signed integer.</summary>
    Integer = 2,
    /// <summary>One member of a server-declared closed enumeration.</summary>
    Enumeration = 3,
    /// <summary>Bounded safe NFC-normalized literal text.</summary>
    BoundedText = 4,
    /// <summary>An exact portable workspace-relative target.</summary>
    WorkspaceRelativeTarget = 5,
    /// <summary>Bounded canonical JSON.</summary>
    BoundedJson = 6,
}
