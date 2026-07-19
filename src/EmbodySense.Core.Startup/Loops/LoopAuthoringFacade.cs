using EmbodySense.Core.Application.Loops.Authoring;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Startup.Loops.Execution;
using ApplicationContextInput = EmbodySense.Core.Common.Loops.Models.Custom.CustomLoopContextInputPolicy;
using ApplicationContextOutput = EmbodySense.Core.Common.Loops.Models.Custom.CustomLoopContextOutputPolicy;
using ApplicationContextPolicy = EmbodySense.Core.Common.Loops.Models.Custom.CustomLoopContextPolicy;
using ApplicationNodeContext = EmbodySense.Core.Common.Loops.Models.Custom.CustomLoopNodeContextPolicy;

namespace EmbodySense.Core.Startup.Loops;

public sealed class LoopAuthoringFacade
{
    private readonly CustomLoopAuthoringService _service;
    private readonly LoopDefinitionStore _systemDefinitionStore;
    private readonly string _actor;

    public LoopAuthoringFacade(string workingDirectory) : this(workingDirectory, WorkspaceActors.Web)
    {
    }

    public LoopAuthoringFacade(string workingDirectory, string actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var paths = new WorkspacePaths(workingDirectory);
        var store = new CustomLoopDefinitionStore(paths);
        _service = new CustomLoopAuthoringService(store, new AuditLog(paths), runStore: new CustomLoopRunStore(paths));
        _systemDefinitionStore = new LoopDefinitionStore(paths);
        _actor = actor;
    }

    internal LoopAuthoringFacade(CustomLoopAuthoringService service, LoopDefinitionStore systemDefinitionStore, string actor)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(systemDefinitionStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        _service = service;
        _systemDefinitionStore = systemDefinitionStore;
        _actor = actor;
    }

