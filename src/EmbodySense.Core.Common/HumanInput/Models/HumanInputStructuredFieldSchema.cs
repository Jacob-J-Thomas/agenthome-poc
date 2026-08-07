namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Defines one bounded field in a structured response.
/// </summary>
/// <param name="FieldId">The stable field ID.</param>
/// <param name="Kind">The field data shape.</param>
/// <param name="Required">Whether the field must be present.</param>
/// <param name="MaxTextCharacters">The maximum text length when <paramref name="Kind"/> is <see cref="HumanInputStructuredFieldKind.Text"/>.</param>
/// <param name="Choices">The selectable choices when <paramref name="Kind"/> is <see cref="HumanInputStructuredFieldKind.Choice"/>.</param>
public sealed record HumanInputStructuredFieldSchema(string FieldId, HumanInputStructuredFieldKind Kind, bool Required, int? MaxTextCharacters, HumanInputChoice[]? Choices);
