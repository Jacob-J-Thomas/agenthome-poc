using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Returns exact server-owned profile metadata and its source revision.</summary>
/// <param name="Status">The structured read status.</param>
/// <param name="Metadata">Exact safe metadata when found.</param>
/// <param name="SourceRevisionHash">The exact trusted source revision hash when found.</param>
public sealed record ModelProfileSourceReadResult(ModelProfileSourceReadStatus Status, GovernedModelProfileMetadata? Metadata, string? SourceRevisionHash);
