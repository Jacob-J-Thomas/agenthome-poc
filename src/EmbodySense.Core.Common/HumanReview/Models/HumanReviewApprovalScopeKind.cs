namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies the only exact scopes to which Human Review consent may apply.</summary>
public enum HumanReviewApprovalScopeKind
{
    /// <summary>No supported approval scope was supplied.</summary>
    Unknown = 0,
    /// <summary>Consent may release only the request's exact continuation binding.</summary>
    Continuation = 1,
    /// <summary>Consent may release only the request's exact conclusively pre-dispatch effect attempt.</summary>
    PreDispatchEffect = 2
}
