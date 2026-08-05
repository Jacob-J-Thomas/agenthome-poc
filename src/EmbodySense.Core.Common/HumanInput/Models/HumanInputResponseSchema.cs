namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Defines exactly one bounded, data-only response schema. Fields irrelevant to <see cref="Kind"/> must be absent.
/// </summary>
/// <param name="Kind">The response data shape.</param>
/// <param name="MaxTextCharacters">The text limit for a text schema.</param>
/// <param name="Choices">The choices for a choice schema.</param>
/// <param name="StructuredFields">The fields for a structured schema.</param>
/// <param name="ReferencePolicy">The safe-reference policy for a reference schema.</param>
public sealed record HumanInputResponseSchema(HumanInputResponseKind Kind, int? MaxTextCharacters, HumanInputChoice[]? Choices, HumanInputStructuredFieldSchema[]? StructuredFields, HumanInputReferencePolicy? ReferencePolicy);
