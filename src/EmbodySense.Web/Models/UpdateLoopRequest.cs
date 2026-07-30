using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Startup.Loops;

namespace EmbodySense.Web.Models;

/// <summary>
/// Represents an optimistic, idempotent custom-loop definition update.
/// </summary>
/// <param name="ExpectedDefinitionVersion">The exact durable definition version the caller observed.</param>
/// <param name="OperationId">The caller-generated mutation identity reused after ambiguous outcomes.</param>
/// <param name="Definition">The complete replacement definition input.</param>
public sealed record UpdateLoopRequest(int ExpectedDefinitionVersion, string OperationId, LoopDefinitionInput Definition);
