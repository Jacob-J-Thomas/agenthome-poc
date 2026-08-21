using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Sequential.Actions.Models;

/// <summary>Requests one exact admitted structured command Action execution or recovery.</summary>
/// <param name="Dispatch">The exact guarded canonical node-dispatch coordinates.</param>
/// <param name="GraphArtifact">The immutable graph revision containing the typed command values.</param>
/// <param name="AttemptOperationId">The exact durable frontier attempt operation identity.</param>
public sealed record GovernedLoopCommandActionExecutionRequest(
    GovernedLoopSequentialNodeDispatchRequest Dispatch,
    GovernedLoopGraphRevisionArtifact GraphArtifact,
    string AttemptOperationId);
