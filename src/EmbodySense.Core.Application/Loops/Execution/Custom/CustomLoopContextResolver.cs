using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed class CustomLoopContextResolver
{
    public CustomLoopContextAssembly ResolveInference(CustomLoopRunRecord run, CustomLoopInferenceStep step)
    {
        return ResolveInference(run, step, run?.AdmittedDefinition?.ToolAssignments ?? []);
    }

    public CustomLoopContextAssembly ResolveInference(CustomLoopRunRecord run, CustomLoopInferenceStep step, IReadOnlyList<CustomLoopToolAssignment> effectiveToolAssignments)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(effectiveToolAssignments);

        var policy = ResolvePolicy(step.ContextPolicy, run.AdmittedDefinition.ContextDefaults.Inference);
        return Resolve(run, step.Id, step.Instruction, policy, isExit: false, effectiveToolAssignments);
    }

    public CustomLoopContextAssembly ResolveExit(CustomLoopRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var exit = run.AdmittedDefinition.ExitPolicy;
        var policy = ResolvePolicy(exit.ContextPolicy, run.AdmittedDefinition.ContextDefaults.Exit);
        var instruction = $"{exit.DecisionInstruction}{Environment.NewLine}{Environment.NewLine}Return exactly one ASCII token: Complete or Repeat. Do not add punctuation, JSON, Markdown, or explanation.";
        return Resolve(run, "exit", instruction, policy, isExit: true, []);
    }

    public static CustomLoopContextPolicy ResolvePolicy(CustomLoopNodeContextPolicy configured, CustomLoopContextPolicy defaults)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(defaults);

        var resolved = configured.Mode switch
        {
            CustomLoopContextPolicyMode.Inherit when configured.CustomPolicy is null => defaults,
            CustomLoopContextPolicyMode.Custom when configured.CustomPolicy is not null => configured.CustomPolicy,
            _ => throw new InvalidOperationException("Custom loop node context policy is not valid for execution.")
        };

        if (resolved.ContextIn is null || resolved.ContextOut is null)
        {
            throw new InvalidOperationException("Resolved custom loop context policy is incomplete and cannot be executed.");
        }

        return resolved;
    }

    private static CustomLoopContextAssembly Resolve(CustomLoopRunRecord run, string stepId, string instruction, CustomLoopContextPolicy policy, bool isExit, IReadOnlyList<CustomLoopToolAssignment> effectiveToolAssignments)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            throw new InvalidOperationException("Custom loop node instruction is required for execution.");
        }

        if (!CustomLoopContextSnapshotHash.Matches(run.ContextSnapshot))
        {
            throw new InvalidOperationException("The admitted custom-loop context manifest failed its integrity check.");
        }

        var messages = new List<LlmMessage>();
        var blocks = new List<CustomLoopContextBlock>();
        var trustedInstructions = new List<EmbodySenseTrustedInstruction>();
        var governance = EmbodySenseDeveloperInstructions.Capture(isExit ? [] : MapToolAssignments(effectiveToolAssignments));
        AddIncluded(blocks, CustomLoopContextSource.HarnessGovernance, "harness-governance", LlmMessageRole.System, governance.Content, sourceVersion: governance.Version);

        AddWorkspaceContext(run, policy, messages, blocks, trustedInstructions);

        var assignedTools = effectiveToolAssignments.Count == 0
            ? "none"
            : string.Join(", ", effectiveToolAssignments.Select(value => value.ToString().ToLowerInvariant()));
        var metadata = $"Loop: {run.LoopId}{Environment.NewLine}Run: {run.Id}{Environment.NewLine}Role: {run.AdmittedDefinition.RoleId}{Environment.NewLine}Iteration: {run.Checkpoint.Iteration}{Environment.NewLine}Node: {stepId}{Environment.NewLine}Tools: {(isExit ? "none (Exit is tool-less)" : assignedTools)}";
        AddIncluded(messages, blocks, CustomLoopContextSource.RunMetadata, "run-metadata", LlmMessageRole.User, metadata);
        trustedInstructions.Add(new EmbodySenseTrustedInstruction(stepId, instruction));
        AddIncluded(blocks, CustomLoopContextSource.NodeInstruction, stepId, LlmMessageRole.System, instruction);

        AddTriggerPrompt(run, policy, messages, blocks);
        AddConversation(run, policy, messages, blocks);
        AddEarlierOutputs(run, policy, messages, blocks);
        AddPreviousIteration(run, policy, messages, blocks);

        messages.Add(LlmMessage.User(isExit
            ? "Apply the trusted Exit instruction to the admitted lower-authority context and return the required decision token."
            : "Execute the trusted custom-loop node instruction using the admitted lower-authority context. If no trigger prompt was admitted, proceed from the trusted instruction alone."));
        var instructionContext = new LlmInferenceInstructionContext(governance, trustedInstructions, preserveExactLogicalContext: true);
        return new CustomLoopContextAssembly(new LlmInferenceRequest(messages, instructionContext: instructionContext), blocks.ToArray(), policy.ContextOut);
    }

    private static void AddWorkspaceContext(
        CustomLoopRunRecord run,
        CustomLoopContextPolicy policy,
        List<LlmMessage> messages,
        List<CustomLoopContextBlock> blocks,
        List<EmbodySenseTrustedInstruction> trustedInstructions)
    {
        var sources = run.ContextSnapshot.SourceManifest
            .Where(source => source.SourceType is CustomLoopContextSource.RoleInstruction or CustomLoopContextSource.AgentIdentity or CustomLoopContextSource.ContextualState)
            .OrderBy(source => source.Order)
            .ToArray();
        if (!policy.ContextIn.IncludeRoleContext)
        {
            foreach (var source in sources)
            {
                AddOmitted(blocks, source.SourceType, source.SourceId, source.Role, "The resolved node context policy excluded directory-role/startup product context.");
            }

            return;
        }

        foreach (var source in sources)
        {
            if (!source.Included)
            {
                AddOmitted(blocks, source.SourceType, source.SourceId, source.Role, source.OmissionReason ?? "The admitted context source was unavailable.");
                continue;
            }

            if (source.SourceType is CustomLoopContextSource.RoleInstruction or CustomLoopContextSource.AgentIdentity)
            {
                trustedInstructions.Add(new EmbodySenseTrustedInstruction(source.SourceId, source.Content));
                AddIncluded(blocks, source.SourceType, source.SourceId, LlmMessageRole.System, source.Content, source.OriginalCharacterCount, source.Truncated);
            }
            else
            {
                AddIncluded(messages, blocks, source.SourceType, source.SourceId, LlmMessageRole.User, source.Content, source.OriginalCharacterCount, source.Truncated);
            }
        }
    }

    private static void AddTriggerPrompt(CustomLoopRunRecord run, CustomLoopContextPolicy policy, List<LlmMessage> messages, List<CustomLoopContextBlock> blocks)
    {
        if (!policy.ContextIn.IncludeTriggerPrompt)
        {
            AddOmitted(blocks, CustomLoopContextSource.TriggerPrompt, "trigger-prompt", LlmMessageRole.User, "The resolved node context policy excluded the admitted trigger prompt.");
            return;
        }

        if (string.IsNullOrWhiteSpace(run.TriggerPrompt))
        {
            AddOmitted(blocks, CustomLoopContextSource.TriggerPrompt, "trigger-prompt", LlmMessageRole.User, "Trigger admitted no prompt.");
            return;
        }

        AddIncluded(messages, blocks, CustomLoopContextSource.TriggerPrompt, "trigger-prompt", LlmMessageRole.User, $"[EmbodySense untrusted trigger prompt data]{Environment.NewLine}{run.TriggerPrompt}");
    }

    private static void AddConversation(CustomLoopRunRecord run, CustomLoopContextPolicy policy, List<LlmMessage> messages, List<CustomLoopContextBlock> blocks)
    {
        if (!run.AdmittedDefinition.TriggerPolicy.IncludeInvokingConversation)
        {
            AddOmitted(blocks, CustomLoopContextSource.InvokingConversation, "invoking-conversation", LlmMessageRole.User, "Trigger did not admit invoking-conversation history.");
            return;
        }

        if (!policy.ContextIn.IncludeInvokingConversation)
        {
            AddOmitted(blocks, CustomLoopContextSource.InvokingConversation, "invoking-conversation", LlmMessageRole.User, "The resolved node context policy excluded invoking-conversation history.");
            return;
        }

        if (run.InvokingConversation is null)
        {
            AddOmitted(blocks, CustomLoopContextSource.InvokingConversation, "invoking-conversation", LlmMessageRole.User, "No invoking conversation was bound at admission.");
            return;
        }

        var sources = run.ContextSnapshot.SourceManifest
            .Where(source => source.SourceType == CustomLoopContextSource.InvokingConversation && source.Included)
            .OrderBy(source => source.Order)
            .ToArray();
        if (sources.Length == 0)
        {
            AddOmitted(blocks, CustomLoopContextSource.InvokingConversation, "invoking-conversation", LlmMessageRole.User, "No invoking conversation existed at admission or all messages were omitted by the admitted bound.");
            return;
        }

        foreach (var source in sources)
        {
            AddIncluded(messages, blocks, source.SourceType, source.SourceId, LlmMessageRole.User, source.Content, source.OriginalCharacterCount, source.Truncated);
        }
    }

    private static void AddEarlierOutputs(CustomLoopRunRecord run, CustomLoopContextPolicy policy, List<LlmMessage> messages, List<CustomLoopContextBlock> blocks)
    {
        if (!policy.ContextIn.IncludeEarlierRetainedOutputs)
        {
            AddOmitted(blocks, CustomLoopContextSource.EarlierRetainedOutput, "earlier-retained-outputs", LlmMessageRole.User, "The resolved node context policy excluded earlier retained outputs.");
            return;
        }

        if (run.Checkpoint.EarlierRetainedOutputs.Length == 0)
        {
            AddOmitted(blocks, CustomLoopContextSource.EarlierRetainedOutput, "earlier-retained-outputs", LlmMessageRole.User, "No earlier output was retained for this iteration.");
            return;
        }

        foreach (var output in run.Checkpoint.EarlierRetainedOutputs)
        {
            AddIncluded(messages, blocks, CustomLoopContextSource.EarlierRetainedOutput, $"iteration-{output.Iteration}-{output.StepId}", LlmMessageRole.User, $"[EmbodySense untrusted retained output from {output.StepId}]{Environment.NewLine}{output.Content}");
        }
    }

    private static void AddPreviousIteration(CustomLoopRunRecord run, CustomLoopContextPolicy policy, List<LlmMessage> messages, List<CustomLoopContextBlock> blocks)
    {
        if (!policy.ContextIn.IncludePreviousIterationResult)
        {
            AddOmitted(blocks, CustomLoopContextSource.PreviousIterationResult, "previous-iteration-result", LlmMessageRole.User, "The resolved node context policy excluded the previous iteration result.");
            return;
        }

        var output = run.Checkpoint.PreviousIterationResult;
        if (output is null)
        {
            AddOmitted(blocks, CustomLoopContextSource.PreviousIterationResult, "previous-iteration-result", LlmMessageRole.User, "No previous iteration result was retained at the repeat boundary.");
            return;
        }

        AddIncluded(messages, blocks, CustomLoopContextSource.PreviousIterationResult, $"iteration-{output.Iteration}-result", LlmMessageRole.User, $"[EmbodySense untrusted previous iteration result]{Environment.NewLine}{output.Content}");
    }

    private static void AddIncluded(
        List<LlmMessage> messages,
        List<CustomLoopContextBlock> blocks,
        CustomLoopContextSource source,
        string sourceId,
        LlmMessageRole role,
        string content,
        int? originalCharacterCount = null,
        bool truncated = false)
    {
        messages.Add(new LlmMessage(role, content));
        AddIncluded(blocks, source, sourceId, role, content, originalCharacterCount, truncated);
    }

    private static void AddIncluded(
        List<CustomLoopContextBlock> blocks,
        CustomLoopContextSource source,
        string sourceId,
        LlmMessageRole role,
        string content,
        int? originalCharacterCount = null,
        bool truncated = false,
        string? sourceVersion = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            AddOmitted(blocks, source, sourceId, role, "The selected source was empty.");
            return;
        }

        blocks.Add(new CustomLoopContextBlock(source, sourceId, role, true, null, content, CustomLoopTraceContentHash.Compute(content), originalCharacterCount ?? content.Length, truncated, sourceVersion));
    }

    private static void AddOmitted(List<CustomLoopContextBlock> blocks, CustomLoopContextSource source, string sourceId, LlmMessageRole role, string reason)
    {
        blocks.Add(new CustomLoopContextBlock(source, sourceId, role, false, reason, string.Empty, CustomLoopTraceContentHash.Compute(string.Empty), 0, false));
    }

    private static ToolCommand[] MapToolAssignments(IEnumerable<CustomLoopToolAssignment> assignments)
    {
        return assignments.Select(assignment => assignment switch
        {
            CustomLoopToolAssignment.List => ToolCommand.List,
            CustomLoopToolAssignment.Read => ToolCommand.Read,
            CustomLoopToolAssignment.Search => ToolCommand.Search,
            _ => throw new InvalidOperationException("Only admitted list, read, and search assignments are implemented.")
        }).ToArray();
    }
}
