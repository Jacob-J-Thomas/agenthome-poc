using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Common.Loops.Execution.Retry.Models;

namespace EmbodySense.Core.Application.Loops.Retry.Models;

/// <summary>Requests ordered re-entry from one exact durable retry dispatch or routed exhaustion state.</summary>
/// <param name="Context">The immutable canonical execution context reconstructed from the run.</param>
/// <param name="RetryState">The exact durable dispatched or exhausted retry state.</param>
/// <param name="Actor">The bounded actor retained for resumed execution.</param>
public sealed record GovernedLoopRetryOrderedResumeRequest(
    GovernedLoopWaitOrderedContext Context,
    GovernedLoopRetryState RetryState,
    string Actor);
