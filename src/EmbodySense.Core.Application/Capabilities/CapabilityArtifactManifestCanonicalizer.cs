using System.Text;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Produces the exact server-verifiable binding for an artifact's provenance and executable semantics.</summary>
public static class CapabilityArtifactManifestCanonicalizer
{
    /// <summary>Builds the canonical signature payload after structural validation.</summary>
    public static bool TryGetSignaturePayload(CapabilityArtifactManifest manifest, out byte[] payload)
    {
        payload = [];
        if (!CapabilityArtifactManifestValidator.Validate(manifest).IsValid || !CapabilityDescriptorHash.TryCompute(manifest.Descriptor, out var descriptorHash, out _))
        {
            return false;
        }

        var builder = new StringBuilder();
        Append(builder, "artifact-manifest-v1");
        Append(builder, manifest.Checksum.Value);
        Append(builder, descriptorHash!.Value);
        Append(builder, manifest.Source.Kind.ToString());
        Append(builder, manifest.Source.Uri);
        Append(builder, manifest.Source.Revision);
        Append(builder, manifest.Source.UpdatePolicy.ToString());
        Append(builder, manifest.Platform.ToString());
        Append(builder, manifest.EntryPoint);
        Append(builder, manifest.Arguments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var argument in manifest.Arguments)
        {
            Append(builder, argument);
        }
        Append(builder, manifest.Dependencies is null ? string.Empty : CapabilityDependencyManifestHash.TryCompute(manifest.Dependencies, out var dependencyHash, out _) ? dependencyHash!.Value : throw new InvalidOperationException("Validated artifact dependencies must be canonicalizable."));
        payload = Encoding.UTF8.GetBytes(builder.ToString());
        return true;
    }

    /// <summary>Computes the server configuration pin for one exact canonical artifact manifest.</summary>
    public static CapabilityIntegrityDigest ComputePolicyPin(CapabilityArtifactManifest manifest)
    {
        if (!TryGetSignaturePayload(manifest, out var payload))
        {
            throw new ArgumentException("The artifact manifest cannot be canonicalized.", nameof(manifest));
        }
        return CapabilityIntegrityDigest.Compute(payload);
    }

    private static void Append(StringBuilder builder, string value) => builder.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n');
}
