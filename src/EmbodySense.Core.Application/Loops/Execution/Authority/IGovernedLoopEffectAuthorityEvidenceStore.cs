using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Authority;

/// <summary>Appends immutable effect-authority decisions before any protected continuation may cross its boundary.</summary>
public interface IGovernedLoopEffectAuthorityEvidenceStore
{
    /// <summary>Appends or exactly replays one validated authority decision.</summary>
    /// <param name="decision">The canonical immutable authority decision.</param>
    /// <param name="cancellationToken">A token that cancels persistence before its outcome is known.</param>
    /// <returns>The exact append, replay, conflict, unavailable, or ambiguous posture.</returns>
    Task<GovernedLoopEffectAuthorityEvidenceStoreResult> AppendAsync(GovernedLoopEffectAuthorityDecision decision, CancellationToken cancellationToken = default);
}
