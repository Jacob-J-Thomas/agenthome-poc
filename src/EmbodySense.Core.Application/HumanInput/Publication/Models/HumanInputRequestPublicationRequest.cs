namespace EmbodySense.Core.Application.HumanInput.Publication.Models;

/// <summary>Identifies the exact immutable checkpoint whose embedded request must be reconciled to the canonical lifecycle ledger.</summary>
/// <param name="RunId">The exact canonical custom-loop run identity.</param>
/// <param name="CheckpointId">The exact checkpoint identity within the run.</param>
/// <param name="CheckpointHash">The exact immutable checkpoint content hash observed by the caller.</param>
public sealed record HumanInputRequestPublicationRequest(string RunId, string CheckpointId, string CheckpointHash);
