using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Catalog.Models;

/// <summary>Retains one exact request lifecycle and its current-version response aggregate from a single ledger generation.</summary>
/// <param name="Lifecycle">The complete request lifecycle snapshot.</param>
/// <param name="Responses">The current immutable request-version response snapshot.</param>
public sealed record HumanInputRequestCatalogEntry(
    HumanInputRequestLifecycleStoreSnapshot Lifecycle,
    HumanInputResponseLifecycleStoreSnapshot Responses);
