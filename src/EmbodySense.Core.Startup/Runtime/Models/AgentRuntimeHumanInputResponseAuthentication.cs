using EmbodySense.Core.Common.Authority;

namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Returns one server-owned Human Input response actor-authentication decision.</summary>
/// <param name="Status">The closed authentication disposition.</param>
/// <param name="ActorId">The authenticated actor when identity was established, including a denied decision.</param>
/// <param name="AuthenticationEvidenceHash">The server-owned evidence digest when identity was established.</param>
public sealed record AgentRuntimeHumanInputResponseAuthentication(
    AgentRuntimeHumanInputAuthorityStatus Status,
    AuthorityActorId? ActorId,
    string AuthenticationEvidenceHash);
