namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Identifies the trust posture of a bounded current authority-grant catalog read.</summary>
public enum AuthorityGrantCatalogReadStatus
{
    /// <summary>The status is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>The current catalog is complete and trustworthy.</summary>
    Available = 1,
    /// <summary>The ledger cannot currently be read safely.</summary>
    Unavailable = 2,
    /// <summary>The recovered or observed ledger cannot safely establish one current catalog.</summary>
    Ambiguous = 3,
}
