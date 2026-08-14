using EmbodySense.Core.Common.Loops.Admission.Models;

namespace EmbodySense.Core.Persistence.Loops.Admission.Models;

internal sealed record GovernedLoopAdmissionStoreDocument(
    int SchemaVersion,
    string WorkspaceIdentity,
    string WorkspaceId,
    long Generation,
    IReadOnlyList<GovernedLoopAdmissionTerminalOutcome> Outcomes,
    string ContentDigest,
    string AuthenticationTag)
{
    internal const int CurrentSchemaVersion = 1;
}
