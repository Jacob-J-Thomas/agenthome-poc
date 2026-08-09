namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Contains one bounded artifact payload read from an explicit source.</summary>
public sealed class CapabilityArtifactContent
{
    private readonly byte[] _bytes;

    /// <summary>Creates a defensive artifact payload snapshot.</summary>
    /// <param name="bytes">The payload bytes.</param>
    public CapabilityArtifactContent(ReadOnlySpan<byte> bytes) => _bytes = bytes.ToArray();

    /// <summary>Gets the payload size.</summary>
    public int Length => _bytes.Length;

    /// <summary>Returns a defensive payload copy.</summary>
    public byte[] ToArray() => _bytes.ToArray();
}
