namespace EmbodySense.Core.Application.HumanInput.Publication.Models;

/// <summary>Reports whether the canonical Human Input request ledger can safely support publication.</summary>
/// <param name="Status">The closed publication-ledger health disposition.</param>
public sealed record HumanInputRequestPublicationHealthResult(HumanInputRequestPublicationHealthStatus Status);
