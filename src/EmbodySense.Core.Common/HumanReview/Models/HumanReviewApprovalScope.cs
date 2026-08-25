namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Defines the one exact continuation or conclusively pre-dispatch effect attempt to which approval consent is restricted.</summary>
/// <param name="Kind">The exact release scope kind.</param>
/// <param name="BindingHash">The immutable canonical binding hash that approval must match.</param>
/// <param name="EffectAttemptId">The exact effect attempt only for a pre-dispatch effect scope.</param>
/// <param name="ScopeHash">The canonical hash of every prior approval-scope field.</param>
public sealed record HumanReviewApprovalScope(HumanReviewApprovalScopeKind Kind, string BindingHash, string? EffectAttemptId, string ScopeHash);
