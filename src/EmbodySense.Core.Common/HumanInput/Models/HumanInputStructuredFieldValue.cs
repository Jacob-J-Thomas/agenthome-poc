namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Contains one untrusted typed field value submitted for a structured schema.
/// </summary>
/// <param name="FieldId">The declared field ID.</param>
/// <param name="Text">The text value when the field is text.</param>
/// <param name="ChoiceId">The selected choice ID when the field is choice.</param>
public sealed record HumanInputStructuredFieldValue(string FieldId, string? Text, string? ChoiceId);
