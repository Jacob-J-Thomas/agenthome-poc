using System.Text.Json.Nodes;

namespace EmbodySense.Core.Persistence.Loops.Models;

internal sealed record StructuralEntry(string Id, JsonObject Value);
