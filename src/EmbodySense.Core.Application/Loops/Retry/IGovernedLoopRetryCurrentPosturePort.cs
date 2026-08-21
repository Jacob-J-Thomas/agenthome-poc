using EmbodySense.Core.Application.Loops.Retry.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Retry.Models;
using EmbodySense.Core.Common.Loops.Failures.Models;

namespace EmbodySense.Core.Application.Loops.Retry;

/// <summary>Reads fresh lifecycle, authority, dependency, and authoritative usage posture for one retry decision.</summary>
public interface IGovernedLoopRetryCurrentPosturePort
{
    /// <summary>Reads one exact current posture without granting or reserving a retry.</summary>
    Task<GovernedLoopRetryCurrentPostureReadResult?> ReadAsync(
        CustomLoopRunRecord run,
        GovernedLoopRetryPolicy policy,
        GovernedLoopFailureEvidence failure,
        CancellationToken cancellationToken = default);
}
