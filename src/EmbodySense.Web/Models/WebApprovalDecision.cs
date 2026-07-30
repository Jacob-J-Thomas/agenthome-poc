namespace EmbodySense.Web.Models;

/// <summary>
/// Represents a browser user's decision for one pending governed tool approval.
/// </summary>
public sealed record WebApprovalDecision
{
    /// <summary>
    /// Initializes the JSON request model with a rejected default decision.
    /// </summary>
    public WebApprovalDecision()
    {
    }

    /// <summary>
    /// Initializes an explicit approval decision.
    /// </summary>
    /// <param name="approved">Whether the tool request is approved.</param>
    /// <param name="detail">Optional human-supplied audit detail.</param>
    public WebApprovalDecision(bool approved, string? detail)
    {
        Approved = approved;
        Detail = detail;
    }

    /// <summary>
    /// Gets whether the pending request is approved.
    /// </summary>
    public bool Approved { get; init; } = false;

    /// <summary>
    /// Gets optional human-supplied detail retained with the decision.
    /// </summary>
    public string? Detail { get; init; }
}
