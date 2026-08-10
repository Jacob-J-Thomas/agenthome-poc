namespace EmbodySense.Core.Startup.Capabilities.Models;

/// <summary>Captures one bounded client request to retire an exact durable capability lifecycle preview.</summary>
public sealed record CapabilityLifecycleDiscardInput(
    string OperationId,
    string Operation,
    string CapabilityId,
    string? TargetVersion,
    long BaselineCatalogRevision,
    long BaselineActivationRevision,
    long LifecycleRevision,
    long DependentSetRevision,
    string DependentSetHash,
    string PreviewHash);
