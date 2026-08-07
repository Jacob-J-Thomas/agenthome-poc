using EmbodySense.Core.Startup.Loops.Models;

namespace EmbodySense.Web.Models;

/// <summary>
/// Represents the first explicit durable save of a client-side custom-loop draft.
/// </summary>
/// <param name="OperationId">The caller-generated operation identifier reused after ambiguous outcomes.</param>
/// <param name="Definition">The complete editable definition captured at the save boundary.</param>
public sealed record CreateLoopRequest(string OperationId, LoopDefinitionInput? Definition);
