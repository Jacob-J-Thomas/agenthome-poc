using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Declares schema-version-1 intake evidence for one executable capability artifact.</summary>
/// <param name="SchemaVersion">The manifest schema version.</param>
/// <param name="Descriptor">The exact capability descriptor.</param>
/// <param name="Source">The exact artifact source.</param>
/// <param name="Checksum">The expected content checksum.</param>
/// <param name="Signature">Optional signature evidence evaluated only by server-owned trust policy.</param>
/// <param name="Platform">The artifact platform.</param>
/// <param name="EntryPoint">The contained relative executable path.</param>
/// <param name="Arguments">The bounded fixed argument vector.</param>
public sealed record CapabilityArtifactManifest(
    int SchemaVersion,
    CapabilityDescriptor Descriptor,
    CapabilityArtifactSourceReference Source,
    CapabilityIntegrityDigest Checksum,
    CapabilityArtifactSignatureEvidence? Signature,
    CapabilityPlatform Platform,
    string EntryPoint,
    IReadOnlyList<string> Arguments)
{
    /// <summary>Gets the only supported experimental artifact manifest schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets a defensive read-only snapshot of fixed executable arguments.</summary>
    public IReadOnlyList<string> Arguments { get; } = Arguments is null ? null! : Array.AsReadOnly(Arguments.ToArray());
}
