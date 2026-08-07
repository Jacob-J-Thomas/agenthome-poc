namespace EmbodySense.Core.Common.Credentials.Models;

internal sealed record BindingDto(int SchemaVersion, string ReferenceId, string Requirement, CapabilityDto Capability, ScopeDto Scope);
