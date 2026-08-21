using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record ExecutableGraphJson(
    int SchemaVersion,
    string? GraphId,
    string? RevisionId,
    string? Purpose,
    ContextualRoleRevisionPinJson? OwningRole,
    string? EntryNodeId,
    string[]? TerminalNodeIds,
    string[]? AuthorityCeiling,
    ValueSchemaJson[]? ValueSchemas,
    NodeJson[]? Nodes,
    ControlEdgeJson[]? ControlEdges,
    BindingJson[]? Bindings,
    OutputContractJson? OutputContract,
    GovernedModelRoutingPolicy? DefaultModelRoutingPolicy);
