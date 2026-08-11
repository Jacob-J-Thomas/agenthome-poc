using System.Collections.ObjectModel;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.PureNodes;

/// <summary>Contains one immutable canonical pure-node outcome suitable for durable restart evidence.</summary>
/// <remarks>Run and attempt correlation belong to the containing sequential event; this artifact binds only exact graph, node, input, output, and validation content.</remarks>
public sealed class GovernedLoopPureNodeOutcome
{
    internal GovernedLoopPureNodeOutcome(
        GovernedLoopRevisionReference graphRevision,
        string nodeId,
        GovernedLoopNodeDescriptor descriptor,
        GovernedLoopTypedBindingValue[] inputs,
        GovernedLoopTypedNodeOutput[] outputs,
        GovernedLoopValidationEvidence? validationEvidence,
        string canonicalPayloadJson,
        string canonicalJson,
        string contentHash)
    {
        GraphRevision = graphRevision;
        NodeId = nodeId;
        Descriptor = descriptor;
        Inputs = new ReadOnlyCollection<GovernedLoopTypedBindingValue>(inputs);
        Outputs = new ReadOnlyCollection<GovernedLoopTypedNodeOutput>(outputs);
        ValidationEvidence = validationEvidence;
        CanonicalPayloadJson = canonicalPayloadJson;
        CanonicalJson = canonicalJson;
        ContentHash = contentHash;
    }

    /// <summary>The only supported pure-node outcome schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the schema version.</summary>
    /// <value>Always <see cref="CurrentSchemaVersion"/>.</value>
    public int SchemaVersion => CurrentSchemaVersion;
    /// <summary>Gets the exact immutable graph revision.</summary>
    /// <value>The graph, revision, and executable-hash pin.</value>
    public GovernedLoopRevisionReference GraphRevision { get; }
    /// <summary>Gets the exact executed node identity.</summary>
    /// <value>The canonical graph node ID.</value>
    public string NodeId { get; }
    /// <summary>Gets the exact schema-1 descriptor.</summary>
    /// <value>The Transform or Validate descriptor key.</value>
    public GovernedLoopNodeDescriptor Descriptor { get; }
    /// <summary>Gets every exact materialized input binding in binding-ID order.</summary>
    /// <value>The immutable input witnesses.</value>
    public IReadOnlyList<GovernedLoopTypedBindingValue> Inputs { get; }
    /// <summary>Gets every materialized node output in port-ID order.</summary>
    /// <value>The immutable output witnesses.</value>
    public IReadOnlyList<GovernedLoopTypedNodeOutput> Outputs { get; }
    /// <summary>Gets bounded validation evidence for Validate outcomes, otherwise <see langword="null"/>.</summary>
    /// <value>The Boolean result and path/code observations.</value>
    public GovernedLoopValidationEvidence? ValidationEvidence { get; }
    /// <summary>Gets the exact canonical artifact JSON including its verified content hash.</summary>
    /// <value>The durable byte-stable schema-1 outcome.</value>
    public string CanonicalJson { get; }
    /// <summary>Gets the lowercase SHA-256 digest of the canonical payload excluding its self-referential hash field.</summary>
    /// <value>The exact outcome evidence identity.</value>
    public string ContentHash { get; }

    internal string CanonicalPayloadJson { get; }

    /// <summary>Creates one graph-resolved canonical pure-node outcome.</summary>
    /// <param name="graph">The exact canonical graph revision.</param>
    /// <param name="nodeId">The exact Transform or Validate node identity.</param>
    /// <param name="inputs">Every exact materialized binding targeting the node.</param>
    /// <param name="outputs">Every required and any present optional node output.</param>
    /// <param name="validationEvidence">Required only for Validate and forbidden for Transform.</param>
    /// <param name="outcome">The immutable canonical artifact on success.</param>
    /// <param name="validation">The deterministic validation result.</param>
    /// <returns><see langword="true"/> only when the complete outcome is valid.</returns>
    public static bool TryCreate(
        GovernedLoopGraphDefinition? graph,
        string? nodeId,
        IEnumerable<GovernedLoopTypedBindingValue>? inputs,
        IEnumerable<GovernedLoopTypedNodeOutput>? outputs,
        GovernedLoopValidationEvidence? validationEvidence,
        out GovernedLoopPureNodeOutcome? outcome,
        out GovernedLoopPureNodeOutcomeValidationResult validation)
        => GovernedLoopPureNodeOutcomeJson.TryCreate(graph, nodeId, inputs, outputs, validationEvidence, out outcome, out validation);

    /// <summary>Reads an exact canonical outcome against its immutable graph without aliases or fallback normalization.</summary>
    /// <param name="graph">The exact canonical graph revision.</param>
    /// <param name="json">The candidate canonical schema-1 outcome.</param>
    /// <param name="outcome">The immutable verified artifact on success.</param>
    /// <param name="validation">The deterministic validation result.</param>
    /// <returns><see langword="true"/> only when the input is byte-for-byte canonical and graph-exact.</returns>
    public static bool TryDeserialize(GovernedLoopGraphDefinition? graph, string? json, out GovernedLoopPureNodeOutcome? outcome, out GovernedLoopPureNodeOutcomeValidationResult validation)
        => GovernedLoopPureNodeOutcomeJson.TryDeserialize(graph, json, out outcome, out validation);
}
