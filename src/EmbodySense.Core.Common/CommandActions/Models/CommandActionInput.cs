namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Supplies only typed values for one exact server-owned command template.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="TemplateId">The exact template identity.</param>
/// <param name="TemplateVersion">The exact immutable template version.</param>
/// <param name="TemplateHash">The exact template content hash.</param>
/// <param name="Values">The name-ordered typed slot values.</param>
public sealed record CommandActionInput(int SchemaVersion, string TemplateId, long TemplateVersion, string TemplateHash, IReadOnlyList<CommandActionSlotValue> Values)
{
    /// <summary>Gets a defensive immutable copy of the typed values.</summary>
    public IReadOnlyList<CommandActionSlotValue> Values { get; } = Values is null ? null! : Array.AsReadOnly(Values.ToArray());
}
