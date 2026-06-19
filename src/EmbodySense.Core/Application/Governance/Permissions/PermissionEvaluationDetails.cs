namespace EmbodySense.Core.Application.Governance.Permissions;

public static class PermissionEvaluationDetails
{
    public const string ApprovedWithoutAdditionalHumanApproval = "Approved without additional human approval.";

    public const string MissingOrUnsupportedDocument = "permissions.json is missing, invalid, or unsupported.";

    public const string ExplicitDirectoryDeny = "Denied by explicit directory rule.";

    public const string ApprovedDirectoryRequiresHumanApproval = "Approved directory rule requires human approval before use.";

    public const string NoMatchingDirectoryRule = "No approved or denied directory rule matched.";
}
