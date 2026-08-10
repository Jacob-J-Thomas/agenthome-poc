using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Requests one authenticated, globally idempotent governed-loop revision lifecycle operation.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="OperationId">The workspace-global idempotency identifier.</param>
/// <param name="Kind">The exact lifecycle operation.</param>
/// <param name="GraphId">The exact graph identifier.</param>
/// <param name="ActorId">The authenticated actor identity; this identity does not itself grant authority.</param>
/// <param name="ExpectedLifecycleStatus">The exact expected lifecycle posture, or unknown only for initial creation.</param>
/// <param name="ExpectedLifecycleVersion">The exact optimistic version, or zero only for initial creation.</param>
/// <param name="ExpectedDraftRevision">The exact expected draft head, when one exists.</param>
/// <param name="ExpectedPublishedRevision">The exact expected publication pin, when one exists.</param>
/// <param name="CandidateRevision">The new immutable artifact identity for draft creation, replacement, or rollback.</param>
/// <param name="TargetRevision">The exact existing revision acted upon by replacement, publication, disable, archive, or rollback.</param>
/// <param name="RollbackSourcePublication">The exact historical publication copied by rollback.</param>
public sealed record GovernedLoopRevisionLifecycleRequest(
    int SchemaVersion,
    string OperationId,
    GovernedLoopRevisionOperationKind Kind,
    string GraphId,
    AuthorityActorId ActorId,
    GovernedLoopRevisionLifecycleStatus ExpectedLifecycleStatus,
    long ExpectedLifecycleVersion,
    GovernedLoopRevisionReference? ExpectedDraftRevision,
    GovernedLoopRevisionPublicationPin? ExpectedPublishedRevision,
    GovernedLoopRevisionReference? CandidateRevision,
    GovernedLoopRevisionReference? TargetRevision,
    GovernedLoopRevisionPublicationPin? RollbackSourcePublication);
