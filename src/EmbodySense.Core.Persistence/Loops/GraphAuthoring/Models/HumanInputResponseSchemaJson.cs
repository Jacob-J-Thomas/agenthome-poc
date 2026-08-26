namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record HumanInputResponseSchemaJson(
    string? Kind,
    int? MaxTextCharacters,
    HumanInputChoiceJson?[]? Choices,
    HumanInputStructuredFieldSchemaJson?[]? StructuredFields,
    HumanInputReferencePolicyJson? ReferencePolicy);
