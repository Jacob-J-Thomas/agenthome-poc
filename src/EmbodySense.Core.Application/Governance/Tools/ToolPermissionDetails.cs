namespace EmbodySense.Core.Application.Governance.Tools;

public static class ToolPermissionDetails
{
    public const string OutsideWorkspaceRoot = "Tool targets must stay within the configured workspace root.";

    public const string ReparsePointPath = "Tool targets must not pass through symbolic links, junctions, or other reparse points.";
}
