using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Triggers;

namespace EmbodySense.Core.Startup.Triggers.Schedules.Models;

/// <summary>Returns one governed payload posture with isolated content evidence.</summary>
public sealed class ScheduleGovernedPayloadResolution
{
    private readonly byte[]? _content;

    /// <summary>Initializes one source result at the public adapter boundary.</summary>
    /// <remarks>The current-evidence adapter revalidates the complete shape and digest before use.</remarks>
    public ScheduleGovernedPayloadResolution(
        ScheduleGovernedPayloadResolutionStatus status,
        string? governedReference,
        CapabilityIntegrityDigest? contentHash,
        byte[]? content)
    {
        Status = status;
        GovernedReference = governedReference;
        ContentHash = contentHash;
        HasBoundedContent = content is { Length: <= TriggerDeliveryLimits.MaxInlinePayloadBytes };
        _content = HasBoundedContent ? content!.ToArray() : null;
    }

    /// <summary>Gets the closed source posture.</summary>
    public ScheduleGovernedPayloadResolutionStatus Status { get; }

    /// <summary>Gets the exact opaque identity when the source proved one.</summary>
    public string? GovernedReference { get; }

    /// <summary>Gets the source-proved exact content digest.</summary>
    public CapabilityIntegrityDigest? ContentHash { get; }

    /// <summary>Gets whether non-null source bytes fit the inline schedule payload bound and were isolated.</summary>
    public bool HasBoundedContent { get; }

    /// <summary>Returns an isolated copy of source content, or <see langword="null"/> when absent.</summary>
    public byte[]? GetContent() => _content?.ToArray();
}
