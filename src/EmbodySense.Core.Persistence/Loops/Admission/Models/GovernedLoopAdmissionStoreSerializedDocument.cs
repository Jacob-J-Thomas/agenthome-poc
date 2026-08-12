namespace EmbodySense.Core.Persistence.Loops.Admission.Models;

internal sealed record GovernedLoopAdmissionStoreSerializedDocument(
    string Json,
    string ContentDigest,
    string AuthenticationTag);
