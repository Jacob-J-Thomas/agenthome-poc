using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Associates workspace-global operation evidence with its exact grant identity.</summary>
/// <param name="GrantId">The exact affected grant.</param>
/// <param name="Evidence">The immutable operation evidence.</param>
public sealed record AuthorityGrantStoredOperation(AuthorityGrantId GrantId, AuthorityGrantOperationEvidence Evidence);
