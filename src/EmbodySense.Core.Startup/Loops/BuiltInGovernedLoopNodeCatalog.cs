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
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Startup.Capabilities;

namespace EmbodySense.Core.Startup.Loops;

/// <summary>Composes the exact schema-1 executable catalog from Application-owned contracts and current capability evidence.</summary>
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
    private readonly GovernedLoopNodeCatalogSnapshot _initialSnapshot;
    private readonly IReadOnlyList<CommandActionRegistration> _commandActions;
    private readonly ICapabilityCatalogStore? _capabilityCatalog;
    private readonly ICommandActionNativeHost? _commandActionNativeHost;

    internal BuiltInGovernedLoopNodeCatalog() : this(Array.Empty<CommandActionRegistration>(), null)
    {
    }

    internal BuiltInGovernedLoopNodeCatalog(
        IEnumerable<CommandActionRegistration> commandActions,
        Func<CommandActionRegistration, bool>? isCommandActionExecutable = null,
        ICapabilityCatalogStore? capabilityCatalog = null,
        ICommandActionNativeHost? commandActionNativeHost = null)
    {
        ArgumentNullException.ThrowIfNull(commandActions);
        var registrations = commandActions.Take(257).ToArray();
        if (registrations.Length > 256)
        {
            throw new ArgumentException("The finite command Action catalog is too large.", nameof(commandActions));
        }
        _commandActions = Array.AsReadOnly(registrations);
        _capabilityCatalog = capabilityCatalog;
        _commandActionNativeHost = commandActionNativeHost;
        _initialSnapshot = CreateSnapshot(registrations, isCommandActionExecutable);
    }

    /// <inheritdoc />
    public async Task<GovernedLoopNodeCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_capabilityCatalog is null)
        {
            return _initialSnapshot;
        }

        try
        {
            var entries = new List<CapabilityCatalogEntry>();
            string? cursor = null;
            long? catalogRevision = null;
            for (var pageNumber = 0; pageNumber < CapabilityCatalogLimits.MaximumEntries / CapabilityCatalogLimits.MaximumPageSize + 1; pageNumber++)
            {
                var read = await _capabilityCatalog.ReadAsync(cursor, CapabilityCatalogLimits.MaximumPageSize, cancellationToken).ConfigureAwait(false);
                if (read.Status != CapabilityCatalogReadStatus.Available || read.Page is null)
                {
                    return UnavailableSnapshot();
                }

                catalogRevision ??= read.Page.CatalogRevision;
                if (catalogRevision != read.Page.CatalogRevision)
                {
                    return UnavailableSnapshot();
                }

                entries.AddRange(read.Page.Entries);
                if (read.Page.NextCursor is null)
                {
                    cursor = null;
                    break;
                }

                if (string.Equals(cursor, read.Page.NextCursor, StringComparison.Ordinal))
                {
                    return UnavailableSnapshot();
                }

                cursor = read.Page.NextCursor;
            }

            if (cursor is not null || entries.Count > CapabilityCatalogLimits.MaximumEntries)
            {
                return UnavailableSnapshot();
            }

            var current = entries.ToDictionary(entry => entry.Descriptor.Id.Value, StringComparer.Ordinal);
            var executableCommandActions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var registration in _commandActions)
            {
                if (!HasCurrentExecutableCapabilities(registration.Template.Capability.Id.Value, current, registration.Template.Capability)
                    || _commandActionNativeHost is null)
                {
                    continue;
                }

                var availability = await _commandActionNativeHost.CheckExecutableAvailabilityAsync(registration, cancellationToken).ConfigureAwait(false);
                if (availability.Status == CapabilityExecutableAvailabilityStatus.Available)
                {
                    executableCommandActions.Add(registration.Template.ContentHash);
                }
            }

            return CreateSnapshot(
                _commandActions,
                registration => executableCommandActions.Contains(registration.Template.ContentHash),
                capabilityId => HasCurrentExecutableCapabilities(capabilityId, current));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return UnavailableSnapshot();
        }
    }

    private static GovernedLoopNodeCatalogSnapshot CreateSnapshot(
        IReadOnlyList<CommandActionRegistration> commandActions,
        Func<CommandActionRegistration, bool>? isCommandActionExecutable,
        Func<string, bool>? isCapabilityExecutable = null)
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
            .Select(descriptor => isCapabilityExecutable is null
                ? descriptor
                : descriptor with
                {
                    IsExecutable = descriptor.IsExecutable && descriptor.RequiredCapabilityIds.All(isCapabilityExecutable)
                })
            .OrderBy(DescriptorKey, StringComparer.Ordinal)
            .ToArray();
        if (descriptors.Length != 26 + graphCompatibleCommandActions.Length
            || descriptors.Select(item => item.Descriptor).Distinct().Count() != descriptors.Length
            || descriptors.Any(item => item.IsExecutable && !GovernedLoopSequentialNodeDescriptors.IsSupported(item.Descriptor)))
        {
            throw new InvalidOperationException("The built-in schema-1 governed-loop catalog is incomplete or advertises an executable descriptor that the runner does not support.");
        }

        return new GovernedLoopNodeCatalogSnapshot(
            IsAvailable: true,
            SourceEvidenceId,
            Array.AsReadOnly(descriptors));
    }

    private static bool HasCurrentExecutableCapabilities(
        string capabilityId,
        IReadOnlyDictionary<string, CapabilityCatalogEntry> current,
        CapabilityDescriptorIdentity? expectedIdentity = null)
        => current.TryGetValue(capabilityId, out var entry)
            && CapabilityDescriptorIdentity.TryCreate(entry.Descriptor, out var descriptorIdentity, out _)
            && HasExpectedDescriptorIdentity(capabilityId, descriptorIdentity!, expectedIdentity)
            && entry.Lifecycle.SchemaVersion == CapabilityLifecycleSnapshot.CurrentSchemaVersion
            && Equals(entry.Lifecycle.DescriptorIdentity, descriptorIdentity)
            && entry.Lifecycle.Declaration == CapabilityDeclarationState.Declared
            && entry.Lifecycle.Installation == CapabilityInstallationState.Installed
            && entry.Lifecycle.Enablement == CapabilityEnablementState.Enabled
            && entry.Lifecycle.Health == CapabilityHealthState.Healthy
            && entry.Lifecycle.Retirement is CapabilityRetirementState.Active or CapabilityRetirementState.Deprecated
            && entry.Lifecycle.Trust == CapabilityTrustState.Verified;

    private static bool HasExpectedDescriptorIdentity(
        string capabilityId,
        CapabilityDescriptorIdentity actualIdentity,
        CapabilityDescriptorIdentity? expectedIdentity)
    {
        if (expectedIdentity is not null)
        {
            return Equals(actualIdentity, expectedIdentity);
        }

        var builtIn = BuiltInCapabilityCatalog.Descriptors.FirstOrDefault(descriptor => descriptor.Id.Value == capabilityId);
        return builtIn is null
            || CapabilityDescriptorIdentity.TryCreate(builtIn, out var builtInIdentity, out _)
                && Equals(actualIdentity, builtInIdentity);
    }

    private static GovernedLoopNodeCatalogSnapshot UnavailableSnapshot()
        => new(false, SourceEvidenceId, Array.Empty<GovernedLoopNodeCatalogDescriptor>());

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
