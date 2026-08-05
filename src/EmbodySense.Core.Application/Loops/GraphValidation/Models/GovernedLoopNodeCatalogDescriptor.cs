using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.GraphValidation.Models;

/// <summary>Defines current semantics for one exact node descriptor key without implying executable support.</summary>
/// <param name="Descriptor">The exact kind, type identity, and version key.</param>
/// <param name="IsAdvertised">Whether the current catalog still advertises the declaration.</param>
/// <param name="IsExecutable">Whether the current harness explicitly supports execution; availability alone never implies support.</param>
/// <param name="IsLegalEntry">Whether the descriptor may be the graph's sole trigger entry.</param>
/// <param name="IsLegalTerminal">Whether the descriptor may be a declared terminal.</param>
/// <param name="AllowedControlOutcomes">The complete set of control outcomes the descriptor may emit.</param>
/// <param name="RequiredControlOutcomes">The branch outcomes that must each have an outgoing control edge.</param>
/// <param name="JoinPolicy">The descriptor's control-arrival policy.</param>
/// <param name="MinimumIncomingControlEdges">The minimum incoming edges required to satisfy the descriptor.</param>
/// <param name="AllowsCycle">Whether the descriptor may participate in a bounded cycle.</param>
/// <param name="CycleIterationBudgetParameterId">The catalog-declared parameter carrying the explicit positive iteration budget, or <see langword="null"/> when cycles are forbidden.</param>
/// <param name="CycleTimeBudgetMillisecondsParameterId">The catalog-declared parameter carrying the explicit positive time budget, or <see langword="null"/> when cycles are forbidden.</param>
/// <param name="Ports">The exact node-local port contracts.</param>
/// <param name="Parameters">The exact executable parameter contracts; undeclared parameters are never admitted.</param>
/// <param name="RequiredCapabilityIds">The capabilities the node ceiling must explicitly contain.</param>
/// <param name="ResourceBudget">The descriptor's fixed resource envelope.</param>
public sealed record GovernedLoopNodeCatalogDescriptor(
    GovernedLoopNodeDescriptor Descriptor,
    bool IsAdvertised,
    bool IsExecutable,
    bool IsLegalEntry,
    bool IsLegalTerminal,
    IReadOnlyList<GovernedLoopControlCondition> AllowedControlOutcomes,
    IReadOnlyList<GovernedLoopControlCondition> RequiredControlOutcomes,
    GovernedLoopJoinPolicy JoinPolicy,
    int MinimumIncomingControlEdges,
    bool AllowsCycle,
    string? CycleIterationBudgetParameterId,
    string? CycleTimeBudgetMillisecondsParameterId,
    IReadOnlyList<GovernedLoopCatalogPortContract> Ports,
    IReadOnlyList<GovernedLoopCatalogParameterContract> Parameters,
    IReadOnlyList<string> RequiredCapabilityIds,
    GovernedLoopNodeResourceBudget ResourceBudget);
