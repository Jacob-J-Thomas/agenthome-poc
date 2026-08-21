using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Common.Authority.Delegation.Models;

/// <summary>Links delegation to the exact parent grant, admission, and completion scope without following successors.</summary>
/// <param name="ParentGrant">The exact immutable parent grant revision.</param>
/// <param name="ParentAdmissionReceiptHash">The exact parent admission receipt hash.</param>
/// <param name="WorkspaceId">The exact parent workspace.</param>
/// <param name="ParentRunId">The exact parent run identity.</param>
/// <param name="ParentExecutionGeneration">The exact parent execution generation.</param>
/// <param name="LinkageHash">The canonical hash over the complete link except this field.</param>
public sealed record AuthorityDelegationRevocationLink(
    AuthorityGrantReference ParentGrant,
    string ParentAdmissionReceiptHash,
    string WorkspaceId,
    string ParentRunId,
    long ParentExecutionGeneration,
    string LinkageHash);
