using EmbodySense.Core.Common.Loops.Custom.Graph;

namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record GovernedLoopGraphRevisionArtifactDocument(
    GovernedLoopGraphDefinition Graph,
    string LayoutHash,
    string PayloadHash,
    string WorkspaceIdentity,
    long TrustGeneration,
    string ContentDigest,
    string AuthenticationTag);
