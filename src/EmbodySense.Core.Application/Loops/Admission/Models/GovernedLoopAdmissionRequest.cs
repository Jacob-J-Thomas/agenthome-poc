using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Admission.Models;

/// <summary>Supplies the caller-stable portion of one server-prepared governed-loop admission request.</summary>
/// <param name="SchemaVersion">The request schema version, which must be 1.</param>
/// <param name="OperationId">The workspace-global idempotency identity.</param>
/// <param name="InvocationPayloadHash">The trusted server-prepared canonical invocation-payload identity, never a browser or client assertion.</param>
/// <param name="RequestHash">The canonical hash binding the payload identity to every caller-stable admission coordinate.</param>
/// <param name="Publication">The exact published loop revision requested for admission.</param>
/// <param name="AuthorityGrant">The exact immutable authority-grant revision requested for admission.</param>
/// <param name="ActorId">The authenticated actor retained as attribution, not authority.</param>
/// <param name="Surface">The server-owned canonical invocation-surface token.</param>
/// <remarks>Workspace, contextual role, graph hashes, run identity, and execution generation are derived by the server and cannot be supplied through this contract.</remarks>
public sealed record GovernedLoopAdmissionRequest(
    int SchemaVersion,
    string OperationId,
    string InvocationPayloadHash,
    string RequestHash,
    GovernedLoopRevisionPublicationPin Publication,
    AuthorityGrantReference AuthorityGrant,
    AuthorityActorId ActorId,
    string Surface)
{
    /// <summary>Gets the only supported experimental request schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
