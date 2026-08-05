using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Loops.Authoring.Models;
using EmbodySense.Core.Application.Loops.Authoring;
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
using ApplicationNodeContext = EmbodySense.Core.Common.Loops.Custom.CustomLoopNodeContextPolicy;

namespace EmbodySense.Core.Startup.Loops;

/// <summary>
/// Exposes custom-loop authoring through Core.Startup without leaking application or persistence types.
/// </summary>
/// <remarks>
/// The persisted default-conversation definition is the authority source for role identity and
/// assignable tools. An initialized workspace with a missing or substituted system definition fails
/// closed. Mutations are audited, operation-idempotent, and version-checked by the underlying authoring
/// service; validation and conflicts are returned as data.
/// </remarks>
public sealed class LoopAuthoringFacade
{
    private readonly CustomLoopAuthoringService _service;
    private readonly LoopDefinitionStore _systemDefinitionStore;
    private readonly WorkspacePaths? _paths;
    private readonly string _actor;

    /// <summary>
    /// Creates a Web-attributed authoring facade over the supplied workspace.
    /// </summary>
    /// <param name="workingDirectory">The workspace root, normalized to an absolute path.</param>
    public LoopAuthoringFacade(string workingDirectory) : this(workingDirectory, WorkspaceActors.Web)
    {
    }

    /// <summary>
    /// Creates an authoring facade over the supplied workspace and audit actor.
    /// </summary>
    /// <param name="workingDirectory">The workspace root, normalized to an absolute path.</param>
    /// <param name="actor">The nonblank actor attributed to authoring audit events.</param>
    public LoopAuthoringFacade(string workingDirectory, string actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var paths = new WorkspacePaths(workingDirectory);
        var store = new CustomLoopDefinitionStore(paths);
        _service = new CustomLoopAuthoringService(store, new AuditLog(paths), runStore: new CustomLoopRunStore(paths));
        _systemDefinitionStore = new LoopDefinitionStore(paths);
        _paths = paths;
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

    /// <summary>
    /// Reads the system definition, current role's custom definitions, effective limits, and assignable tools.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel persistence reads.</param>
    /// <returns>A task whose result is the complete authoring catalog for the system role.</returns>
    public async Task<LoopAuthoringCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var systemDefinition = await GetSystemDefinitionAsync(cancellationToken);
        var definitions = await _service.ListAsync(systemDefinition.RoleId, cancellationToken);
        return new LoopAuthoringCatalog(
            systemDefinition.RoleId,
            MapSystemDefinition(systemDefinition),
            definitions.Select(Map).ToArray(),
            CreateDraftTemplate(systemDefinition.RoleId),
            CreateLimits(),
            CreateToolCatalog(systemDefinition));
    }

    /// <summary>
    /// Reads one custom definition for the current system role.
    /// </summary>
    /// <param name="loopId">The custom loop identifier.</param>
    /// <param name="cancellationToken">The token used to cancel persistence reads.</param>
    /// <returns>A task whose result is the definition, or null when no definition is visible to the current role.</returns>
    public async Task<LoopDefinitionSnapshot?> GetAsync(string loopId, CancellationToken cancellationToken = default)
    {
        var systemDefinition = await GetSystemDefinitionAsync(cancellationToken);
        var definition = await _service.GetAsync(loopId, systemDefinition.RoleId, cancellationToken);
        return definition is null ? null : Map(definition);
    }

