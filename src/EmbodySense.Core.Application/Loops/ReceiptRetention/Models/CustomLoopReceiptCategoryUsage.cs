using EmbodySense.Core.Common.Loops.Models.Custom.Retention;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Represents the count and aggregate bytes assigned to one receipt posture category.
/// </summary>
/// <param name="Category">The artifact category.</param>
/// <param name="ArtifactCount">The number of artifacts or proof entries.</param>
/// <param name="Utf8Bytes">The accounted UTF-8 bytes.</param>
public sealed record CustomLoopReceiptCategoryUsage(CustomLoopReceiptArtifactCategory Category, int ArtifactCount, long Utf8Bytes);
