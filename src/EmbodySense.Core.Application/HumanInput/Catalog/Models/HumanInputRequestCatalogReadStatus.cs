namespace EmbodySense.Core.Application.HumanInput.Catalog.Models;

/// <summary>Identifies one exact Human Input catalog-read outcome.</summary>
public enum HumanInputRequestCatalogReadStatus
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact request aggregate was read.</summary>
    Ready = 1,
    /// <summary>The supplied exact request identifier was malformed.</summary>
    Invalid = 2,
    /// <summary>The exact request does not exist.</summary>
    NotFound = 3,
    /// <summary>The catalog dependency was unavailable.</summary>
    Unavailable = 4,
    /// <summary>Available state could not establish a safe result.</summary>
    Ambiguous = 5,
}
