using System.Text.Json.Nodes;

namespace EmbodySense.Core.Persistence.Loops.Models;

/// <summary>
/// Represents a structural entry.
/// </summary>
/// <param name="Id">The ID.</param>
/// <param name="Value">The value.</param>
internal sealed record StructuralEntry(string Id, JsonObject Value);
