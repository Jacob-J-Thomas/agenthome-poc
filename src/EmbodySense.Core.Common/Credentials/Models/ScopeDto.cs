namespace EmbodySense.Core.Common.Credentials.Models;

internal sealed record ScopeDto(string? WorkspaceId, string? RoleId, string? LoopId, long? LoopRevision, string? NodeId, CapabilityDto? Capability, string? Service, string? Target, string? OperationClass, string? ActorId, string? NotBeforeUtc, string? NotAfterUtc);
