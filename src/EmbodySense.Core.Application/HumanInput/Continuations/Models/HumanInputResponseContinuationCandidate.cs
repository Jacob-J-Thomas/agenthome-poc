namespace EmbodySense.Core.Application.HumanInput.Continuations.Models;

/// <summary>Identifies one response-continuation candidate discovered from a canonical run page.</summary>
/// <param name="RunId">The exact canonical run identity.</param>
/// <param name="CheckpointId">The exact Human Input checkpoint identity within the run.</param>
public sealed record HumanInputResponseContinuationCandidate(string RunId, string CheckpointId);
