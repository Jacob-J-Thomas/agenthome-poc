using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Captures one server-composed candidate and all exact values needed to bind a later commit.</summary>
/// <param name="WorkspaceId">The exact canonical workspace identity.</param>
/// <param name="Actor">The server-owned actor attribution.</param>
/// <param name="OperationId">The exact operation identity.</param>
/// <param name="RequestId">The exact target request identity.</param>
/// <param name="ExpectedLifecycleVersion">The exact optimistic lifecycle version.</param>
/// <param name="ExpectedRequest">The exact immutable target request reference.</param>
/// <param name="CandidateRequest">The complete server-composed successor request.</param>
/// <param name="GrantReference">The exact grant captured from canonical lifecycle evidence.</param>
/// <param name="ExpiresAtUtc">The trusted candidate expiry.</param>
/// <param name="Kind">The lifecycle operation kind bound to this candidate. The default preserves Supersede callers.</param>
/// <param name="PreparationHash">The exact server-owned preparation intent hash shared by one candidate group.</param>
public sealed record HumanInputSupersedeCandidateRegistration(
    string WorkspaceId,
    string Actor,
    string OperationId,
    string RequestId,
    long ExpectedLifecycleVersion,
    HumanInputRequestReference ExpectedRequest,
    HumanInputRequest CandidateRequest,
    AuthorityGrantReference GrantReference,
    DateTimeOffset ExpiresAtUtc,
    HumanInputRequestLifecycleOperationKind Kind = HumanInputRequestLifecycleOperationKind.Supersede,
    string? PreparationHash = null);
