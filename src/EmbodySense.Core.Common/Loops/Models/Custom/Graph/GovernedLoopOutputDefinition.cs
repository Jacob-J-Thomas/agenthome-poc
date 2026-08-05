namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Declares one named successful loop output and its explicit source port.</summary>
/// <param name="Id">The stable output identifier.</param>
/// <param name="ValueSchemaId">The output value-schema identifier.</param>
/// <param name="SourceNodeId">The source node identifier.</param>
/// <param name="SourcePortId">The source output-port identifier.</param>
/// <param name="Required">Whether successful completion must produce the output.</param>
public sealed record GovernedLoopOutputDefinition(string Id, string ValueSchemaId, string SourceNodeId, string SourcePortId, bool Required);
