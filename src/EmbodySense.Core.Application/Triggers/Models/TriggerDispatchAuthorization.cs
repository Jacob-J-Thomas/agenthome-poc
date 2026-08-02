namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Returns a bounded current-evidence decision and exact proof hash.</summary>
/// <param name="Status">The closed authorization posture.</param>
/// <param name="EvidenceHash">The lowercase SHA-256 current-evidence binding.</param>
/// <param name="Detail">The bounded inspectable reason.</param>
public sealed record TriggerDispatchAuthorization(TriggerDispatchAuthorizationStatus Status, string EvidenceHash, string Detail);
