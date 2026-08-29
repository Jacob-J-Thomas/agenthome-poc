using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Returns server-owned candidate and exact active grant terms for one lifecycle operation.</summary>
/// <param name="Status">The closed server-owned terms disposition.</param>
/// <param name="CandidateRequest">The complete server-owned immutable candidate when the operation requires one.</param>
/// <param name="GrantReference">The complete server-owned exact grant when the operation requires delivery authority.</param>
public sealed record AgentRuntimeHumanInputLifecycleTerms(
    AgentRuntimeHumanInputAuthorityStatus Status,
    HumanInputRequest? CandidateRequest,
    AuthorityGrantReference? GrantReference);
