namespace EmbodySense.Core.Application.HumanInput.Catalog.Models;

/// <summary>Identifies one bounded Human Input catalog-page read outcome.</summary>
public enum HumanInputRequestCatalogPageStatus
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,
    /// <summary>The requested page was read from one authenticated unchanged ledger generation.</summary>
    Ready = 1,
    /// <summary>The page shape or opaque cursor was malformed.</summary>
    Invalid = 2,
    /// <summary>The cursor belongs to an older or different authenticated ledger generation.</summary>
    Stale = 3,
    /// <summary>The catalog dependency was unavailable.</summary>
    Unavailable = 4,
    /// <summary>Available state could not establish a safe page.</summary>
    Ambiguous = 5,
}
