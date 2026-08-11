using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Persistence.HumanInput.Requests.Models;

internal sealed record HumanInputRequestStoreDocument(
    int SchemaVersion,
    string WorkspaceIdentity,
    long Generation,
    IReadOnlyList<HumanInputRequest> RequestVersions,
    IReadOnlyList<HumanInputRequestLifecycleHead> Heads,
    IReadOnlyList<HumanInputRequestLifecycleOperationEvidence> Operations,
    string ContentDigest,
    string AuthenticationTag)
{
    internal const int CurrentSchemaVersion = 1;
}
