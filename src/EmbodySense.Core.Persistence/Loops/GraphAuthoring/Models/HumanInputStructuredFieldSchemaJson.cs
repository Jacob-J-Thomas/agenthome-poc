namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record HumanInputStructuredFieldSchemaJson(
    string? FieldId,
    string? Kind,
    bool Required,
    int? MaxTextCharacters,
    HumanInputChoiceJson?[]? Choices);
