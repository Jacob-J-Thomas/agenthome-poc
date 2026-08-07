namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Restricts a reference response to one safe opaque-reference kind and bounded identifier length.
/// </summary>
/// <param name="Kind">The permitted safe reference kind.</param>
/// <param name="MaxReferenceCharacters">The maximum reference length.</param>
public sealed record HumanInputReferencePolicy(HumanInputReferenceKind Kind, int MaxReferenceCharacters);
