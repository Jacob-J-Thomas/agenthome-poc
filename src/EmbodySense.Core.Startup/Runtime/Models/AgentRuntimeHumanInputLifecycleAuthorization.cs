using EmbodySense.Core.Common.Authority;

namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Returns one server-owned Human Input lifecycle actor-authorization decision.</summary>
/// <param name="Status">The closed authorization disposition.</param>
/// <param name="ActorId">The authenticated actor when identity was established, including a denied decision.</param>
/// <param name="AuthorityEvidenceHash">The server-owned evidence digest when identity was established.</param>
public sealed record AgentRuntimeHumanInputLifecycleAuthorization(
    AgentRuntimeHumanInputAuthorityStatus Status,
    AuthorityActorId? ActorId,
    string AuthorityEvidenceHash);
