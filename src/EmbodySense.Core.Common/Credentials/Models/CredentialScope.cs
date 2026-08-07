using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Credentials.Models;

/// <summary>Constrains credential use; null dimensions are unbounded and never imply authority.</summary>
public sealed record CredentialScope(
    string? WorkspaceId,
    string? RoleId,
    string? LoopId,
    long? LoopRevision,
    string? NodeId,
    CapabilityDescriptorIdentity? Capability,
    CapabilityImplementationIdentity? Implementation,
    string? Service,
    string? Target,
    string? OperationClass,
    string? ActorId,
    DateTimeOffset? NotBeforeUtc,
    DateTimeOffset? NotAfterUtc);
