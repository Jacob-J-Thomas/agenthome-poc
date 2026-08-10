using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.GraphAuthoring.Models;

/// <summary>Requests one immutable graph-payload and generic lifecycle operation.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="LifecycleRequest">The exact generic lifecycle intent.</param>
/// <param name="GraphCandidate">The candidate required by create and replace; other operations must omit it.</param>
public sealed record GovernedLoopGraphAuthoringRequest(
    int SchemaVersion,
    GovernedLoopRevisionLifecycleRequest LifecycleRequest,
    GovernedLoopGraphCandidate? GraphCandidate);
