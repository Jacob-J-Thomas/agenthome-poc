namespace EmbodySense.Core.Persistence.Inference.Profiles.Models;

/// <summary>Returns one exact model-profile metadata publication outcome.</summary>
/// <param name="Status">The structured publication status.</param>
/// <param name="SourceRevisionHash">The exact current source revision when authenticated.</param>
public sealed record ModelProfileMetadataPublishResult(ModelProfileMetadataPublishStatus Status, string? SourceRevisionHash);
