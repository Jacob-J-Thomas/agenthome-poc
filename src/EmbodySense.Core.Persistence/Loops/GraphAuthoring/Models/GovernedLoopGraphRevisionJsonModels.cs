namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record GraphRevisionArtifactJson(
    int SchemaVersion,
    string WorkspaceIdentity,
    long TrustGeneration,
    ExecutableGraphJson? ExecutableGraph,
    GraphLayoutJson? Layout,
    string? ExecutableHash,
    string? LayoutHash,
    string? PayloadHash,
    string? ContentDigest,
    string? AuthenticationTag);

internal sealed record ExecutableGraphJson(
    int SchemaVersion,
    string? GraphId,
    string? RevisionId,
    string? Purpose,
    string? OwningRoleId,
    string? EntryNodeId,
    string[]? TerminalNodeIds,
    string[]? AuthorityCeiling,
    ValueSchemaJson[]? ValueSchemas,
    NodeJson[]? Nodes,
    ControlEdgeJson[]? ControlEdges,
    BindingJson[]? Bindings,
    OutputContractJson? OutputContract);

internal sealed record ValueSchemaJson(
    string? Id,
    string? Kind,
    bool Nullable,
    string? Format,
    string? ElementSchemaId);

internal sealed record NodeJson(
    string? Id,
    string? Kind,
    string? TypeId,
    int DescriptorVersion,
    string[]? AuthorityCeiling,
    IReadOnlyDictionary<string, string>? Parameters,
    PortJson[]? Ports);

internal sealed record PortJson(
    string? Id,
    string? Direction,
    string? BindingKind,
    string? ValueSchemaId,
    bool Required);

internal sealed record ControlEdgeJson(
    string? Id,
    string? FromNodeId,
    string? ToNodeId,
    string? Condition);

internal sealed record BindingJson(
    string? Id,
    string? Kind,
    string? FromNodeId,
    string? FromPortId,
    string? ToNodeId,
    string? ToPortId);

internal sealed record OutputContractJson(
    string? Summary,
    OutputJson[]? Outputs);

internal sealed record OutputJson(
    string? Id,
    string? ValueSchemaId,
    string? SourceNodeId,
    string? SourcePortId,
    bool Required);

internal sealed record GraphLayoutJson(
    string? DisplayName,
    string? Description,
    NodeLayoutJson[]? Nodes);

internal sealed record NodeLayoutJson(
    string? NodeId,
    string? DisplayName,
    string? Description,
    int? CanvasX,
    int? CanvasY);

internal sealed record GraphRevisionIntentJson(
    int SchemaVersion,
    string? WorkspaceIdentity,
    long TrustGeneration,
    string? GraphId,
    string? OperationId,
    string? LifecycleRequestHash,
    string? AuthoringRequestHash,
    string? GraphPayloadHash,
    string? GraphValidationEvidenceHash,
    string? ContentDigest,
    string? AuthenticationTag);

internal sealed record GraphPayloadHashJson(
    int SchemaVersion,
    ExecutableGraphJson ExecutableGraph,
    GraphLayoutJson Layout);
