using System.Globalization;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Startup.Loops;

/// <summary>Composes the exact schema-1 executable catalog from Application-owned contracts.</summary>
internal sealed class BuiltInGovernedLoopNodeCatalog : IGovernedLoopNodeCatalog
{
    private const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    private const string ModelInferenceCapabilityId = "org.embodysense/model-inference";
    private const string ScheduleTriggerCapabilityId = "org.embodysense/triggers/time";
    private const int ProviderTransportResourceUnitsPerActivation = 1;
    private const string SourceEvidenceId = "built-in-governed-loop-node-catalog-schema-1-v1";
    private static readonly IReadOnlyList<GovernedLoopControlCondition> _always =
        Array.AsReadOnly(new[] { GovernedLoopControlCondition.Always });
    private static readonly IReadOnlyList<GovernedLoopControlCondition> _success =
        Array.AsReadOnly(new[] { GovernedLoopControlCondition.Success });
    private static readonly GovernedLoopValueKindSet _textKind =
        GovernedLoopValueKindSet.Create([GovernedLoopValueKind.Text]);
    private static readonly GovernedLoopNodeCatalogSnapshot _snapshot = CreateSnapshot();

    /// <inheritdoc />
    public Task<GovernedLoopNodeCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_snapshot);
    }

    private static GovernedLoopNodeCatalogSnapshot CreateSnapshot()
    {
        var descriptors = BaselineDescriptors()
            .Concat(GovernedLoopPureNodeCatalogContract.Descriptors)
            .Concat(GovernedLoopTopologyNodeCatalogContract.Descriptors)
            .OrderBy(DescriptorKey, StringComparer.Ordinal)
            .ToArray();
        if (descriptors.Length != 19
            || descriptors.Select(item => item.Descriptor).Distinct().Count() != descriptors.Length
            || descriptors.Any(item => !GovernedLoopSequentialNodeDescriptors.IsSupported(item.Descriptor)))
        {
            throw new InvalidOperationException("The built-in schema-1 governed-loop catalog is incomplete or contains an unsupported descriptor.");
        }

        return new GovernedLoopNodeCatalogSnapshot(
            IsAvailable: true,
            SourceEvidenceId,
            Array.AsReadOnly(descriptors));
    }

    private static IReadOnlyList<GovernedLoopNodeCatalogDescriptor> BaselineDescriptors()
        => Array.AsReadOnly(new[]
        {
            new GovernedLoopNodeCatalogDescriptor(
                GovernedLoopSequentialNodeDescriptors.ManualTrigger,
                IsAdvertised: true,
                IsExecutable: true,
                IsLegalEntry: true,
                IsLegalTerminal: false,
                _always,
                _always,
                GovernedLoopJoinPolicy.None,
                MinimumIncomingControlEdges: 0,
                AllowsCycle: false,
                CycleIterationBudgetParameterId: null,
                CycleTimeBudgetMillisecondsParameterId: null,
                Array.AsReadOnly(new[]
                {
                    Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
                    Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context),
                }),
                Array.Empty<GovernedLoopCatalogParameterContract>(),
                Array.Empty<string>(),
                ActivationBudget(evidenceItems: 1)),
            new GovernedLoopNodeCatalogDescriptor(
                GovernedLoopSequentialNodeDescriptors.ScheduleTrigger,
                IsAdvertised: true,
                IsExecutable: true,
                IsLegalEntry: true,
                IsLegalTerminal: false,
                _always,
                _always,
                GovernedLoopJoinPolicy.None,
                MinimumIncomingControlEdges: 0,
                AllowsCycle: false,
                CycleIterationBudgetParameterId: null,
                CycleTimeBudgetMillisecondsParameterId: null,
                Array.AsReadOnly(new[]
                {
                    Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
                    Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context),
                }),
                Array.Empty<GovernedLoopCatalogParameterContract>(),
                Array.AsReadOnly(new[] { ScheduleTriggerCapabilityId }),
                ActivationBudget(evidenceItems: 1)),
            new GovernedLoopNodeCatalogDescriptor(
                GovernedLoopSequentialNodeDescriptors.ProviderInference,
                IsAdvertised: true,
                IsExecutable: true,
                IsLegalEntry: false,
                IsLegalTerminal: false,
                _success,
                _success,
                GovernedLoopJoinPolicy.None,
                MinimumIncomingControlEdges: 1,
                AllowsCycle: true,
                GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter,
                GovernedLoopTopologyNodeVocabulary.MaximumDurationMillisecondsParameter,
                Array.AsReadOnly(new[]
                {
                    Port("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data),
                    Port("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context),
                    Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
                }),
                Array.AsReadOnly(new[]
                {
                    TextParameter("instruction"),
                    CycleParameter(GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter, CustomLoopLimits.MaxGraphCycleIterations),
                    CycleParameter(GovernedLoopTopologyNodeVocabulary.MaximumDurationMillisecondsParameter, CustomLoopLimits.MaxGraphCycleMilliseconds),
                }.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray()),
                Array.AsReadOnly(new[] { ModelInferenceCapabilityId }),
                ActivationBudget(
                    CustomLoopLimits.MaxGraphSequentialEvidenceItemsPerActivation,
                    ProviderTransportResourceUnitsPerActivation)),
            new GovernedLoopNodeCatalogDescriptor(
                GovernedLoopSequentialNodeDescriptors.SuccessExit,
                IsAdvertised: true,
                IsExecutable: true,
                IsLegalEntry: false,
                IsLegalTerminal: true,
                Array.Empty<GovernedLoopControlCondition>(),
                Array.Empty<GovernedLoopControlCondition>(),
                GovernedLoopJoinPolicy.None,
                MinimumIncomingControlEdges: 1,
                AllowsCycle: false,
                CycleIterationBudgetParameterId: null,
                CycleTimeBudgetMillisecondsParameterId: null,
                Array.AsReadOnly(new[]
                {
                    Port("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data),
                    Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
                }),
                Array.Empty<GovernedLoopCatalogParameterContract>(),
                Array.AsReadOnly(new[] { ConversationTurnCapabilityId }),
                ActivationBudget(CustomLoopLimits.MaxGraphSequentialEvidenceItemsPerActivation)),
        });

    private static GovernedLoopCatalogPortContract Port(
        string id,
        GovernedLoopPortDirection direction,
        GovernedLoopBindingKind bindingKind)
        => new(id, direction, bindingKind, _textKind, Required: true);

    private static GovernedLoopCatalogParameterContract TextParameter(string id)
        => new(
            id,
            GovernedLoopParameterValueKind.Text,
            Required: true,
            MinimumCharacters: 1,
            MaximumCharacters: CustomLoopLimits.MaxGraphParameterValueCharacters,
            MinimumInteger: null,
            MaximumInteger: null,
            Array.Empty<string>());

    private static GovernedLoopCatalogParameterContract CycleParameter(string id, long maximum)
        => new(
            id,
            GovernedLoopParameterValueKind.Integer,
            Required: false,
            MinimumCharacters: 1,
            MaximumCharacters: maximum.ToString(CultureInfo.InvariantCulture).Length,
            MinimumInteger: 1,
            MaximumInteger: maximum,
            Array.Empty<string>());

    private static GovernedLoopNodeResourceBudget ActivationBudget(int evidenceItems, int resourceUnits = 0)
        => new(
            Attempts: 1,
            PayloadCharacters: 0,
            EvidenceItems: evidenceItems,
            ResourceUnits: resourceUnits);

    private static string DescriptorKey(GovernedLoopNodeCatalogDescriptor descriptor)
        => $"{(int)descriptor.Descriptor.Kind:D3}:{descriptor.Descriptor.TypeId}:{descriptor.Descriptor.Version:D10}";
}
