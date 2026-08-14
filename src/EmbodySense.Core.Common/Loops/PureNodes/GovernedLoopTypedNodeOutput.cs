using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.PureNodes;

/// <summary>Materializes one exact graph node output port and its immutable canonical typed value.</summary>
public sealed class GovernedLoopTypedNodeOutput
{
    private GovernedLoopTypedNodeOutput(GovernedLoopRevisionReference graphRevision, string nodeId, GovernedLoopPortDefinition port, GovernedLoopTypedValue value)
    {
        GraphRevision = graphRevision;
        NodeId = nodeId;
        PortId = port.Id;
        BindingKind = port.BindingKind;
        ValueSchemaId = port.ValueSchemaId;
        Value = value;
    }

    /// <summary>The only supported materialized-output schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the schema version.</summary>
    /// <value>Always <see cref="CurrentSchemaVersion"/>.</value>
    public int SchemaVersion => CurrentSchemaVersion;
    /// <summary>Gets the exact immutable graph revision.</summary>
    /// <value>The graph, revision, and executable-hash pin.</value>
    public GovernedLoopRevisionReference GraphRevision { get; }
    /// <summary>Gets the exact producing node identity.</summary>
    /// <value>The declared node ID.</value>
    public string NodeId { get; }
    /// <summary>Gets the exact output-port identity.</summary>
    /// <value>The declared output port ID.</value>
    public string PortId { get; }
    /// <summary>Gets the declared data or context channel.</summary>
    /// <value>The exact port binding kind.</value>
    public GovernedLoopBindingKind BindingKind { get; }
    /// <summary>Gets the declared value-schema identity.</summary>
    /// <value>The exact graph schema ID.</value>
    public string ValueSchemaId { get; }
    /// <summary>Gets the canonical typed value.</summary>
    /// <value>The immutable value evidence.</value>
    public GovernedLoopTypedValue Value { get; }

    /// <summary>Resolves and materializes one exact output port from a canonical graph.</summary>
    /// <param name="graph">The exact canonical graph revision.</param>
    /// <param name="nodeId">The declared producing node identity.</param>
    /// <param name="portId">The declared output-port identity.</param>
    /// <param name="value">The canonical typed value produced at the port.</param>
    /// <returns>A graph-pinned materialized node output.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="graph"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the node or port is absent, the port is not an output, or the value violates its exact root schema.</exception>
    public static GovernedLoopTypedNodeOutput Create(GovernedLoopGraphDefinition graph, string nodeId, string portId, GovernedLoopTypedValue value)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(value);
        var node = graph.Nodes.SingleOrDefault(item => string.Equals(item.Id, nodeId, StringComparison.Ordinal));
        var port = node?.Ports.SingleOrDefault(item => string.Equals(item.Id, portId, StringComparison.Ordinal));
        if (port is null || port.Direction != GovernedLoopPortDirection.Output)
        {
            throw new ArgumentException("The materialized output must reference one exact declared graph output port.", nameof(portId));
        }

        var schema = graph.ValueSchemas.Single(item => string.Equals(item.Id, port.ValueSchemaId, StringComparison.Ordinal));
        GovernedLoopTypedBindingValue.RequireValue(graph, schema, value, nameof(value));
        return new GovernedLoopTypedNodeOutput(GovernedLoopTypedBindingValue.Copy(graph.RevisionReference), node!.Id, port, value);
    }
}
