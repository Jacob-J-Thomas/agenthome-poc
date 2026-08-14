using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.PureNodes;

/// <summary>Materializes one exact graph binding and its immutable canonical typed value.</summary>
public sealed class GovernedLoopTypedBindingValue
{
    private GovernedLoopTypedBindingValue(GovernedLoopRevisionReference graphRevision, GovernedLoopBindingDefinition binding, string valueSchemaId, GovernedLoopTypedValue value)
    {
        GraphRevision = graphRevision;
        BindingId = binding.Id;
        BindingKind = binding.Kind;
        SourceNodeId = binding.FromNodeId;
        SourcePortId = binding.FromPortId;
        TargetNodeId = binding.ToNodeId;
        TargetPortId = binding.ToPortId;
        ValueSchemaId = valueSchemaId;
        Value = value;
    }

    /// <summary>The only supported materialized-binding schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the schema version.</summary>
    /// <value>Always <see cref="CurrentSchemaVersion"/>.</value>
    public int SchemaVersion => CurrentSchemaVersion;
    /// <summary>Gets the exact immutable graph revision.</summary>
    /// <value>The graph, revision, and executable-hash pin.</value>
    public GovernedLoopRevisionReference GraphRevision { get; }
    /// <summary>Gets the declared binding identity.</summary>
    /// <value>The canonical binding ID.</value>
    public string BindingId { get; }
    /// <summary>Gets the declared data or context channel.</summary>
    /// <value>The exact binding kind.</value>
    public GovernedLoopBindingKind BindingKind { get; }
    /// <summary>Gets the exact source node identity.</summary>
    /// <value>The declared source node ID.</value>
    public string SourceNodeId { get; }
    /// <summary>Gets the exact source output-port identity.</summary>
    /// <value>The declared source port ID.</value>
    public string SourcePortId { get; }
    /// <summary>Gets the exact target node identity.</summary>
    /// <value>The declared target node ID.</value>
    public string TargetNodeId { get; }
    /// <summary>Gets the exact target input-port identity.</summary>
    /// <value>The declared target port ID.</value>
    public string TargetPortId { get; }
    /// <summary>Gets the shared source/target value-schema identity.</summary>
    /// <value>The exact graph schema ID.</value>
    public string ValueSchemaId { get; }
    /// <summary>Gets the canonical typed value.</summary>
    /// <value>The immutable value evidence.</value>
    public GovernedLoopTypedValue Value { get; }

    /// <summary>Resolves and materializes one exact binding from a canonical graph.</summary>
    /// <param name="graph">The exact canonical graph revision.</param>
    /// <param name="bindingId">The declared binding identity.</param>
    /// <param name="value">The canonical typed value produced at the source port.</param>
    /// <returns>A graph-pinned materialized binding.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="graph"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the binding is absent or the value violates its exact root schema.</exception>
    public static GovernedLoopTypedBindingValue Create(GovernedLoopGraphDefinition graph, string bindingId, GovernedLoopTypedValue value)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(value);
        var binding = graph.Bindings.SingleOrDefault(item => string.Equals(item.Id, bindingId, StringComparison.Ordinal));
        if (binding is null)
        {
            throw new ArgumentException("The materialized binding must reference one exact declared graph binding.", nameof(bindingId));
        }

        var target = graph.Nodes.Single(node => string.Equals(node.Id, binding.ToNodeId, StringComparison.Ordinal)).Ports.Single(port => string.Equals(port.Id, binding.ToPortId, StringComparison.Ordinal));
        var schema = graph.ValueSchemas.Single(item => string.Equals(item.Id, target.ValueSchemaId, StringComparison.Ordinal));
        RequireValue(graph, schema, value, nameof(value));
        return new GovernedLoopTypedBindingValue(Copy(graph.RevisionReference), binding, schema.Id, value);
    }

    internal static void RequireValue(GovernedLoopGraphDefinition graph, GovernedLoopValueSchemaDefinition schema, GovernedLoopTypedValue value, string parameterName)
    {
        if (!GovernedLoopTypedValueSchemaValidator.IsConformant(graph, schema, value))
        {
            throw new ArgumentException("The typed value must recursively conform to the exact declared graph schema.", parameterName);
        }
    }

    internal static GovernedLoopRevisionReference Copy(GovernedLoopRevisionReference reference)
        => GovernedLoopRevisionReference.Create(reference.SchemaVersion, reference.GraphId, reference.RevisionId, reference.ExecutableHash);
}
