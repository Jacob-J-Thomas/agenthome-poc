namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Binds one trigger outcome to the exact governed-loop admission receipt and closed target reference.</summary>
/// <param name="OperationId">The exact idempotent invocation operation identity.</param>
/// <param name="RunId">The exact durable run identity returned by the governed runtime.</param>
/// <param name="AdmissionRequestHash">The governed run's exact admission request hash.</param>
/// <param name="LoopId">The exact admitted loop identity.</param>
/// <param name="LoopReferenceHash">The domain-separated hash of the exact admitted legacy or governed target reference.</param>
public sealed record TriggerGovernedInvocationEvidence(string OperationId, string RunId, string AdmissionRequestHash, string LoopId, string LoopReferenceHash);
