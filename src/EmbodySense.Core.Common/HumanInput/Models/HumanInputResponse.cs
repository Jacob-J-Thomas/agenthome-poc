namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Records one authenticated actor's untrusted response data. Authentication identity is a reference supplied by an outer boundary, not an authorization or credential contract.
/// </summary>
/// <param name="RequestId">The exact request ID.</param>
/// <param name="RequestVersionId">The exact immutable request-version ID.</param>
/// <param name="Binding">The exact request workspace, loop graph and revision, node, run, and checkpoint binding.</param>
/// <param name="AuthenticatedActorRef">The stable actor reference obtained by an external authentication boundary.</param>
/// <param name="RespondentRoleId">The exact eligible role established by the external authentication and eligibility boundary.</param>
/// <param name="SubmittedAtUtc">The UTC submission time.</param>
/// <param name="Value">The required untrusted response data.</param>
/// <param name="Explanation">Optional bounded canonical explanation data.</param>
public sealed partial record HumanInputResponse(string RequestId, string RequestVersionId, HumanInputRequestBinding Binding, string AuthenticatedActorRef, string RespondentRoleId, DateTimeOffset SubmittedAtUtc, HumanInputResponseValue Value, string? Explanation);
