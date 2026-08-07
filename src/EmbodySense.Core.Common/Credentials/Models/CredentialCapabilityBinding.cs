using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Credentials.Models;

/// <summary>Binds a reference and declared secret requirement to one exact capability implementation and scope without granting use.</summary>
public sealed record CredentialCapabilityBinding(
    int SchemaVersion,
    CredentialReferenceId ReferenceId,
    CapabilitySecretRequirement Requirement,
    CapabilityDescriptorIdentity Capability,
    CapabilityImplementationIdentity Implementation,
    CredentialScope Scope)
{
    /// <summary>Gets the only supported experimental schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
