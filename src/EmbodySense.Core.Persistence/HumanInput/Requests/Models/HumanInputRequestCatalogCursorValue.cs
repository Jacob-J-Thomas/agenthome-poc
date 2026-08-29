namespace EmbodySense.Core.Persistence.HumanInput.Requests.Models;

/// <summary>Retains one validated opaque Human Input catalog continuation payload.</summary>
internal sealed record HumanInputRequestCatalogCursorValue(long Generation, string ContentDigest, string LastRequestId);
