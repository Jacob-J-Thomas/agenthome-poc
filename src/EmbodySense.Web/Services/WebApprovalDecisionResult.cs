namespace EmbodySense.Web.Services;

public sealed record WebApprovalDecisionResult(bool Accepted, string Message)
{
    public static WebApprovalDecisionResult Completed(string requestId)
    {
        return new WebApprovalDecisionResult(true, $"Approval request `{requestId}` was completed.");
    }

    public static WebApprovalDecisionResult NotFound(string requestId)
    {
        return new WebApprovalDecisionResult(false, $"Approval request `{requestId}` is no longer pending.");
    }

    public static WebApprovalDecisionResult AlreadyCompleted(string requestId)
    {
        return new WebApprovalDecisionResult(false, $"Approval request `{requestId}` was already completed.");
    }

    public static WebApprovalDecisionResult NotAuthorized(string requestId)
    {
        return new WebApprovalDecisionResult(false, $"Approval request `{requestId}` belongs to another browser connection.");
    }
}
