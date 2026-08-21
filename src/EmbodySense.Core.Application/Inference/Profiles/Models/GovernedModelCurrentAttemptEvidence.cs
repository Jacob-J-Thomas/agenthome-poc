namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Binds exact current authority and data-posture proof hashes after all attempt narrowing succeeds.</summary>
public sealed record GovernedModelCurrentAttemptEvidence(string AuthorityEvidenceHash, string DataPostureEvidenceHash);
