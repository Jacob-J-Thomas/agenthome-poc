namespace EmbodySense.Core.Common.Loops.Admission.Models;

/// <summary>References one exact bounded admission proof without retaining source payloads or diagnostics.</summary>
/// <param name="Kind">The closed proof category.</param>
/// <param name="EvidenceHash">The canonical lowercase SHA-256 digest of the exact proof.</param>
public sealed record GovernedLoopAdmissionEvidenceReference(GovernedLoopAdmissionEvidenceKind Kind, string EvidenceHash);
