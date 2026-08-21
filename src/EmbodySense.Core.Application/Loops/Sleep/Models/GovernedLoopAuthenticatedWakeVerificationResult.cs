namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Reports one trusted authenticated-event verification attempt.</summary>
/// <param name="Status">The closed verification status.</param>
/// <param name="Verification">The exact verification evidence, present only when verified.</param>
public sealed record GovernedLoopAuthenticatedWakeVerificationResult(
    GovernedLoopAuthenticatedWakeVerificationStatus Status,
    GovernedLoopAuthenticatedWakeVerification? Verification = null);
