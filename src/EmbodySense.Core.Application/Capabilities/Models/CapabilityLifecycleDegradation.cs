namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Projects visible optional-dependent degradation evidence without mutating the dependent.</summary>
/// <param name="OperationId">The lifecycle operation causing degradation.</param>
/// <param name="DependentKind">The dependent domain.</param>
/// <param name="DependentIdentity">The dependent identity.</param>
/// <param name="DependentRevision">The exact dependent revision.</param>
/// <param name="CompatibleVersionRange">The unsatisfied declared range.</param>
/// <param name="RecordedAtUtc">The trusted mutation time.</param>
public sealed record CapabilityLifecycleDegradation(string OperationId, CapabilityDependentKind DependentKind, string DependentIdentity, string DependentRevision, string CompatibleVersionRange, DateTimeOffset RecordedAtUtc);
