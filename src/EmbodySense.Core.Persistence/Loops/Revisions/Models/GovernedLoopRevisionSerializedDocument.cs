namespace EmbodySense.Core.Persistence.Loops.Revisions.Models;

internal sealed record GovernedLoopRevisionSerializedDocument(
    string Json,
    string ContentDigest,
    string AuthenticationTag);
