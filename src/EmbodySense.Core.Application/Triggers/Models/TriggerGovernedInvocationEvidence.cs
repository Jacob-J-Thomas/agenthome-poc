namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Binds one trigger outcome to the exact governed custom-loop admission receipt and definition.</summary>
/// <param name="OperationId">The exact idempotent invocation operation identity.</param>
/// <param name="RunId">The exact durable run identity returned by the governed runtime.</param>
/// <param name="AdmissionRequestHash">The governed run's exact admission request hash.</param>
/// <param name="LoopId">The exact admitted loop identity.</param>
/// <param name="DefinitionVersion">The exact admitted definition version.</param>
/// <param name="DefinitionHash">The exact admitted definition content hash.</param>
public sealed record TriggerGovernedInvocationEvidence(string OperationId, string RunId, string AdmissionRequestHash, string LoopId, int DefinitionVersion, string DefinitionHash);
