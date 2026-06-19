using EmbodySense.Core.Application.Governance.Permissions;

namespace EmbodySense.Core.Application.Governance.Permissions.Models;

public sealed record PermissionEvaluation(PermissionDecision Decision, string MatchedPath, string Detail)
{
    public static PermissionEvaluation Allowed(string matchedPath) => new(PermissionDecision.Allow, matchedPath, PermissionEvaluationDetails.ApprovedWithoutAdditionalHumanApproval);

    public static PermissionEvaluation RequiresApproval(string matchedPath, string detail) => new(PermissionDecision.RequiresApproval, matchedPath, detail);

    public static PermissionEvaluation Denied(string matchedPath, string detail) => new(PermissionDecision.Deny, matchedPath, detail);
}
