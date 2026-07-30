namespace EmbodySense.Core.Common.Governance.Tools;

/// <summary>
/// Creates structured tool permission details.
/// </summary>
public static class ToolPermissionDetails
{
    /// <summary>
    /// Identifies the outside workspace root tool permission details.
    /// </summary>
    public const string OutsideWorkspaceRoot = "Tool targets must stay within the configured workspace root.";

    /// <summary>
    /// Identifies the reparse point path tool permission details.
    /// </summary>
    public const string ReparsePointPath = "Tool targets must not pass through symbolic links, junctions, or other reparse points.";
}
