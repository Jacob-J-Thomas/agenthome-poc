namespace EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;

/// <summary>Returns one bounded durable graph mutation result and refreshed authoritative aggregate.</summary>
/// <param name="Status">The closed lowercase operation status.</param>
/// <param name="OperationId">The exact operation identity.</param>
/// <param name="AuthoringRequestHash">The canonical full-intent hash when available.</param>
/// <param name="GraphValidationEvidenceHash">The exact validation binding hash when committed.</param>
/// <param name="ChangeKind">The semantic change classification.</param>
/// <param name="Errors">Structured element- or lifecycle-attributed errors.</param>
/// <param name="Current">A refreshed exact aggregate when safely readable.</param>
public sealed record GovernedLoopGraphMutationResponse(
    string Status,
    string OperationId,
    string AuthoringRequestHash,
    string? GraphValidationEvidenceHash,
    string ChangeKind,
    IReadOnlyList<GovernedLoopElementErrorSnapshot> Errors,
    GovernedLoopGraphReadResponse? Current);
