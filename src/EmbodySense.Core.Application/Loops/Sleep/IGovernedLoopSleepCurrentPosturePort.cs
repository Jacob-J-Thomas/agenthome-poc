using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Application.Loops.Sleep;

/// <summary>Reads one fresh authoritative execution posture for sleep publication and wake admission.</summary>
public interface IGovernedLoopSleepCurrentPosturePort
{
    /// <summary>Reads the current canonical planes, publication, unattended authority, and expiry for one exact execution.</summary>
    /// <param name="binding">The exact run, revision, and execution generation.</param>
    /// <param name="cancellationToken">The token used while reading authoritative state.</param>
    /// <returns>A fresh bounded posture result, or <see langword="null"/> when an adapter violates the port contract.</returns>
    Task<GovernedLoopSleepCurrentPostureReadResult?> ReadAsync(GovernedLoopExecutionBinding binding, CancellationToken cancellationToken = default);
}
