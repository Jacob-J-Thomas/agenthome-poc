using System.Globalization;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;

namespace EmbodySense.Core.Startup.Loops;

/// <summary>Composes the exact schema-1 executable catalog from Application-owned contracts.</summary>
internal sealed class BuiltInGovernedLoopNodeCatalog : IGovernedLoopNodeCatalog
{
    private const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    private const string ModelInferenceCapabilityId = "org.embodysense/model-inference";
    private const string ScheduleTriggerCapabilityId = "org.embodysense/triggers/time";
    private const string WorkspaceCommandCapabilityId = "org.embodysense/workspace-command";
    private const int ProviderTransportResourceUnitsPerActivation = 1;
    private const string SourceEvidenceId = "built-in-governed-loop-node-catalog-schema-1-v1";
    private static readonly IReadOnlyList<GovernedLoopControlCondition> _always =
        Array.AsReadOnly(new[] { GovernedLoopControlCondition.Always });
    private static readonly IReadOnlyList<GovernedLoopControlCondition> _success =
        Array.AsReadOnly(new[] { GovernedLoopControlCondition.Success });
    private static readonly IReadOnlyList<GovernedLoopControlCondition> _successFailure =
        Array.AsReadOnly(new[] { GovernedLoopControlCondition.Success, GovernedLoopControlCondition.Failure });
    private static readonly GovernedLoopValueKindSet _textKind =
        GovernedLoopValueKindSet.Create([GovernedLoopValueKind.Text]);
    private readonly GovernedLoopNodeCatalogSnapshot _snapshot;

    internal BuiltInGovernedLoopNodeCatalog() : this(Array.Empty<CommandActionRegistration>(), null)
    {
    }

    internal BuiltInGovernedLoopNodeCatalog(
        IEnumerable<CommandActionRegistration> commandActions,
        Func<CommandActionRegistration, bool>? isCommandActionExecutable = null)
    {
        ArgumentNullException.ThrowIfNull(commandActions);
        var registrations = commandActions.Take(257).ToArray();
        if (registrations.Length > 256)
        {
            throw new ArgumentException("The finite command Action catalog is too large.", nameof(commandActions));
        }
        _snapshot = CreateSnapshot(registrations, isCommandActionExecutable);
    }

