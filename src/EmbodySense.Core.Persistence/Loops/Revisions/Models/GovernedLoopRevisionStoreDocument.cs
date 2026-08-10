using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Persistence.Loops.Revisions.Models;

internal sealed record GovernedLoopRevisionStoreDocument(
    int SchemaVersion,
    string WorkspaceIdentity,
    long Generation,
    IReadOnlyList<GovernedLoopRevisionArtifact> Artifacts,
    IReadOnlyList<GovernedLoopRevisionLifecycleHead> Heads,
    IReadOnlyList<GovernedLoopRevisionStoredOperation> Operations,
    string ContentDigest,
    string AuthenticationTag)
{
    internal const int CurrentSchemaVersion = 1;
}
