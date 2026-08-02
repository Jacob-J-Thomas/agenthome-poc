using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Reports one explicit at-least-once credential outcome-audit reconciliation pass.</summary>
public sealed record CredentialLifecycleAuditDrainResult(int DeliveredCount, int RemainingCount, CredentialFailure? Failure);
