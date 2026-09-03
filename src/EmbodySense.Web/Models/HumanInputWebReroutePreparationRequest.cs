namespace EmbodySense.Web.Models;

/// <summary>Supplies exact optimistic terms for a server-generated Human Input reroute proposal.</summary>
/// <param name="OperationId">The bounded idempotency identity shared with the later commit.</param>
/// <param name="ExpectedLifecycleVersion">The exact lifecycle version displayed by the client.</param>
/// <param name="ExpectedLifecycleStatus">The exact lifecycle status displayed by the client.</param>
/// <param name="ExpectedRequest">The exact immutable request reference displayed by the client.</param>
/// <param name="CandidateExpiresAtUtc">The requested short candidate lifetime; Startup validates it against its trusted clock and finite maximum.</param>
public sealed record HumanInputWebReroutePreparationRequest(
    string OperationId,
    long ExpectedLifecycleVersion,
    string ExpectedLifecycleStatus,
    HumanInputWebRequestReference? ExpectedRequest,
    DateTimeOffset CandidateExpiresAtUtc);
