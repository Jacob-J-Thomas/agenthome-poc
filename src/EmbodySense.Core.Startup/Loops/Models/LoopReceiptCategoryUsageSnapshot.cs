namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Projects accounted receipt usage for one safe retention category.
/// </summary>
/// <param name="Category">The category name.</param>
/// <param name="ArtifactCount">The number of artifacts or compact proof entries in the category.</param>
/// <param name="Utf8Bytes">The aggregate UTF-8 bytes accounted to the category.</param>
public sealed record LoopReceiptCategoryUsageSnapshot(string Category, int ArtifactCount, long Utf8Bytes);
