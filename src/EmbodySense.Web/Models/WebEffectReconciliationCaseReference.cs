namespace EmbodySense.Web.Models;

/// <summary>Carries only the redacted exact immutable case terms accepted from a Web client.</summary>
/// <param name="CaseId">The route-correlated immutable case identity.</param>
/// <param name="CaseVersion">The exact optimistic case version.</param>
/// <param name="ContentHash">The exact immutable case content hash.</param>
/// <param name="BindingHash">The redacted exact execution-binding hash.</param>
public sealed record WebEffectReconciliationCaseReference(string? CaseId, long CaseVersion, string? ContentHash, string? BindingHash);
