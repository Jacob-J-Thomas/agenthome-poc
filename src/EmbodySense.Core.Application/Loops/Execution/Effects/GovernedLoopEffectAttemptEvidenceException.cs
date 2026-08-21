namespace EmbodySense.Core.Application.Loops.Execution.Effects;

/// <summary>Signals that durable protocol evidence could not be proved before continuation.</summary>
internal sealed class GovernedLoopEffectAttemptEvidenceException(string message) : Exception(message);
