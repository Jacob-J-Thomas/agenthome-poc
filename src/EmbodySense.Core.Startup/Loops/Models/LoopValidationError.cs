namespace EmbodySense.Core.Startup.Loops.Models;

public sealed record LoopValidationError(string Code, string Field, string Message);
