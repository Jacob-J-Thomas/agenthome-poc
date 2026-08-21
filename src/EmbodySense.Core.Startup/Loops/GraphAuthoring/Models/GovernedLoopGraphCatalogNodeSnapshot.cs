using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;

/// <summary>Projects one exact executable descriptor and its legal connector and configuration contract.</summary>
public sealed record GovernedLoopGraphCatalogNodeSnapshot(
    GovernedLoopNodeDescriptor Descriptor,
    bool IsAdvertised,
    bool IsExecutable,
    bool IsLegalEntry,
    bool IsLegalTerminal,
    IReadOnlyList<string> AllowedControlOutcomes,
    IReadOnlyList<string> RequiredControlOutcomes,
    string JoinPolicy,
    int MinimumIncomingControlEdges,
    bool AllowsCycle,
    string? CycleIterationBudgetParameterId,
    string? CycleTimeBudgetMillisecondsParameterId,
    IReadOnlyList<GovernedLoopGraphCatalogPortSnapshot> Ports,
    IReadOnlyList<GovernedLoopGraphCatalogParameterSnapshot> Parameters,
    IReadOnlyList<string> RequiredCapabilityIds);
