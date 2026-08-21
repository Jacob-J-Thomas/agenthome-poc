namespace EmbodySense.Core.Application.Inference.Profiles.Models;

using EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Returns one safe deterministic model-profile page.</summary>
/// <param name="Status">The structured status.</param>
/// <param name="Items">Safe items ordered by exact capability ID.</param>
/// <param name="NextCursor">The last returned capability ID when another item remains.</param>
public sealed record ModelProfileCatalogReadResult(ModelProfileCatalogReadStatus Status, IReadOnlyList<ModelProfileCatalogItem> Items, string? NextCursor)
{
    /// <summary>Gets a defensive copy of projected items.</summary>
    public IReadOnlyList<ModelProfileCatalogItem> Items { get; } = ModelProfileApplicationContractCopy.Snapshot(Items, 50, nameof(Items));
}
