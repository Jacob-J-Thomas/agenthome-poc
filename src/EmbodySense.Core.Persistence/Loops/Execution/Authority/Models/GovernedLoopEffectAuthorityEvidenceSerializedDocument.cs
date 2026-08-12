namespace EmbodySense.Core.Persistence.Loops.Execution.Authority.Models;

internal sealed record GovernedLoopEffectAuthorityEvidenceSerializedDocument(
    string Json,
    string ContentDigest,
    string AuthenticationTag);
