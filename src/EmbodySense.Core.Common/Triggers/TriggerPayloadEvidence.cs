using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Common.Triggers;

/// <summary>
/// Captures either bounded inline bytes or one governed payload reference with an exact content digest.
/// </summary>
public sealed class TriggerPayloadEvidence
{
    private readonly byte[]? _inlinePayload;

    internal TriggerPayloadEvidence(byte[]? inlinePayload, string? governedReference, CapabilityIntegrityDigest contentHash)
    {
        _inlinePayload = inlinePayload;
        GovernedReference = governedReference;
        ContentHash = contentHash;
    }

    /// <summary>Gets a value indicating whether the payload is represented inline.</summary>
    public bool IsInline => _inlinePayload is not null;

    /// <summary>Gets the governed payload reference when bytes are not inline.</summary>
    public string? GovernedReference { get; }

    /// <summary>Gets the exact payload content digest.</summary>
    public CapabilityIntegrityDigest ContentHash { get; }

    /// <summary>
    /// Returns an isolated copy of the inline bytes.
    /// </summary>
    /// <returns>The copied bytes, or <see langword="null"/> when a governed reference is used.</returns>
    public byte[]? GetInlinePayload() => _inlinePayload?.ToArray();
}
