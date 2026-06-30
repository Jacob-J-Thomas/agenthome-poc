namespace EmbodySense.Core.Common.Governance.Permissions.Models;

public sealed record PermissionRuleMatch(FileSystemPermissionEntry Entry, int Specificity);
