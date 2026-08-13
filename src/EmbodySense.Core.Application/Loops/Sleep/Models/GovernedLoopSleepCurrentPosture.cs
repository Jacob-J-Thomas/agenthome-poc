using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Contains one fresh authoritative posture used as an optimistic sleep or wake fence.</summary>
/// <param name="Execution">The complete canonical lifecycle, frontier, effect, and projection planes.</param>
/// <param name="Publication">The exact immutable publication admitted for the run.</param>
/// <param name="UnattendedExecutionPermitted">Whether current authority explicitly permits unattended continuation.</param>
/// <param name="UnattendedAuthorityEvidenceHash">The exact hash of the current unattended-authority decision.</param>
/// <param name="ExecutionExpiresAtUtc">The optional exclusive run-expiry boundary.</param>
/// <param name="ObservedAtUtc">The trusted UTC instant at which all returned evidence was read consistently.</param>
/// <param name="PostureHash">The exact optimistic hash of the complete returned posture.</param>
public sealed record GovernedLoopSleepCurrentPosture(
    GovernedLoopExecutionEvidenceSet Execution,
    GovernedLoopRevisionPublicationPin Publication,
    bool UnattendedExecutionPermitted,
    string UnattendedAuthorityEvidenceHash,
    DateTimeOffset? ExecutionExpiresAtUtc,
    DateTimeOffset ObservedAtUtc,
    string PostureHash);
