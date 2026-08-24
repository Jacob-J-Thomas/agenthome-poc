using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Failures.Models;

namespace EmbodySense.Core.Application.Loops.Retry.Models;

/// <summary>Requests durable scheduling for one exact classified failed activation.</summary>
/// <param name="Anchor">The immutable admitted run anchor.</param>
/// <param name="Plan">The immutable canonical plan.</param>
/// <param name="Node">The exact policy-bearing plan node.</param>
/// <param name="Failure">The exact retained retry-safe failure.</param>
/// <param name="Actor">The bounded audit actor used when ordered execution resumes.</param>
public sealed record GovernedLoopRetryExecutionRequest(
    GovernedLoopSequentialRunAnchor Anchor,
    GovernedLoopSequentialPlan Plan,
    GovernedLoopSequentialPlanNode Node,
    GovernedLoopFailureEvidence Failure,
    string Actor);
