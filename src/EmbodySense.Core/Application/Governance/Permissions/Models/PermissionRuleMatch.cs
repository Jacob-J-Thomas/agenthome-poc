namespace EmbodySense.Core.Application.Governance.Permissions.Models;

internal sealed record PermissionRuleMatch(FileSystemPermissionEntry Entry, int Specificity);
