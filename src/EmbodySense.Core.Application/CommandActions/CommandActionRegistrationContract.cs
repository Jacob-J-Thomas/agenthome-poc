using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.CommandActions;

namespace EmbodySense.Core.Application.CommandActions;

/// <summary>Validates one exact server-owned template-to-artifact registration.</summary>
public static class CommandActionRegistrationContract
{
    /// <summary>Returns a closed reason code when a registration is malformed or contradictory.</summary>
    public static string? Validate(CommandActionRegistration? registration)
    {
        if (registration?.Template is null || registration.Manifest is null)
        {
            return "command-registration-required";
        }
        var template = registration.Template;
        var manifest = registration.Manifest;
        if (CommandActionTemplateContract.Validate(template) is not null
            || !CapabilityArtifactManifestValidator.Validate(manifest).IsValid
            || !CapabilityDescriptorIdentity.TryCreate(manifest.Descriptor, out var identity, out _)
            || !Equals(identity, template.Capability)
            || !Equals(manifest.Descriptor.Implementation, template.Implementation)
            || !manifest.Checksum.FixedTimeEquals(template.ArtifactDigest)
            || manifest.Descriptor.Kind != CapabilityKind.Actuator
            || manifest.Arguments.Count != 0
            || manifest.Platform.Equals(CapabilityPlatform.Any))
        {
            return "command-registration-artifact-pin-invalid";
        }
        var resources = manifest.Descriptor.ResourceLimits;
        if (resources.MaxExecutionMilliseconds != template.Isolation.MaxExecutionMilliseconds
            || resources.MaxMemoryBytes != template.Isolation.MaxMemoryBytes
            || resources.MaxOutputBytes != template.Isolation.MaxOutputBytes
            || resources.MaxConcurrency != template.Isolation.MaxConcurrency)
        {
            return "command-registration-resource-policy-conflict";
        }
        var requirements = manifest.Descriptor.Requirements;
        if (requirements.EgressMode != CapabilityEgressMode.None
            || requirements.EgressDestinations.Count != 0
            || template.RequiresCredentialChannel != (requirements.Secrets.Count > 0))
        {
            return "command-registration-access-policy-conflict";
        }
        return null;
    }
}
