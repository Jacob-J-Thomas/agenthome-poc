namespace EmbodySense.Core.Tools.Models;

public sealed record ToolRequest(ToolCommand Command, string TargetPath, string? Content = null, string? Pattern = null);
