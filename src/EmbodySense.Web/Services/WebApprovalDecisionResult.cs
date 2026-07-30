namespace EmbodySense.Web.Services;

/// <summary>
/// Reports whether a browser approval decision was accepted and provides a user-facing disposition.
/// </summary>
/// <param name="Accepted">Whether this request completed the pending approval.</param>
/// <param name="Message">The bounded disposition message.</param>
public sealed record WebApprovalDecisionResult(bool Accepted, string Message)
{
    /// <summary>
    /// Creates an accepted completion result.
    /// </summary>
    /// <param name="requestId">The completed approval request identity.</param>
    /// <returns>An accepted result.</returns>
    public static WebApprovalDecisionResult Completed(string requestId)
    {
        return new WebApprovalDecisionResult(true, $"Approval request `{requestId}` was completed.");
    }

    /// <summary>
    /// Creates a result for a request that is no longer pending.
    /// </summary>
    /// <param name="requestId">The requested approval identity.</param>
    /// <returns>A rejected not-found result.</returns>
    public static WebApprovalDecisionResult NotFound(string requestId)
    {
        return new WebApprovalDecisionResult(false, $"Approval request `{requestId}` is no longer pending.");
    }

    /// <summary>
    /// Creates a result for a request already completed by another decision.
    /// </summary>
    /// <param name="requestId">The requested approval identity.</param>
    /// <returns>A rejected already-completed result.</returns>
    public static WebApprovalDecisionResult AlreadyCompleted(string requestId)
    {
        return new WebApprovalDecisionResult(false, $"Approval request `{requestId}` was already completed.");
    }

    /// <summary>
    /// Creates a result for a request owned by a different browser connection.
    /// </summary>
    /// <param name="requestId">The requested approval identity.</param>
    /// <returns>A rejected authorization result.</returns>
    public static WebApprovalDecisionResult NotAuthorized(string requestId)
    {
        return new WebApprovalDecisionResult(false, $"Approval request `{requestId}` belongs to another browser connection.");
    }
}
