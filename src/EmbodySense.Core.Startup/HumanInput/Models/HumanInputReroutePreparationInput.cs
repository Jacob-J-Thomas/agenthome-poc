namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Requests server-generated bounded reroute alternatives for one exact pending request.</summary>
/// <param name="OperationId">The caller-owned idempotency identity shared with commit.</param>
/// <param name="RequestId">The exact target request identity.</param>
/// <param name="ExpectedRequest">The exact immutable request reference observed by the surface.</param>
/// <param name="ExpectedLifecycleVersion">The exact optimistic lifecycle version observed by the surface.</param>
/// <param name="ExpectedLifecycleStatus">The exact optimistic lifecycle status observed by the surface.</param>
/// <param name="CandidateExpiresAtUtc">The short trusted expiry of the prepared candidate registrations.</param>
public sealed record HumanInputReroutePreparationInput(
    string OperationId,
    string RequestId,
    HumanInputSurfaceRequestReference? ExpectedRequest,
    long ExpectedLifecycleVersion,
    string ExpectedLifecycleStatus,
    DateTimeOffset CandidateExpiresAtUtc);