    /// <inheritdoc />
    public Task<GovernedLoopNodeCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_snapshot);
    }

    private static GovernedLoopNodeCatalogSnapshot CreateSnapshot(
        IReadOnlyList<CommandActionRegistration> commandActions,
        Func<CommandActionRegistration, bool>? isCommandActionExecutable)
    {
        var graphCompatibleCommandActions = commandActions
            .Select(registration => new
            {
                Registration = registration,
                IsCompatible = CommandActionGraphProjectionContract.TryGetPayloadCharacters(registration, out var payloadCharacters),
                PayloadCharacters = payloadCharacters,
            })
            .Where(candidate => candidate.IsCompatible)
            .ToArray();
        var descriptors = BaselineDescriptors()
            .Concat(GovernedLoopPureNodeCatalogContract.Descriptors)
            .Concat(GovernedLoopTopologyNodeCatalogContract.Descriptors)
            .Concat(GovernedLoopWaitNodeCatalogContract.Descriptors)
            .Append(GovernedLoopHumanInputNodeCatalogContract.Descriptor)
            .Concat(GovernedLoopFailNodeCatalogContract.Descriptors)
            .Concat(graphCompatibleCommandActions.Select(candidate => CommandAction(candidate.Registration, candidate.PayloadCharacters, isCommandActionExecutable?.Invoke(candidate.Registration) == true)))
            .OrderBy(DescriptorKey, StringComparer.Ordinal)
            .ToArray();
        if (descriptors.Length != 26 + graphCompatibleCommandActions.Length
            || descriptors.Select(item => item.Descriptor).Distinct().Count() != descriptors.Length
            || descriptors.Any(item => !GovernedLoopSequentialNodeDescriptors.IsSupported(item.Descriptor)
                && !GovernedLoopHumanInputNodeCatalogContract.TryResolve(item.Descriptor, out _)))
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
                _successFailure,
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
            WorkspaceAction(WorkspaceActionNodeDescriptors.Append),
            WorkspaceAction(WorkspaceActionNodeDescriptors.Write),
            WorkspaceAction(WorkspaceActionNodeDescriptors.Delete),
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

    private static GovernedLoopNodeCatalogDescriptor WorkspaceAction(GovernedLoopNodeDescriptor descriptor)
        => new(
            descriptor,
            IsAdvertised: true,
            IsExecutable: true,
            IsLegalEntry: false,
            IsLegalTerminal: false,
            _successFailure,
            _success,
            GovernedLoopJoinPolicy.None,
            MinimumIncomingControlEdges: 1,
            AllowsCycle: false,
            CycleIterationBudgetParameterId: null,
            CycleTimeBudgetMillisecondsParameterId: null,
            Array.AsReadOnly(new[]
            {
                Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
            }),
            Array.AsReadOnly(new[]
            {
                new GovernedLoopCatalogParameterContract(
                    "input",
                    GovernedLoopParameterValueKind.Text,
                    Required: true,
                    MinimumCharacters: 1,
                    MaximumCharacters: CustomLoopLimits.MaxGraphParameterValueCharacters,
                    MinimumInteger: null,
                    MaximumInteger: null,
                    Array.Empty<string>()),
            }),
            Array.AsReadOnly(new[] { WorkspaceCommandCapabilityId }),
            new GovernedLoopNodeResourceBudget(
                Attempts: 1,
                PayloadCharacters: CustomLoopLimits.MaxGraphParameterValueCharacters,
                EvidenceItems: CustomLoopLimits.MaxGraphSequentialEvidenceItemsPerActivation,
                ResourceUnits: 1));

    private static GovernedLoopNodeCatalogDescriptor CommandAction(CommandActionRegistration registration, int payloadCharacters, bool isolationAvailable)
    {
        if (CommandActionRegistrationContract.Validate(registration) is { } reasonCode)
        {
            throw new ArgumentException(reasonCode, nameof(registration));
        }
        var template = registration.Template;
        return new GovernedLoopNodeCatalogDescriptor(
            CommandActionNodeDescriptors.For(template),
            IsAdvertised: true,
            IsExecutable: isolationAvailable && !template.RequiresCredentialChannel,
            IsLegalEntry: false,
            IsLegalTerminal: false,
            _successFailure,
            _success,
            GovernedLoopJoinPolicy.None,
            MinimumIncomingControlEdges: 1,
            AllowsCycle: false,
            CycleIterationBudgetParameterId: null,
            CycleTimeBudgetMillisecondsParameterId: null,
            Array.AsReadOnly(new[]
            {
                Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
            }),
            Array.AsReadOnly(template.Slots.Select(Parameter).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(new[] { template.Capability.Id.Value }),
            new GovernedLoopNodeResourceBudget(
                Attempts: 1,
                PayloadCharacters: payloadCharacters,
                EvidenceItems: CustomLoopLimits.MaxGraphSequentialEvidenceItemsPerActivation,
                ResourceUnits: 1));
    }

    private static GovernedLoopCatalogParameterContract Parameter(CommandActionSlotDefinition slot)
        => new(
            slot.Name,
            slot.Kind switch
            {
                CommandActionSlotKind.Identifier => GovernedLoopParameterValueKind.CapabilityPath,
                CommandActionSlotKind.Integer => GovernedLoopParameterValueKind.Integer,
                CommandActionSlotKind.Enumeration => GovernedLoopParameterValueKind.Enumeration,
                CommandActionSlotKind.WorkspaceRelativeTarget => GovernedLoopParameterValueKind.WorkspaceRelativeTarget,
                CommandActionSlotKind.BoundedJson => GovernedLoopParameterValueKind.Json,
                _ => GovernedLoopParameterValueKind.Text,
            },
            Required: true,
            MinimumCharacters: slot.Kind == CommandActionSlotKind.BoundedText ? 0 : 1,
            MaximumCharacters: Math.Min(slot.MaxUtf8Bytes, CustomLoopLimits.MaxGraphParameterValueCharacters),
            slot.MinimumInteger,
            slot.MaximumInteger,
            Array.AsReadOnly(slot.EnumerationValues.ToArray()),
            slot.MaxUtf8Bytes,
            slot.AllowLeadingOption,
            AllowResponseFileReference: false);

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
