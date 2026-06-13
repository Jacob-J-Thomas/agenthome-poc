namespace EmbodySense.Core.Permissions;

public sealed record PermissionEvaluation(PermissionDecision Decision, string MatchedPath, string Detail)
{
    public static PermissionEvaluation Allowed(string matchedPath) => new(PermissionDecision.Allow, matchedPath, "Approved without additional human approval.");

    public static PermissionEvaluation RequiresApproval(string matchedPath, string detail) => new(PermissionDecision.RequiresApproval, matchedPath, detail);

    public static PermissionEvaluation Denied(string matchedPath) => new(PermissionDecision.Deny, matchedPath, "Denied by explicit directory rule.");
}
