namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>Identifies whether the server currently recognizes a capability declaration.</summary>
public enum CapabilityDeclarationState
{
    /// <summary>The declaration state is unknown.</summary>
    Unknown = 0,

    /// <summary>The server recognizes the declaration.</summary>
    Declared = 1,

    /// <summary>The declaration has been withdrawn.</summary>
    Withdrawn = 2
}
