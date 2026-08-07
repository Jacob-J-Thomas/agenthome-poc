namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies one explicit catalog lifecycle transition.</summary>
public enum CapabilityCatalogMutationKind
{
    /// <summary>Registers a validated source declaration without trusting, installing, enabling, or assigning it.</summary>
    Declare = 1,
    /// <summary>Marks the declared implementation installed.</summary>
    Install = 2,
    /// <summary>Marks the capability enabled without assigning authority.</summary>
    Enable = 3,
    /// <summary>Marks the capability disabled.</summary>
    Disable = 4,
    /// <summary>Records a server-owned verified trust decision.</summary>
    Verify = 5,
    /// <summary>Records a server-owned rejected trust decision.</summary>
    RejectTrust = 6,
    /// <summary>Records a healthy observation.</summary>
    MarkHealthy = 7,
    /// <summary>Records a degraded observation.</summary>
    MarkDegraded = 8,
    /// <summary>Records an unavailable observation.</summary>
    MarkUnavailable = 9,
    /// <summary>Marks the capability deprecated while retaining it.</summary>
    Deprecate = 10,
    /// <summary>Creates a retained removal tombstone.</summary>
    Remove = 11
}
