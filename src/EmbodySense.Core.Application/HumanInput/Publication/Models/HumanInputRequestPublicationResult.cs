namespace EmbodySense.Core.Application.HumanInput.Publication.Models;

/// <summary>Returns a privacy-safe publication reconciliation result for one checkpoint-backed Human Input request.</summary>
/// <param name="Status">The closed publication disposition.</param>
public sealed record HumanInputRequestPublicationResult(HumanInputRequestPublicationStatus Status);
