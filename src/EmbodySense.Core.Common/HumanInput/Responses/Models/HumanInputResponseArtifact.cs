using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.HumanInput.Responses.Models;

/// <summary>Retains one immutable authenticated response as privacy-classified untrusted data without granting approval, consent, credential access, or effect authority.</summary>
/// <param name="SchemaVersion">The response-artifact schema version.</param>
/// <param name="ResponseId">The stable response identifier.</param>
/// <param name="Request">The exact immutable request version answered by this response.</param>
/// <param name="Binding">The exact workspace, graph, revision, node, run, and checkpoint binding.</param>
/// <param name="ActorId">The authenticated actor retained as provenance, not authority.</param>
/// <param name="RespondentRoleId">The exact request-eligible role established by trusted policy.</param>
/// <param name="SubmittedAtUtc">The trusted UTC commit time.</param>
/// <param name="PrivacyClass">The request privacy classification retained with the response.</param>
/// <param name="Value">The immutable untrusted typed response data.</param>
/// <param name="Explanation">Optional bounded untrusted explanation data.</param>
/// <param name="ValueHash">The canonical response-value digest.</param>
/// <param name="ResponseHash">The canonical full-artifact digest.</param>
public sealed partial record HumanInputResponseArtifact(
    int SchemaVersion,
    string ResponseId,
    HumanInputRequestReference Request,
    HumanInputRequestBinding Binding,
    AuthorityActorId ActorId,
    string RespondentRoleId,
    DateTimeOffset SubmittedAtUtc,
    HumanInputPrivacyClass PrivacyClass,
    HumanInputResponseValue Value,
    string? Explanation,
    string ValueHash,
    string ResponseHash)
{
    /// <summary>The only supported response-artifact schema version.</summary>
    public const int CurrentSchemaVersion = HumanInputResponseContractLimits.CurrentSchemaVersion;
}
