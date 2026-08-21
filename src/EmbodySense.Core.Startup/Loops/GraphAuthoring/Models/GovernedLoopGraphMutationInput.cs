using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;

/// <summary>Requests one authenticated optimistic mutation without accepting caller-supplied actor or authority evidence.</summary>
/// <param name="OperationId">The caller-owned workspace-global idempotency identity.</param>
/// <param name="Kind">The exact lifecycle operation.</param>
/// <param name="GraphId">The canonical graph identity.</param>
/// <param name="ExpectedLifecycleStatus">The exact observed lifecycle posture, or unknown for initial creation.</param>
/// <param name="ExpectedLifecycleVersion">The exact observed optimistic version, or zero for initial creation.</param>
/// <param name="ExpectedDraftRevision">The exact observed draft head.</param>
/// <param name="ExpectedPublishedRevision">The exact observed publication pin.</param>
/// <param name="GraphCandidate">The complete candidate required only for create and replace operations.</param>
public sealed record GovernedLoopGraphMutationInput(
    string OperationId,
    GovernedLoopGraphMutationKind Kind,
    string GraphId,
    GovernedLoopRevisionLifecycleStatus ExpectedLifecycleStatus,
    long ExpectedLifecycleVersion,
    GovernedLoopRevisionReference? ExpectedDraftRevision,
    GovernedLoopRevisionPublicationPin? ExpectedPublishedRevision,
    GovernedLoopGraphCandidate? GraphCandidate);
