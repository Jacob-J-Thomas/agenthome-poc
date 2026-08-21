using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Requests durable publication of one exact waiting-frontier checkpoint.</summary>
/// <param name="Binding">The exact waiting activation, visit, cycle, and attempt binding.</param>
/// <param name="WakeMode">The admitted wake mode.</param>
/// <param name="WakeDeadlineUtc">The exact timestamp eligibility boundary for a timestamp wake.</param>
/// <param name="AuthenticatedEventReference">The already-admitted event subscription reference for an event wake.</param>
/// <param name="CheckpointPreparedAtUtc">The stable UTC checkpoint-preparation instant retained by the parking transaction.</param>
public sealed record GovernedLoopSleepPublicationRequest(
    GovernedLoopSleepBinding Binding,
    GovernedLoopWakeMode WakeMode,
    DateTimeOffset? WakeDeadlineUtc,
    string? AuthenticatedEventReference,
    DateTimeOffset? CheckpointPreparedAtUtc = null);
