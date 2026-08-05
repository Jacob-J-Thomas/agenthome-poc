namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Carries a raw, potentially invalid schema-1 graph candidate into fail-closed normalization.</summary>
/// <remarks>This boundary deliberately permits missing values and duplicate identities so validation can return stable element-attributed errors. It owns no serialization, persistence, compatibility, layout, or execution semantics.</remarks>
public sealed record GovernedLoopGraphCandidate(
    int SchemaVersion,
    string? GraphId,
    string? RevisionId,
    string? Purpose,
    string? OwningRoleId,
    string? EntryNodeId,
    IReadOnlyList<string?>? TerminalNodeIds,
    GovernedLoopAuthorityCeiling? AuthorityCeiling,
    IReadOnlyList<GovernedLoopValueSchemaDefinition?>? ValueSchemas,
    IReadOnlyList<GovernedLoopNodeDefinition?>? Nodes,
    IReadOnlyList<GovernedLoopControlEdgeDefinition?>? ControlEdges,
    IReadOnlyList<GovernedLoopBindingDefinition?>? Bindings,
    GovernedLoopOutputContract? OutputContract,
    GovernedLoopDisplayMetadata? DisplayMetadata);
