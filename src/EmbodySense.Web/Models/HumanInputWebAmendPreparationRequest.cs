namespace EmbodySense.Web.Models;

/// <summary>Supplies bounded replacement content for a server-owned Human Input amend proposal.</summary>
/// <param name="OperationId">The bounded idempotency identity shared with the later commit.</param>
/// <param name="ExpectedLifecycleVersion">The exact lifecycle version displayed by the client.</param>
/// <param name="ExpectedLifecycleStatus">The exact lifecycle status displayed by the client.</param>
/// <param name="ExpectedRequest">The exact immutable request reference displayed by the client.</param>
/// <param name="Purpose">The bounded replacement purpose.</param>
/// <param name="Prompt">The bounded replacement prompt.</param>
/// <param name="PrivacyClass">The replacement privacy-class token, which Startup validates against the current class.</param>
/// <param name="RequestExpiresAtUtc">The replacement request deadline. Startup applies the canonical timing rules.</param>
/// <param name="CandidateExpiresAtUtc">The requested short candidate lifetime; Startup validates it against its trusted clock and finite maximum.</param>
public sealed record HumanInputWebAmendPreparationRequest(
    string OperationId,
    long ExpectedLifecycleVersion,
    string ExpectedLifecycleStatus,
    HumanInputWebRequestReference? ExpectedRequest,
    string Purpose,
    string Prompt,
    string PrivacyClass,
    DateTimeOffset RequestExpiresAtUtc,
    DateTimeOffset CandidateExpiresAtUtc);
