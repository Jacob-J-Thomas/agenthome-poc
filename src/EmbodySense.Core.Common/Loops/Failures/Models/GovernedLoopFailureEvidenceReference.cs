namespace EmbodySense.Core.Common.Loops.Failures.Models;

/// <summary>References one exact bounded causal artifact without copying private source data.</summary>
/// <param name="EvidenceId">The stable evidence identity.</param>
/// <param name="EvidenceHash">The lowercase SHA-256 content hash.</param>
public sealed record GovernedLoopFailureEvidenceReference(string EvidenceId, string EvidenceHash);
