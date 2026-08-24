using EmbodySense.Core.Common.Loops.Execution.Retry.Models;

namespace EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;

/// <summary>Returns one server-canonicalized retry policy and finite non-granting reach preview.</summary>
public sealed record GovernedLoopRetryPolicyPreviewResponse(
    string Status,
    string Reason,
    GovernedLoopRetryPolicy? Policy,
    GovernedLoopRetryPolicyPreviewSnapshot? Preview);
