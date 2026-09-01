using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Captures one server-composed candidate and all exact values needed to bind a later commit.</summary>
/// <param name="WorkspaceId">The exact canonical workspace identity.</param>
/// <param name="Actor">The server-owned actor attribution.</param>
/// <param name="OperationId">The exact operation identity.</param>
/// <param name="RequestId">The exact superseded request identity.</param>
/// <param name="ExpectedLifecycleVersion">The exact optimistic lifecycle version.</param>
/// <param name="ExpectedRequest">The exact immutable superseded request reference.</param>
/// <param name="CandidateRequest">The complete server-composed successor request.</param>
/// <param name="GrantReference">The exact grant captured from canonical lifecycle evidence.</param>
/// <param name="ExpiresAtUtc">The trusted candidate expiry.</param>
public sealed record HumanInputSupersedeCandidateRegistration(
    string WorkspaceId,
    string Actor,
    string OperationId,
    string RequestId,
    long ExpectedLifecycleVersion,
    HumanInputRequestReference ExpectedRequest,
    HumanInputRequest CandidateRequest,
    AuthorityGrantReference GrantReference,
    DateTimeOffset ExpiresAtUtc);
