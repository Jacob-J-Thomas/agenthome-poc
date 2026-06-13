namespace EmbodySense.Core.Inference.Models;

public sealed record CodexCliProcessResult(int ExitCode, string Output, string Error);