    public async Task<LoopAuthoringCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var systemDefinition = await GetSystemDefinitionAsync(cancellationToken);
        var definitions = await _service.ListAsync(systemDefinition.RoleId, cancellationToken);
        return new LoopAuthoringCatalog(
            systemDefinition.RoleId,
            MapSystemDefinition(systemDefinition),
            definitions.Select(Map).ToArray(),
            CreateLimits(),
            CreateToolCatalog(systemDefinition));
    }

    public async Task<LoopDefinitionSnapshot?> GetAsync(string loopId, CancellationToken cancellationToken = default)
    {
        var systemDefinition = await GetSystemDefinitionAsync(cancellationToken);
        var definition = await _service.GetAsync(loopId, systemDefinition.RoleId, cancellationToken);
        return definition is null ? null : Map(definition);
    }

    public async Task<LoopAuthoringResponse> CreateAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var systemDefinition = await GetSystemDefinitionAsync(cancellationToken);
        return Map(await _service.CreateAsync(systemDefinition.RoleId, operationId, _actor, cancellationToken));
    }

    public async Task<LoopAuthoringResponse> UpdateAsync(string loopId, int expectedDefinitionVersion, string operationId, LoopDefinitionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var systemDefinition = await GetSystemDefinitionAsync(cancellationToken);
        var applicationInput = new CustomLoopDefinitionInput(
            input.DisplayName,
            input.Description,
            Map(input.TriggerPolicy)!,
            input.InferenceSteps?.Select(step => step is null ? null! : new CustomLoopInferenceStepInput(step.Id, step.Name, step.Instruction, Map(step.ContextPolicy)!)).ToArray()!,
            input.ToolAssignments?.Select(Map).ToArray()!,
            Map(input.ExitPolicy)!);
        var currentRoleCeiling = CustomLoopToolAuthorityProvider.ResolveCurrentRoleCeiling(systemDefinition);
        var result = await _service.UpdateAsync(loopId, expectedDefinitionVersion, systemDefinition.RoleId, operationId, _actor, applicationInput, currentRoleCeiling, cancellationToken);
        return Map(result);
    }

    public async Task<LoopAuthoringResponse> DeleteAsync(string loopId, int expectedDefinitionVersion, string operationId, CancellationToken cancellationToken = default)
    {
        var systemDefinition = await GetSystemDefinitionAsync(cancellationToken);
        return Map(await _service.DeleteAsync(loopId, expectedDefinitionVersion, systemDefinition.RoleId, operationId, _actor, cancellationToken));
    }

    private async Task<LoopDefinition> GetSystemDefinitionAsync(CancellationToken cancellationToken)
    {
        return await _systemDefinitionStore.LoadAsync("default-conversation", cancellationToken) ?? LoopDefinition.CreateDefaultConversation();
    }

    private static LoopAuthoringLimits CreateLimits()
    {
        return new LoopAuthoringLimits(
            CustomLoopLimits.MaxDefinitionsPerWorkspace,
            CustomLoopLimits.MinInferenceSteps,
            CustomLoopLimits.MaxInferenceSteps,
            CustomLoopLimits.MaxAdditionalIterations,
            CustomLoopLimits.MaxModelAttemptsPerRun,
            CustomLoopLimits.MaxGovernedToolRequestsPerAttempt,
            CustomLoopLimits.MaxGovernedToolRequestsPerRun,
            CustomLoopLimits.MaxNameCharacters,
            CustomLoopLimits.MaxDescriptionCharacters,
            CustomLoopLimits.MaxInstructionCharacters,
            CustomLoopLimits.MaxPresetPromptCharacters,
            CustomLoopLimits.MaxInvokingConversationCharacters,
            CustomLoopLimits.MaxInvokingConversationEntries,
            CustomLoopLimits.MaxGovernedToolTargetCharacters,
            CustomLoopLimits.MaxGovernedToolArgumentCharacters,
            CustomLoopLimits.MaxToolGovernanceDetailCharacters,
            CustomLoopLimits.MaxCanonicalModelOutputCharacters,
            CustomLoopLimits.MaxCanonicalToolResultCharacters,
            CustomLoopLimits.MaxLifecycleControlEventsPerRun,
            CustomLoopLimits.MaxTraceEventsPerRun,
            CustomLoopLimits.MaxLifecycleControlDetailCharacters,
            CustomLoopLimits.MaxRunTraceUtf8Bytes,
            CustomLoopLimits.MaxRunExecutionMilliseconds);
    }

    private static LoopToolCatalog CreateToolCatalog(LoopDefinition systemDefinition)
    {
        var assignable = CustomLoopToolAuthorityProvider.ResolveCurrentRoleCeiling(systemDefinition).Select(Map).ToArray();
        return new LoopToolCatalog(
            assignable,
            LoopCustomToolAuthorityCeiling.WorkspaceReadOnly);
    }

    private static LoopAuthoringResponse Map(CustomLoopAuthoringResult result)
    {
        return new LoopAuthoringResponse(
            result.Status.ToString(),
            result.IsCommitted,
            result.Definition is null ? null : Map(result.Definition),
            result.ValidationErrors.Select(error => new LoopValidationError(error.Code, error.Field, error.Message)).ToArray(),
            result.Conflict is null ? null : new LoopDefinitionConflict(result.Conflict.LoopId, result.Conflict.ExpectedDefinitionVersion, result.Conflict.ActualDefinitionVersion, result.Conflict.CurrentContentHash, result.Conflict.CurrentUpdatedAtUtc),
            result.Detail);
    }

    internal static LoopDefinitionSnapshot Map(CustomLoopDefinition definition)
    {
        return new LoopDefinitionSnapshot(
            definition.SchemaVersion,
            definition.Id,
            definition.DefinitionVersion,
            definition.ContentHash,
            definition.CreatedAtUtc,
            definition.UpdatedAtUtc,
            definition.DisplayName,
            definition.Description,
            definition.RoleId,
            Map(definition.TriggerPolicy),
            new LoopContextDefaults(Map(definition.ContextDefaults.Inference), Map(definition.ContextDefaults.Exit)),
            definition.InferenceSteps.Select(step => new LoopInferenceStep(step.Id, step.Name, step.Instruction, Map(step.ContextPolicy))).ToArray(),
            definition.ToolAssignments.Select(Map).ToArray(),
            Map(definition.ExitPolicy),
            definition.LastMutationOperationId);
    }

    private static LoopDefinitionSnapshot MapSystemDefinition(LoopDefinition definition)
    {
        var defaults = CustomLoopContextDefaults.CreatePrototypeDefaults();
        var toolAssignments = Enum.GetValues<ToolCommand>()
            .Where(command => LoopCapabilityIds.AllowsWorkspaceCommand(definition.CapabilityIds, command))
            .Select(Map)
            .ToArray();
        return new LoopDefinitionSnapshot(
            definition.SchemaVersion,
            definition.Id,
            1,
            string.Empty,
            DateTimeOffset.MinValue,
            DateTimeOffset.MinValue,
            definition.DisplayName,
            definition.Description,
            definition.RoleId,
            new LoopTriggerPolicy(LoopTriggerPromptSource.Invocation, string.Empty, true),
            new LoopContextDefaults(Map(defaults.Inference), Map(defaults.Exit)),
            [new LoopInferenceStep(DefaultConversationLoopGraphIds.DispatchInference, "Respond in role", "System-managed default conversation behavior.", new LoopNodeContextPolicy(LoopContextPolicyMode.Custom, new LoopContextPolicy(Map(defaults.Inference.ContextIn), new LoopContextOutputPolicy(true, true))))],
            toolAssignments,
            new LoopExitPolicy(0, string.Empty, new LoopNodeContextPolicy(LoopContextPolicyMode.Inherit, null)),
            string.Empty);
    }

    private static CustomLoopTriggerPolicy? Map(LoopTriggerPolicy? trigger) => trigger is null ? null : new((CustomLoopTriggerPromptSource)(int)trigger.PromptSource, trigger.PresetPrompt, trigger.IncludeInvokingConversation);

    private static LoopTriggerPolicy Map(CustomLoopTriggerPolicy trigger) => new((LoopTriggerPromptSource)(int)trigger.PromptSource, trigger.PresetPrompt, trigger.IncludeInvokingConversation);

    private static CustomLoopToolAssignment Map(LoopToolAssignment assignment) => (CustomLoopToolAssignment)(int)assignment;

    private static LoopToolAssignment Map(CustomLoopToolAssignment assignment) => (LoopToolAssignment)(int)assignment;

    private static LoopToolAssignment Map(ToolCommand command) => command switch
    {
        ToolCommand.List => LoopToolAssignment.List,
        ToolCommand.Read => LoopToolAssignment.Read,
        ToolCommand.Search => LoopToolAssignment.Search,
        ToolCommand.Append => LoopToolAssignment.Append,
        ToolCommand.Write => LoopToolAssignment.Write,
        ToolCommand.Delete => LoopToolAssignment.Delete,
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported governed workspace command.")
    };

    private static CustomLoopExitPolicy? Map(LoopExitPolicy? exit) => exit is null ? null : new(exit.MaxAdditionalIterations, exit.DecisionInstruction, Map(exit.ContextPolicy)!);

    private static LoopExitPolicy Map(CustomLoopExitPolicy exit) => new(exit.MaxAdditionalIterations, exit.DecisionInstruction, Map(exit.ContextPolicy));

    private static ApplicationNodeContext? Map(LoopNodeContextPolicy? policy) => policy is null ? null : new((CustomLoopContextPolicyMode)(int)policy.Mode, policy.CustomPolicy is null ? null : Map(policy.CustomPolicy));

    private static LoopNodeContextPolicy Map(ApplicationNodeContext policy) => new((LoopContextPolicyMode)(int)policy.Mode, policy.CustomPolicy is null ? null : Map(policy.CustomPolicy));

    private static ApplicationContextPolicy? Map(LoopContextPolicy? policy) => policy is null ? null : new(Map(policy.ContextIn)!, Map(policy.ContextOut)!);

    private static LoopContextPolicy Map(ApplicationContextPolicy policy) => new(Map(policy.ContextIn), Map(policy.ContextOut));

    private static ApplicationContextInput? Map(LoopContextInputPolicy? input) => input is null ? null : new(input.IncludeRoleContext, input.IncludeTriggerPrompt, input.IncludeInvokingConversation, input.IncludeEarlierRetainedOutputs, input.IncludePreviousIterationResult);

    private static LoopContextInputPolicy Map(ApplicationContextInput input) => new(input.IncludeRoleContext, input.IncludeTriggerPrompt, input.IncludeInvokingConversation, input.IncludeEarlierRetainedOutputs, input.IncludePreviousIterationResult);

    private static ApplicationContextOutput? Map(LoopContextOutputPolicy? output) => output is null ? null : new(output.RetainForLoopReasoning, output.PublishToInvokingConversation);

    private static LoopContextOutputPolicy Map(ApplicationContextOutput output) => new(output.RetainForLoopReasoning, output.PublishToInvokingConversation);
}
