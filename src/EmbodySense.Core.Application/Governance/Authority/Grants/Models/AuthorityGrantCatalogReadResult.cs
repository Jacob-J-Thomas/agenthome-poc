using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Returns a bounded snapshot of current authority-grant revisions.</summary>
/// <param name="Status">The trusted catalog-read posture.</param>
/// <param name="StoreGeneration">The exact immutable ledger generation, or zero when unavailable.</param>
/// <param name="Grants">The current immutable grant revisions when available.</param>
public sealed record AuthorityGrantCatalogReadResult(
    AuthorityGrantCatalogReadStatus Status,
    long StoreGeneration,
    IReadOnlyList<AuthorityGrant> Grants)
{
    /// <summary>Gets a defensive immutable copy of the current grant revisions.</summary>
    public IReadOnlyList<AuthorityGrant> Grants { get; } = Grants is null ? null! : Array.AsReadOnly(Grants.ToArray());
}
