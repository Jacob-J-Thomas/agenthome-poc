using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Requests one atomic append-only grant revision and operation commit.</summary>
/// <param name="ExpectedStoreGeneration">The exact workspace-global generation observed before intent.</param>
/// <param name="GrantToAppend">The immutable successor revision, or null for an authorized deterministic receipt-only disposition.</param>
/// <param name="Operation">The exact committed operation evidence.</param>
public sealed record AuthorityGrantStoreMutation(
    long ExpectedStoreGeneration,
    AuthorityGrant? GrantToAppend,
    AuthorityGrantOperationEvidence Operation);
