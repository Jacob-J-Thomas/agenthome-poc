namespace EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

/// <summary>Identifies one closed content-segment source without containing secret values.</summary>
public enum WorkspaceActionContentSegmentKind
{
    /// <summary>No supported segment kind was selected.</summary>
    Unknown = 0,

    /// <summary>The segment contains exact strict-UTF-8 literal text.</summary>
    LiteralUtf8 = 1,

    /// <summary>The segment contains only a value-free credential reference reserved for the shared trusted host.</summary>
    CredentialReference = 2,
}
