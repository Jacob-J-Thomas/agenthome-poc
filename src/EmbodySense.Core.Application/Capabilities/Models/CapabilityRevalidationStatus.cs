namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies why immutable admitted capability pins did or did not remain effective.</summary>
public enum CapabilityRevalidationStatus
{
    /// <summary>No authoritative posture was supplied.</summary>
    Unknown = 0,
    /// <summary>Every admitted pin remains exact, active, and inside current authority.</summary>
    Active = 1,
    /// <summary>The immutable admission snapshot is malformed or cannot be proved.</summary>
    InvalidSnapshot = 2,
    /// <summary>The immutable admission snapshot belongs to another workspace.</summary>
    WorkspaceMismatch = 3,
    /// <summary>Current loop or role authority no longer contains every admitted root capability.</summary>
    AuthorityNarrowed = 4,
    /// <summary>The current capability catalog could not be read.</summary>
    CatalogUnavailable = 5,
    /// <summary>The current capability catalog did not yield one coherent bounded snapshot.</summary>
    CatalogAmbiguous = 6,
    /// <summary>An admitted capability no longer exists in the current catalog.</summary>
    PinMissing = 7,
    /// <summary>An admitted descriptor, implementation, provenance, or safe description changed.</summary>
    PinDrifted = 8,
    /// <summary>An exact admitted pin is not currently enabled, healthy, trusted, installed, or compatible.</summary>
    PinInactive = 9,
}
