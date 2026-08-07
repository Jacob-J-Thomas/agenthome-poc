namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Provides the server-owned starting shape for a non-durable client-side loop draft.
/// </summary>
/// <param name="SchemaVersion">The custom-loop schema the first durable save will use.</param>
/// <param name="RoleId">The authoritative contextual role that will own the saved definition.</param>
/// <param name="Definition">The editable definition fields submitted at the first explicit save boundary.</param>
/// <param name="ContextDefaults">The server-owned inherited context policies used while editing.</param>
public sealed record LoopDefinitionDraftTemplate(
    int SchemaVersion,
    string RoleId,
    LoopDefinitionInput Definition,
    LoopContextDefaults ContextDefaults);
