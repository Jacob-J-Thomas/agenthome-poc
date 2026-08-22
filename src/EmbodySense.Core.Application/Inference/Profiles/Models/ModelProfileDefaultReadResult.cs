using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Returns the exact configured default and configuration-source revision.</summary>
/// <param name="Status">The structured read status.</param>
/// <param name="ProfileId">The exact default profile ID when found.</param>
/// <param name="SourceRevisionHash">The exact source revision hash.</param>
public sealed record ModelProfileDefaultReadResult(ModelProfileDefaultReadStatus Status, CapabilityId? ProfileId, string? SourceRevisionHash);
