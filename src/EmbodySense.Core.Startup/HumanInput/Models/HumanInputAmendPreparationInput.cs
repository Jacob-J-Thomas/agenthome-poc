namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Requests a bounded content/expiry/privacy amendment for one exact pending request.</summary>
/// <param name="OperationId">The caller-owned idempotency identity shared with commit.</param>
/// <param name="RequestId">The exact target request identity.</param>
/// <param name="ExpectedRequest">The exact immutable request reference observed by the surface.</param>
/// <param name="ExpectedLifecycleVersion">The exact optimistic lifecycle version observed by the surface.</param>
/// <param name="ExpectedLifecycleStatus">The exact optimistic lifecycle status observed by the surface.</param>
/// <param name="Purpose">The bounded replacement purpose.</param>
/// <param name="Prompt">The bounded replacement prompt.</param>
/// <param name="PrivacyClass">The supported replacement privacy class token.</param>
/// <param name="RequestExpiresAtUtc">The replacement request response deadline.</param>
/// <param name="CandidateExpiresAtUtc">The short trusted expiry of the prepared candidate registration.</param>
public sealed record HumanInputAmendPreparationInput(
    string OperationId,
    string RequestId,
    HumanInputSurfaceRequestReference? ExpectedRequest,
    long ExpectedLifecycleVersion,
    string ExpectedLifecycleStatus,
    string Purpose,
    string Prompt,
    string PrivacyClass,
    DateTimeOffset RequestExpiresAtUtc,
    DateTimeOffset CandidateExpiresAtUtc);
