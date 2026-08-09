namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Captures immutable request binding plus the latest durable dispatch posture.</summary>
/// <param name="OperationId">The idempotent governed-runner operation identity.</param>
/// <param name="RequestHash">The exact trigger dispatch request hash.</param>
/// <param name="AuthorityEvidenceHash">The exact current-evidence proof hash evaluated before intent.</param>
/// <param name="IntentRecordedAtUtc">The durable intent instant.</param>
/// <param name="Outcome">The latest closed dispatch posture.</param>
/// <param name="OutcomeRecordedAtUtc">The terminal outcome instant, or <see langword="null"/> while intent is pending.</param>
/// <param name="Detail">A bounded inspectable outcome detail.</param>
/// <param name="GovernedInvocation">The exact governed invocation receipt binding for proved accepted or terminal outcomes.</param>
public sealed record TriggerDispatchEvidence(string OperationId, string RequestHash, string AuthorityEvidenceHash, DateTimeOffset IntentRecordedAtUtc, TriggerDispatchOutcome Outcome, DateTimeOffset? OutcomeRecordedAtUtc, string Detail, TriggerGovernedInvocationEvidence? GovernedInvocation = null);
