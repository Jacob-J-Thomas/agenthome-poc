namespace EmbodySense.Core.Application.CommandActions.Models;

/// <summary>Returns the bounded result of probing one exact command attempt after restart.</summary>
/// <param name="Posture">The closed reconciliation posture.</param>
/// <param name="Outcome">The exact conclusive outcome when authenticated.</param>
public sealed record CommandActionReconciliationProbeResult(
    CommandActionReconciliationPosture Posture,
    CommandActionNativeOutcome? Outcome);
