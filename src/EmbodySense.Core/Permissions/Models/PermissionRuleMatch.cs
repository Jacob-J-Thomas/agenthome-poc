namespace EmbodySense.Core.Permissions.Models;

internal sealed record PermissionRuleMatch(FileSystemPermissionEntry Entry, int Specificity);
