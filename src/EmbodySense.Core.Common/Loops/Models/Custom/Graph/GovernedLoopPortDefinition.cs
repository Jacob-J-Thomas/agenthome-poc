namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Declares one typed input or output port on a governed node.</summary>
/// <param name="Id">The node-local stable port identifier.</param>
/// <param name="Direction">Whether the port receives or produces a value.</param>
/// <param name="BindingKind">Whether the value is data or deliberately admitted context.</param>
/// <param name="ValueSchemaId">The declared value-schema identifier.</param>
/// <param name="Required">Whether an input must have exactly one explicit binding, or an output must be produced.</param>
public sealed record GovernedLoopPortDefinition(string Id, GovernedLoopPortDirection Direction, GovernedLoopBindingKind BindingKind, string ValueSchemaId, bool Required);
