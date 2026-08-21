namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Projects one exact value-free causal evidence reference for a classified loop failure.</summary>
/// <param name="EvidenceId">The retained evidence identity.</param>
/// <param name="EvidenceHash">The lowercase SHA-256 digest of the retained evidence.</param>
public sealed record LoopRunFailureEvidenceReferenceSnapshot(string EvidenceId, string EvidenceHash);
