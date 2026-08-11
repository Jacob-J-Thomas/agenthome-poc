using EmbodySense.Core.Common.Loops.Admission.Models;

namespace EmbodySense.Core.Application.Loops.Admission.Models;

/// <summary>Returns one bounded governed-loop admission operation result.</summary>
/// <param name="Status">The closed application disposition.</param>
/// <param name="OperationId">The exact operation identity, or an empty value for an absent request.</param>
/// <param name="RequestHash">The trusted canonical request hash, or an empty value when unavailable.</param>
/// <param name="Outcome">The immutable admitted or rejected terminal outcome when durably proved.</param>
public sealed record GovernedLoopAdmissionResult(
    GovernedLoopAdmissionStatus Status,
    string OperationId,
    string RequestHash,
    GovernedLoopAdmissionTerminalOutcome? Outcome);