    /// <summary>
    /// Creates a new server-owned custom-loop seed for the current role.
    /// </summary>
    /// <param name="operationId">The idempotency identity to reuse when the caller cannot determine whether a prior response committed.</param>
    /// <param name="cancellationToken">The token used to cancel the authoring operation.</param>
    /// <returns>A task whose result reports commit status, the created definition, validation, conflict, and audit detail.</returns>
    public async Task<LoopAuthoringResponse> CreateAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var systemDefinition = await GetSystemDefinitionAsync(cancellationToken);
        return Map(await _service.CreateAsync(systemDefinition.RoleId, operationId, _actor, cancellationToken));
    }

    /// <summary>
    /// Validates and atomically creates the first durable version of a client-authored loop draft.
    /// </summary>
    /// <param name="operationId">The idempotency identity reused until an uncertain first-save outcome is resolved.</param>
    /// <param name="input">The complete editable definition captured at the explicit first-save boundary.</param>
    /// <param name="cancellationToken">The token used to cancel validation, persistence, and auditing.</param>
    /// <returns>A task whose result reports commit, replay, validation, authority, quota, conflict, and audit-integrity status.</returns>
    public async Task<LoopAuthoringResponse> CreateAsync(string operationId, LoopDefinitionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var systemDefinition = await GetSystemDefinitionAsync(cancellationToken);
        var currentRoleCeiling = CustomLoopToolAuthorityProvider.ResolveCurrentRoleCeiling(systemDefinition);
        var result = await _service.CreateAsync(systemDefinition.RoleId, operationId, _actor, MapDefinitionInput(input), currentRoleCeiling, cancellationToken);
        return Map(result);
    }

    /// <summary>
    /// Validates and conditionally replaces a custom definition for the current role.
    /// </summary>
    /// <param name="loopId">The custom loop identifier.</param>
    /// <param name="expectedDefinitionVersion">The version required for optimistic concurrency.</param>
    /// <param name="operationId">The idempotency identity for this exact update request.</param>
    /// <param name="input">The interface-owned replacement definition shape.</param>
    /// <param name="cancellationToken">The token used to cancel validation, persistence, and auditing.</param>
    /// <returns>A task whose result distinguishes commits, validation rejections, version conflicts, and audit warnings.</returns>
    public async Task<LoopAuthoringResponse> UpdateAsync(string loopId, int expectedDefinitionVersion, string operationId, LoopDefinitionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var systemDefinition = await GetSystemDefinitionAsync(cancellationToken);
        var currentRoleCeiling = CustomLoopToolAuthorityProvider.ResolveCurrentRoleCeiling(systemDefinition);
        var result = await _service.UpdateAsync(loopId, expectedDefinitionVersion, systemDefinition.RoleId, operationId, _actor, MapDefinitionInput(input), currentRoleCeiling, cancellationToken);
        return Map(result);
    }

    /// <summary>
    /// Conditionally deletes a custom definition while retaining its historical run evidence.
    /// </summary>
    /// <param name="loopId">The custom loop identifier.</param>
    /// <param name="expectedDefinitionVersion">The version required for optimistic concurrency.</param>
    /// <param name="operationId">The idempotency identity for this exact deletion request.</param>
    /// <param name="cancellationToken">The token used to cancel persistence and auditing.</param>
    /// <returns>A task whose result distinguishes deletion, replay, conflict, rejection, and audit-warning outcomes.</returns>
    public async Task<LoopAuthoringResponse> DeleteAsync(string loopId, int expectedDefinitionVersion, string operationId, CancellationToken cancellationToken = default)
    {
        var systemDefinition = await GetSystemDefinitionAsync(cancellationToken);
        return Map(await _service.DeleteAsync(loopId, expectedDefinitionVersion, systemDefinition.RoleId, operationId, _actor, cancellationToken));
    }

    private async Task<LoopDefinition> GetSystemDefinitionAsync(CancellationToken cancellationToken)
    {
        var definition = await _systemDefinitionStore.LoadAsync(BuiltInLoopIds.DefaultConversation, cancellationToken);
        if (definition is not null)
        {
            if (!string.Equals(definition.Id, BuiltInLoopIds.DefaultConversation, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The persisted default conversation authority definition has a substituted identity; loop authoring failed closed.");
            }

            return definition;
        }

        if (_paths?.IsInitialized == true)
        {
            throw new InvalidOperationException("The initialized workspace is missing its default conversation authority definition; loop authoring failed closed.");
        }

        return LoopDefinition.CreateDefaultConversation();
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

    private static LoopDefinitionDraftTemplate CreateDraftTemplate(string roleId)
    {
        var seed = CustomLoopDefinition.CreateSeed("draft-template", roleId, "draft-template-step", "draft-template-operation", DateTimeOffset.UnixEpoch);
        var definition = new LoopDefinitionInput(
            seed.DisplayName,
            seed.Description,
            Map(seed.TriggerPolicy),
            seed.InferenceSteps.Select(step => new LoopInferenceStep(null, step.Name, step.Instruction, Map(step.ContextPolicy))).ToArray(),
            seed.ToolAssignments.Select(Map).ToArray(),
            Map(seed.ExitPolicy));
        var contextDefaults = new LoopContextDefaults(Map(seed.ContextDefaults.Inference), Map(seed.ContextDefaults.Exit));
        return new LoopDefinitionDraftTemplate(seed.SchemaVersion, seed.RoleId, definition, contextDefaults);
    }

    private static CustomLoopDefinitionInput MapDefinitionInput(LoopDefinitionInput input)
    {
        return new CustomLoopDefinitionInput(
            input.DisplayName,
            input.Description,
            Map(input.TriggerPolicy)!,
            input.InferenceSteps?.Select(step => step is null ? null! : new CustomLoopInferenceStepInput(step.Id, step.Name, step.Instruction, Map(step.ContextPolicy)!)).ToArray()!,
            input.ToolAssignments?.Select(Map).ToArray()!,
            Map(input.ExitPolicy)!);
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

    private static SystemLoopDefinitionSnapshot MapSystemDefinition(LoopDefinition definition)
    {
        var graph = definition.Graph;
        var executionBlocker = DefaultConversationLoopGraphContract.GetExecutionBlocker(definition);
        var executionSemantics = executionBlocker is null ? SystemLoopExecutionSemantics.AuthorityTopologyOnly : SystemLoopExecutionSemantics.Unknown;
        var executionDetail = executionBlocker is null
            ? "The dedicated runner accepts this system-owned graph as its authority topology, but it does not certify the nodes and edges as an exact execution-order contract. The hard-coded transaction assembles context before durable user acceptance, publishes the user message before provider inference, then observes and publishes the assistant message; nodes are not dispatched independently by a generic graph executor."
            : $"The dedicated runner rejects this persisted graph contract: {executionBlocker}";
        return new SystemLoopDefinitionSnapshot(
            definition.SchemaVersion,
            definition.Id,
            definition.DisplayName,
            definition.Description,
            definition.RoleId,
            definition.Trigger,
            definition.MemoryScope,
            definition.CapabilityIds.ToArray(),
            definition.ReviewPolicy,
            definition.FailurePolicy,
            definition.State,
            definition.EditMode,
            new SystemLoopGraphSnapshot(
                graph.EntryNodeId,
                graph.TerminalNodeIds.ToArray(),
                graph.Nodes.Select(node => new SystemLoopGraphNodeSnapshot(node.Id, node.DisplayName, node.Description, node.Kind, node.EditMode, node.CapabilityIds.ToArray(), executionSemantics)).ToArray(),
                graph.Edges.Select(edge => new SystemLoopGraphEdgeSnapshot(edge.Id, edge.FromNodeId, edge.ToNodeId, edge.Condition, edge.Description, executionSemantics)).ToArray()),
            new SystemLoopExecutionContractSnapshot(
                nameof(DefaultConversationLoopRunner),
                executionSemantics,
                false,
                executionDetail));
    }

    private static CustomLoopTriggerPolicy? Map(LoopTriggerPolicy? trigger) => trigger is null ? null : new((CustomLoopTriggerPromptSource)(int)trigger.PromptSource, trigger.PresetPrompt, trigger.IncludeInvokingConversation);

    private static LoopTriggerPolicy Map(CustomLoopTriggerPolicy trigger) => new((LoopTriggerPromptSource)(int)trigger.PromptSource, trigger.PresetPrompt, trigger.IncludeInvokingConversation);

    private static CustomLoopToolAssignment Map(LoopToolAssignment assignment) => (CustomLoopToolAssignment)(int)assignment;

    private static LoopToolAssignment Map(CustomLoopToolAssignment assignment) => (LoopToolAssignment)(int)assignment;

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
