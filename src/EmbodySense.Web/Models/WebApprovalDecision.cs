namespace EmbodySense.Web.Models;

public sealed record WebApprovalDecision
{
    public WebApprovalDecision()
    {
    }

    public WebApprovalDecision(bool approved, string? detail)
    {
        Approved = approved;
        Detail = detail;
    }

    public bool Approved { get; init; } = false;

    public string? Detail { get; init; }
}
