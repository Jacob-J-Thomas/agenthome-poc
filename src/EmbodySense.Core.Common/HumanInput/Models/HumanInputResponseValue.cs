using System.Collections.Immutable;

namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Contains exactly one untrusted response data shape. A confirmation is a selected boolean datum and has no approval meaning.
/// </summary>
/// <param name="Kind">The response data shape.</param>
/// <param name="Text">The text data for a text response.</param>
/// <param name="ChoiceId">The choice ID for a choice response.</param>
/// <param name="Confirmation">The selected boolean datum for a confirmation response.</param>
/// <param name="StructuredFields">The field values for a structured response.</param>
/// <param name="Reference">The safe reference for a reference response.</param>
public sealed record HumanInputResponseValue(HumanInputResponseKind Kind, string? Text, string? ChoiceId, bool? Confirmation, ImmutableArray<HumanInputStructuredFieldValue>? StructuredFields, HumanInputReference? Reference);
