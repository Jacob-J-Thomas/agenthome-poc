namespace EmbodySense.Core.Common.Governance.Permissions;

/// <summary>
/// Creates structured permission evaluation details.
/// </summary>
public static class PermissionEvaluationDetails
{
    /// <summary>
    /// Identifies the approved without additional human approval permission evaluation details.
    /// </summary>
    public const string ApprovedWithoutAdditionalHumanApproval = "Approved without additional human approval.";

    /// <summary>
    /// Identifies the missing or unsupported document permission evaluation details.
    /// </summary>
    public const string MissingOrUnsupportedDocument = "permissions.json is missing, invalid, or unsupported.";

    /// <summary>
    /// Identifies the explicit directory deny permission evaluation details.
    /// </summary>
    public const string ExplicitDirectoryDeny = "Denied by explicit directory rule.";

    /// <summary>
    /// Identifies the approved directory requires human approval permission evaluation details.
    /// </summary>
    public const string ApprovedDirectoryRequiresHumanApproval = "Approved directory rule requires human approval before use.";

    /// <summary>
    /// Identifies the no matching directory rule permission evaluation details.
    /// </summary>
    public const string NoMatchingDirectoryRule = "No approved or denied directory rule matched.";
}
