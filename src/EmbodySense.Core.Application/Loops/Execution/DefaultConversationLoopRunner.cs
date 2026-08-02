using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Runtime;
using EmbodySense.Core.Application.Runtime;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Context;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Application.Memory.Models;
using EmbodySense.Core.Application.Runtime.Diagnostics;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Application.Runtime.State;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Loops.Execution;

/// <summary>
/// Serializes one interactive conversation turn through inference and projection, with optional durable transcript and run-evidence stores.
/// </summary>
/// <remarks>
/// A configured conversation-memory store synchronizes and persists the durable transcript. A configured loop-run store records
/// run evidence. Production composition supplies the persistent turn-protocol store; focused compositions that omit it use an
/// explicitly volatile protocol store while preserving the same checkpoint semantics.
/// </remarks>
public sealed class DefaultConversationLoopRunner : IDefaultConversationLoopRunner
{
    private readonly ILlmInferenceClient _inferenceClient;
    private readonly IConversationMemoryStore? _conversationMemoryStore;
    private readonly ConversationRuntimeState _conversationState;
    private readonly LoopDefinition _loopDefinition;
    private readonly ILoopRunStore? _loopRunStore;
    private readonly RuntimeSurfaceId _surface;
    private readonly IDefaultConversationTurnStore _turnStore;
    private readonly IDefaultConversationTurnFailpoint? _failpoint;
    private readonly ICapabilityAdmissionService _capabilityAdmissionService;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultConversationLoopRunner"/> type.
    /// </summary>
    /// <param name="inferenceClient">The inference client.</param>
    /// <param name="conversationState">The conversation state.</param>
    /// <param name="conversationMemoryStore">The conversation memory store.</param>
    /// <param name="loopDefinition">The loop definition.</param>
    /// <param name="loopRunStore">The loop run store.</param>
    /// <param name="surface">The surface.</param>
    /// <param name="turnStore">The durable turn protocol store.</param>
    /// <param name="failpoint">An optional process-loss injection seam.</param>
    /// <param name="capabilityAdmissionService">The workspace-bound exact capability admission authority.</param>
    public DefaultConversationLoopRunner(
        ILlmInferenceClient inferenceClient,
        ConversationRuntimeState conversationState,
        IConversationMemoryStore? conversationMemoryStore = null,
        LoopDefinition? loopDefinition = null,
        ILoopRunStore? loopRunStore = null,
        RuntimeSurfaceId? surface = null,
        IDefaultConversationTurnStore? turnStore = null,
        IDefaultConversationTurnFailpoint? failpoint = null,
        ICapabilityAdmissionService? capabilityAdmissionService = null)
    {
        ArgumentNullException.ThrowIfNull(inferenceClient);
        ArgumentNullException.ThrowIfNull(conversationState);

        _inferenceClient = inferenceClient;
        _conversationState = conversationState;
        _conversationMemoryStore = conversationMemoryStore;
        _loopDefinition = loopDefinition ?? LoopDefinition.CreateDefaultConversation();
        _loopRunStore = loopRunStore;
        _surface = surface ?? RuntimeSurfaceId.Runtime;
        _turnStore = turnStore ?? new VolatileDefaultConversationTurnStore();
        _failpoint = failpoint;
        _capabilityAdmissionService = capabilityAdmissionService ?? throw new ArgumentNullException(nameof(capabilityAdmissionService));
    }

    /// <summary>
    /// Runs one default conversation turn while holding exclusive conversation ownership.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The completed, cancelled, or failed turn and its updated runtime projection.</returns>
    public async Task<DefaultConversationLoopTurnResult> RunTurnAsync(DefaultConversationLoopTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A turn owns transcript synchronization, inference, and projection as one serialized unit;
        // concurrent callers wait rather than race durable and in-memory conversation state.
        IDisposable conversationLease;
        try
        {
            conversationLease = await _conversationState.AcquireExclusiveAccessAsync(request.CancellationToken);
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
            return DefaultConversationLoopTurnResult.Cancelled("Turn was cancelled while waiting to enter the conversation.");
        }

        using var ownedConversationLease = conversationLease;
        ConversationMemorySnapshot conversation;
        try
        {
            var existingRequest = await _turnStore.LoadAsync(DefaultConversationTurnProtocol.CreateTurnId(request.RequestId), request.CancellationToken);
            if (existingRequest is not null)
            {
                return ReplayExistingRequest(request, existingRequest);
            }

            var incompleteTurns = await _turnStore.ListIncompleteAsync(request.CancellationToken);
            if (incompleteTurns.Count > 0)
            {
                var pending = incompleteTurns[0];
                return DefaultConversationLoopTurnResult.Failed($"Default-conversation turn `{pending.TurnId}` has incomplete durable checkpoint `{pending.Checkpoint}`. Restart reconciliation must classify it before another provider attempt can begin.");
            }

            conversation = await LoadConversationSnapshotAsync(request.CancellationToken);
            var activeReview = (await _turnStore.ListNeedsReviewAsync(request.CancellationToken)).FirstOrDefault(record => IdentityMatches(record, conversation));
            if (activeReview is not null)
            {
                var reviewRun = new LoopRunIdentity(activeReview.Run.LoopId, activeReview.Run.RunId, activeReview.Run.RoleId);
                var detail = $"Default-conversation turn `{activeReview.TurnId}` remains in NeedsReview. {activeReview.ReviewDetail} Use `/review resolve {activeReview.TurnId}` only after inspecting provider and audit evidence; no later provider attempt can start before that explicit resolution.";
                return DefaultConversationLoopTurnResult.NeedsReview(detail, runIdentity: reviewRun);
            }

            if (!_conversationState.TrySynchronizeConversationTranscript(conversation.Messages))
            {
                return DefaultConversationLoopTurnResult.Failed("The durable workspace conversation changed outside this runtime. Active local context was preserved; inspect the conflicting transcript and explicitly choose a conversation before sending another model turn.");
            }

            _conversationState.SetDurableConversationVersion(conversation.Version);
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
            return DefaultConversationLoopTurnResult.Cancelled("Turn was cancelled while synchronizing the durable conversation.");
        }
        catch (Exception exception)
        {
            return DefaultConversationLoopTurnResult.Failed($"Could not synchronize the durable conversation before context assembly: {exception.Message}");
        }

        var userMessage = request.ToUserMessage();
        var inferenceContextMessages = _conversationState.ContextMessages
            .Concat([new RuntimeContextMessage(userMessage, RuntimeContextSource.CurrentTurnInput, "Current user input being evaluated by the active loop before provider dispatch.")])
            .ToArray();
        var trustedStartupInstructions = inferenceContextMessages
            .Where(message => message.Source == RuntimeContextSource.StartupContext && message.Message.Role == LlmMessageRole.System)
            .Select((message, index) => new EmbodySenseTrustedInstruction($"startup-context-{index + 1}", message.Message.Content))
            .ToList();
        var inferenceMessages = inferenceContextMessages
            .Where(message => message.Source != RuntimeContextSource.StartupContext || message.Message.Role != LlmMessageRole.System)
            .Select(message => message.Message)
            .ToArray();
        var availableToolCommands = Enum.GetValues<ToolCommand>()
            .Where(command => LoopCapabilityIds.AllowsWorkspaceCommand(_loopDefinition.CapabilityIds, command))
            .ToArray();
        var runId = DefaultConversationTurnProtocol.CreateRunId(request.RequestId);
        var runIdentity = new LoopRunIdentity(_loopDefinition.Id, runId, _loopDefinition.RoleId);
        var run = LoopRunRecord.Started(
            runId,
            _loopDefinition.Id,
            _loopDefinition.RoleId,
            _surface,
            _loopDefinition.Trigger,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["loopDisplayName"] = _loopDefinition.DisplayName,
                ["loopEditMode"] = _loopDefinition.EditMode.ToString(),
                ["graphEntryNodeId"] = _loopDefinition.Graph?.EntryNodeId ?? "",
                ["graphNodeCount"] = (_loopDefinition.Graph?.Nodes?.Length ?? 0).ToString(CultureInfo.InvariantCulture),
                ["reviewPolicy"] = _loopDefinition.ReviewPolicy.ToString(),
                ["failurePolicy"] = _loopDefinition.FailurePolicy.ToString()
            });
        DefaultConversationTurnRecord? turn = null;
        var acceptedTranscriptMessages = new List<RuntimeTranscriptMessage>();
        var userMessageAccepted = false;

        try
        {
            var assignedCapabilityIds = LoopCapabilityRequirements.GetAssignedCapabilityIds(_loopDefinition.CapabilityRequirements);
            var capabilityAdmission = await _capabilityAdmissionService.AdmitAsync(_loopDefinition.CapabilityRequirements, assignedCapabilityIds, request.CancellationToken);
            if (!capabilityAdmission.IsAdmitted || capabilityAdmission.Snapshot is null)
            {
                return DefaultConversationLoopTurnResult.Failed($"Default-conversation capability admission failed closed: {capabilityAdmission.Detail}", runIdentity: runIdentity);
            }

            trustedStartupInstructions.Add(new EmbodySenseTrustedInstruction("admitted-capabilities", FormatSafeCapabilities(capabilityAdmission.Snapshot.Pins)));
            var instructionContext = new LlmInferenceInstructionContext(EmbodySenseDeveloperInstructions.Capture(availableToolCommands), trustedStartupInstructions, preserveExactLogicalContext: false);
            turn = DefaultConversationTurnProtocol.Admit(run, conversation, userMessage, DateTimeOffset.UtcNow, request.RequestId, capabilityAdmission.Snapshot);
            var admission = await _turnStore.CreateAsync(turn, CancellationToken.None);
            if (admission.Status is not (DefaultConversationTurnStoreStatus.Created or DefaultConversationTurnStoreStatus.Replay) || admission.Record is null)
            {
                var existing = await _turnStore.LoadAsync(turn.TurnId, CancellationToken.None);
                return existing is null
                    ? DefaultConversationLoopTurnResult.Failed("Could not durably admit the default-conversation turn because its stable identity conflicted.", runIdentity: runIdentity)
                    : ReplayExistingRequest(request, existing);
            }

            turn = admission.Record;
            await InvokeFailpointAsync(DefaultConversationTurnBoundary.TurnAdmitted, turn, request.CancellationToken);
            await SaveRunProjectionAsync(turn.Run, CancellationToken.None);
            await InvokeFailpointAsync(DefaultConversationTurnBoundary.RunStartSaved, turn, request.CancellationToken);
            turn = await AdvanceAsync(turn, DefaultConversationTurnCheckpoint.RunStarted, "Started loop-run projection persisted.", DefaultConversationTurnBoundary.RunStartCheckpointed, request.CancellationToken);

            if (_loopDefinition.State != LoopState.Enabled)
            {
                var detail = $"Loop `{_loopDefinition.Id}` is not enabled.";
                turn = await FinalizeAsync(turn, LoopRunStatus.Failed, detail, CancellationToken.None);
                return DefaultConversationLoopTurnResult.Failed(detail, runIdentity: runIdentity);
            }

            var graphExecutionBlocker = DefaultConversationLoopGraphContract.GetExecutionBlocker(_loopDefinition);
            if (graphExecutionBlocker is not null)
            {
                turn = await FinalizeAsync(turn, LoopRunStatus.Failed, graphExecutionBlocker, CancellationToken.None);
                return DefaultConversationLoopTurnResult.Failed(graphExecutionBlocker, runIdentity: runIdentity);
            }

            await EmitVisibleContextAsync(request, runIdentity, inferenceContextMessages);
            turn = await AdvanceAsync(turn, DefaultConversationTurnCheckpoint.UserMessageAccepted, "Exact user message content and stable message identity accepted.", DefaultConversationTurnBoundary.UserAccepted, request.CancellationToken);
            userMessageAccepted = true;
            turn = await AdvanceAsync(turn, DefaultConversationTurnCheckpoint.UserPublicationPrepared, "User-message transcript publication intent persisted.", DefaultConversationTurnBoundary.UserPublicationPrepared, request.CancellationToken);
            var capabilityFailure = await GetCapabilityFailureAsync(turn, assignedCapabilityIds, request.CancellationToken);
            if (capabilityFailure is not null)
            {
                var detail = $"Default-conversation capability authority changed before user transcript publication: {capabilityFailure}";
                turn = await FinalizeAsync(turn, LoopRunStatus.Failed, detail, CancellationToken.None);
                return DefaultConversationLoopTurnResult.Failed(detail, acceptedTranscriptMessages.ToArray(), runIdentity, userMessageAccepted: true);
            }

            var userPublication = await PublishMessageAsync(turn, turn.BaseTranscript, turn.UserMessage, DefaultConversationTurnBoundary.UserTranscriptAppended, request.CancellationToken);
            if (userPublication is null)
            {
                var detail = TranscriptConflictDetail(turn, turn.UserMessage);
                turn = await FinalizeAsync(turn, LoopRunStatus.NeedsReview, detail, CancellationToken.None);
                return DefaultConversationLoopTurnResult.NeedsReview(detail, runIdentity: runIdentity, userMessageAccepted: true);
            }

            _conversationState.SynchronizeConversationTranscript(userPublication.Messages);
            acceptedTranscriptMessages.Add(new RuntimeTranscriptMessage(userMessage));
            turn = await AdvanceAsync(turn, DefaultConversationTurnCheckpoint.UserPublished, "User message is present exactly once in the canonical transcript.", DefaultConversationTurnBoundary.UserPublished, request.CancellationToken);
            capabilityFailure = await GetCapabilityFailureAsync(turn, assignedCapabilityIds, request.CancellationToken);
            if (capabilityFailure is not null)
            {
                var detail = $"Default-conversation capability authority changed before provider dispatch: {capabilityFailure}";
                turn = await FinalizeAsync(turn, LoopRunStatus.Failed, detail, CancellationToken.None);
                return DefaultConversationLoopTurnResult.Failed(detail, acceptedTranscriptMessages.ToArray(), runIdentity, userMessageAccepted: true);
            }

            turn = await AdvanceAsync(turn, DefaultConversationTurnCheckpoint.ProviderDispatchPrepared, "Stable provider attempt and correlation identities prepared; provider turn/start transport-write boundary not yet reached.", DefaultConversationTurnBoundary.ProviderDispatchPrepared, request.CancellationToken);
            var inferenceRequest = new LlmInferenceRequest(inferenceMessages, instructionContext: instructionContext, correlation: CreateInferenceCorrelation(turn, availableToolCommands));
            var dispatchStarted = false;
            LlmInferenceResponse response;
            try
            {
                response = await _inferenceClient.GenerateAsync(
                    inferenceRequest,
                    request.ResponseChunkHandler,
                    request.CancellationToken,
                    async token =>
                    {
                        turn = await AdvanceAsync(
                            turn,
                            DefaultConversationTurnCheckpoint.ProviderDispatchStarted,
                            "Provider adapter reached the irreversible turn/start transport boundary; outcome is unknown until a terminal response is durably observed.",
                            DefaultConversationTurnBoundary.ProviderDispatchStarted,
                            token,
                            providerOutcome: DefaultConversationProviderOutcome.OutcomeUnknown);
                        dispatchStarted = true;
                    });
            }
            catch (LlmInferenceObservedResponseException exception)
            {
                if (!dispatchStarted)
                {
                    throw new InvalidOperationException("The inference adapter reported an observed provider response without invoking the required provider-dispatch boundary callback.", exception);
                }

                var observedResponse = exception.Response;
                if (string.IsNullOrWhiteSpace(observedResponse.OutputText))
                {
                    var emptyDetail = $"Provider completed without a usable assistant message body; the terminal provider outcome is conclusive, but no assistant transcript message can be published. Completion audit detail: {exception.Message}";
                    turn = await AdvanceAsync(
                        turn,
                        DefaultConversationTurnCheckpoint.ProviderOutcomeObserved,
                        emptyDetail,
                        DefaultConversationTurnBoundary.ProviderOutcomeObserved,
                        CancellationToken.None,
                        providerOutcome: DefaultConversationProviderOutcome.ObservedFailure,
                        providerResponseId: observedResponse.ProviderResponseId);
                    turn = await FinalizeAsync(turn, LoopRunStatus.Failed, emptyDetail, CancellationToken.None);
                    return DefaultConversationLoopTurnResult.Failed(emptyDetail, acceptedTranscriptMessages.ToArray(), runIdentity, userMessageAccepted: true);
                }

                var observedAssistantMessage = new DefaultConversationTurnMessage(DefaultConversationTurnProtocol.CreateAssistantMessageId(turn.TurnId), LlmMessageRole.Assistant, observedResponse.OutputText);
                var detail = exception.Message;
                turn = await AdvanceAsync(
                    turn,
                    DefaultConversationTurnCheckpoint.ProviderOutcomeObserved,
                    detail,
                    DefaultConversationTurnBoundary.ProviderOutcomeObserved,
                    CancellationToken.None,
                    providerOutcome: DefaultConversationProviderOutcome.ObservedWithAuditFailure,
                    assistantMessage: observedAssistantMessage,
                    providerResponseId: observedResponse.ProviderResponseId);
                turn = await FinalizeAsync(turn, LoopRunStatus.NeedsReview, detail, CancellationToken.None);
                return DefaultConversationLoopTurnResult.NeedsReview(detail, acceptedTranscriptMessages.ToArray(), runIdentity, userMessageAccepted: true);
            }
            catch (LlmInferenceTerminalFailureException exception)
            {
                if (!dispatchStarted)
                {
                    throw new InvalidOperationException("The inference adapter reported a terminal provider failure without invoking the required provider-dispatch boundary callback.", exception);
                }

                var detail = exception.Message;
                turn = await AdvanceAsync(
                    turn,
                    DefaultConversationTurnCheckpoint.ProviderOutcomeObserved,
                    detail,
                    DefaultConversationTurnBoundary.ProviderOutcomeObserved,
                    CancellationToken.None,
                    providerOutcome: DefaultConversationProviderOutcome.ObservedFailure,
                    providerResponseId: exception.ProviderResponseId);
                turn = await FinalizeAsync(turn, LoopRunStatus.Failed, detail, CancellationToken.None);
                return DefaultConversationLoopTurnResult.Failed(detail, acceptedTranscriptMessages.ToArray(), runIdentity, userMessageAccepted: true);
            }

            if (!dispatchStarted)
            {
                throw new InvalidOperationException("The inference adapter returned without invoking the required provider-dispatch boundary callback.");
            }

            if (string.IsNullOrWhiteSpace(response.OutputText))
            {
                var detail = "Provider completed without a usable assistant message body; the terminal provider outcome is conclusive, but no assistant transcript message can be published.";
                turn = await AdvanceAsync(
                    turn,
                    DefaultConversationTurnCheckpoint.ProviderOutcomeObserved,
                    detail,
                    DefaultConversationTurnBoundary.ProviderOutcomeObserved,
                    CancellationToken.None,
                    providerOutcome: DefaultConversationProviderOutcome.ObservedFailure,
                    providerResponseId: response.ProviderResponseId);
                turn = await FinalizeAsync(turn, LoopRunStatus.Failed, detail, CancellationToken.None);
                return DefaultConversationLoopTurnResult.Failed(detail, acceptedTranscriptMessages.ToArray(), runIdentity, userMessageAccepted: true);
            }

            var assistantMessage = new DefaultConversationTurnMessage(DefaultConversationTurnProtocol.CreateAssistantMessageId(turn.TurnId), LlmMessageRole.Assistant, response.OutputText);
            turn = await AdvanceAsync(
                turn,
                DefaultConversationTurnCheckpoint.ProviderOutcomeObserved,
                "Terminal provider output observed and canonicalized with stable assistant identity.",
                DefaultConversationTurnBoundary.ProviderOutcomeObserved,
                CancellationToken.None,
                providerOutcome: DefaultConversationProviderOutcome.Observed,
                assistantMessage: assistantMessage,
                providerResponseId: response.ProviderResponseId);
            turn = await AdvanceAsync(turn, DefaultConversationTurnCheckpoint.AssistantPublicationPrepared, "Assistant-message publication intent persisted.", DefaultConversationTurnBoundary.AssistantPublicationPrepared, CancellationToken.None);
            capabilityFailure = await GetCapabilityFailureAsync(turn, assignedCapabilityIds, CancellationToken.None);
            if (capabilityFailure is not null)
            {
                var detail = $"Default-conversation capability authority changed before assistant transcript publication: {capabilityFailure}";
                turn = await FinalizeAsync(turn, LoopRunStatus.NeedsReview, detail, CancellationToken.None);
                return DefaultConversationLoopTurnResult.NeedsReview(detail, acceptedTranscriptMessages.ToArray(), runIdentity, userMessageAccepted: true);
            }

            var assistantPublication = await PublishMessageAsync(turn, CanonicalUserTranscript(turn), assistantMessage, DefaultConversationTurnBoundary.AssistantTranscriptAppended, CancellationToken.None);
            if (assistantPublication is null)
            {
                var detail = TranscriptConflictDetail(turn, assistantMessage);
                turn = await FinalizeAsync(turn, LoopRunStatus.NeedsReview, detail, CancellationToken.None);
                return DefaultConversationLoopTurnResult.NeedsReview(detail, acceptedTranscriptMessages.ToArray(), runIdentity, userMessageAccepted: true);
            }

            acceptedTranscriptMessages.Add(new RuntimeTranscriptMessage(assistantMessage.ToLlmMessage()));
            turn = await AdvanceAsync(turn, DefaultConversationTurnCheckpoint.AssistantPublished, "Assistant message is present exactly once in the canonical transcript.", DefaultConversationTurnBoundary.AssistantPublished, CancellationToken.None);
            _conversationState.SynchronizeConversationTranscript(assistantPublication.Messages);
            turn = await AdvanceAsync(turn, DefaultConversationTurnCheckpoint.TranscriptSynchronized, "Runtime state and durable conversation memory match the canonical ordered transcript.", DefaultConversationTurnBoundary.TranscriptSynchronized, CancellationToken.None);
            turn = await FinalizeAsync(turn, LoopRunStatus.Completed, "Default-conversation turn completed.", CancellationToken.None);
            return DefaultConversationLoopTurnResult.Completed(response.OutputText, acceptedTranscriptMessages.ToArray(), runIdentity);
        }
        catch (DefaultConversationTurnInterruptedException)
        {
            throw;
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
            return await HandleInterruptedOutcomeAsync(turn, runIdentity, acceptedTranscriptMessages, userMessageAccepted, "Turn was cancelled.", cancelled: true);
        }
        catch (Exception exception)
        {
            return await HandleInterruptedOutcomeAsync(turn, runIdentity, acceptedTranscriptMessages, userMessageAccepted, exception.Message, cancelled: false);
        }
    }

    private static string FormatSafeCapabilities(IReadOnlyList<CapabilityAdmissionPin> pins)
    {
        var descriptions = pins.OrderBy(pin => pin.DescriptorIdentity.Id.Value, StringComparer.Ordinal).Select(pin => $"- {pin.SafeDescription}");
        return "Effective admitted capabilities (descriptions only; pins, provenance, catalog state, private configuration, and secrets are intentionally omitted):" + Environment.NewLine + string.Join(Environment.NewLine, descriptions);
    }

    private async Task<string?> GetCapabilityFailureAsync(DefaultConversationTurnRecord turn, IReadOnlyCollection<CapabilityId> assignedCapabilityIds, CancellationToken cancellationToken)
    {
        var currentCapabilities = await _capabilityAdmissionService.RevalidateAsync(turn.CapabilityAdmission, assignedCapabilityIds, cancellationToken);
        return currentCapabilities.IsValid ? null : currentCapabilities.Detail;
    }

    private async Task<DefaultConversationLoopTurnResult> HandleInterruptedOutcomeAsync(
        DefaultConversationTurnRecord? turn,
        LoopRunIdentity runIdentity,
        IReadOnlyList<RuntimeTranscriptMessage> acceptedMessages,
        bool userMessageAccepted,
        string detail,
        bool cancelled)
    {
        if (turn is null)
        {
            return cancelled ? DefaultConversationLoopTurnResult.Cancelled(detail) : DefaultConversationLoopTurnResult.Failed(detail, runIdentity: runIdentity);
        }

        if (turn.Checkpoint >= DefaultConversationTurnCheckpoint.ProviderDispatchStarted
            && turn.ProviderOutcome is not (DefaultConversationProviderOutcome.Observed or DefaultConversationProviderOutcome.ObservedWithAuditFailure or DefaultConversationProviderOutcome.ObservedFailure))
        {
            var quarantineDetail = await QuarantineProviderAsync();
            var ambiguity = $"Provider attempt `{turn.ProviderAttemptId}` with correlation `{turn.ProviderCorrelationId}` reached the irreversible turn/start transport-write boundary, but no terminal outcome was durably observed. Automatic redispatch is forbidden; inspect provider and audit evidence before retrying as a new turn. {quarantineDetail} Observed adapter detail: {detail}";
            var finalDetail = await TryFinalizeWithEvidenceAsync(turn, LoopRunStatus.NeedsReview, ambiguity);
            return DefaultConversationLoopTurnResult.NeedsReview(finalDetail, acceptedMessages, runIdentity, userMessageAccepted);
        }

        if (turn.ProviderOutcome == DefaultConversationProviderOutcome.Observed)
        {
            var recoveryDetail = $"Assistant outcome was durably observed, but publication or terminal synchronization did not complete: {detail}. Restart recovery can repair only the retained idempotent publication; provider redispatch is forbidden.";
            return DefaultConversationLoopTurnResult.Failed(recoveryDetail, acceptedMessages, runIdentity, userMessageAccepted);
        }

        if (turn.ProviderOutcome == DefaultConversationProviderOutcome.ObservedWithAuditFailure)
        {
            var auditFailureDetail = await TryFinalizeWithEvidenceAsync(turn, LoopRunStatus.NeedsReview, detail);
            return DefaultConversationLoopTurnResult.NeedsReview(auditFailureDetail, acceptedMessages, runIdentity, userMessageAccepted);
        }

        if (turn.ProviderOutcome == DefaultConversationProviderOutcome.ObservedFailure)
        {
            var observedFailureDetail = await TryFinalizeWithEvidenceAsync(turn, LoopRunStatus.Failed, detail);
            return DefaultConversationLoopTurnResult.Failed(observedFailureDetail, acceptedMessages, runIdentity, userMessageAccepted);
        }

        if (turn.Checkpoint is DefaultConversationTurnCheckpoint.UserMessageAccepted or DefaultConversationTurnCheckpoint.UserPublicationPrepared)
        {
            var recoveryDetail = $"The user message was durably accepted, but its transcript publication outcome was not checkpointed: {detail}. Restart reconciliation must prove or repair the exact append before another provider attempt can begin.";
            return cancelled
                ? DefaultConversationLoopTurnResult.Cancelled(recoveryDetail, acceptedMessages, runIdentity, userMessageAccepted: true)
                : DefaultConversationLoopTurnResult.Failed(recoveryDetail, acceptedMessages, runIdentity, userMessageAccepted: true);
        }

        if (turn.Checkpoint == DefaultConversationTurnCheckpoint.Admitted)
        {
            detail = "Could not record loop run start: " + detail;
        }

        var status = cancelled ? LoopRunStatus.Cancelled : LoopRunStatus.Failed;
        var terminalDetail = await TryFinalizeWithEvidenceAsync(turn, status, detail);
        return cancelled
            ? DefaultConversationLoopTurnResult.Cancelled(terminalDetail, acceptedMessages, runIdentity, userMessageAccepted)
            : DefaultConversationLoopTurnResult.Failed(terminalDetail, acceptedMessages, runIdentity, userMessageAccepted);
    }

    private async Task<string> QuarantineProviderAsync()
    {
        if (_inferenceClient is not IQuarantinableInferenceClient quarantinableClient)
        {
            return "This provider client cannot be safely reused; restart the runtime before resolving review.";
        }

        try
        {
            await quarantinableClient.QuarantineAsync(CancellationToken.None);
            return "The ambiguous provider transport was quarantined and cannot contaminate a later turn.";
        }
        catch (Exception exception)
        {
            return $"Provider transport quarantine failed with `{exception.GetType().Name}`; restart the runtime before resolving review.";
        }
    }

    private async Task<string> TryFinalizeWithEvidenceAsync(DefaultConversationTurnRecord turn, LoopRunStatus status, string detail)
    {
        try
        {
            _ = await FinalizeAsync(turn, status, detail, CancellationToken.None);
            return detail;
        }
        catch (Exception exception) when (exception is not DefaultConversationTurnInterruptedException)
        {
            return $"{detail} Durable terminal synchronization also failed: {exception.Message}";
        }
    }

    private async Task<DefaultConversationTurnRecord> FinalizeAsync(DefaultConversationTurnRecord turn, LoopRunStatus status, string detail, CancellationToken cancellationToken)
    {
        if (turn.Checkpoint < DefaultConversationTurnCheckpoint.TerminalPrepared)
        {
            var now = DateTimeOffset.UtcNow;
            var terminalRun = status switch
            {
                LoopRunStatus.Completed => turn.Run.Complete(now),
                LoopRunStatus.Cancelled => turn.Run.Cancel(now, detail),
                LoopRunStatus.NeedsReview => turn.Run.NeedsReview(now, detail),
                LoopRunStatus.Failed => turn.Run.Fail(now, detail),
                _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Choose a terminal turn status.")
            };
            turn = await AdvanceAsync(turn, DefaultConversationTurnCheckpoint.TerminalPrepared, "Desired terminal run status and checkpoint persisted.", DefaultConversationTurnBoundary.TerminalPrepared, cancellationToken, run: terminalRun, reviewDetail: status == LoopRunStatus.NeedsReview ? detail : null);
        }

        await SaveRunProjectionAsync(turn.Run, cancellationToken);
        await InvokeFailpointAsync(DefaultConversationTurnBoundary.TerminalRunSaved, turn, cancellationToken);
        turn = await AdvanceAsync(turn, DefaultConversationTurnCheckpoint.Terminal, "Terminal loop-run projection synchronized with the durable turn protocol.", DefaultConversationTurnBoundary.TerminalCommitted, cancellationToken, runProjectionSynchronized: true);
        return turn;
    }

    private async Task<DefaultConversationTurnRecord> AdvanceAsync(
        DefaultConversationTurnRecord turn,
        DefaultConversationTurnCheckpoint checkpoint,
        string detail,
        DefaultConversationTurnBoundary boundary,
        CancellationToken cancellationToken,
        DefaultConversationProviderOutcome? providerOutcome = null,
        DefaultConversationTurnMessage? assistantMessage = null,
        string? providerResponseId = null,
        LoopRunRecord? run = null,
        bool? runProjectionSynchronized = null,
        string? reviewDetail = null)
    {
        var candidate = turn.Advance(checkpoint, DateTimeOffset.UtcNow, detail, providerOutcome, assistantMessage, providerResponseId, run, runProjectionSynchronized, reviewDetail);
        var result = await _turnStore.UpdateAsync(candidate, turn.LifecycleVersion, cancellationToken);
        if (result.Status is not (DefaultConversationTurnStoreStatus.Updated or DefaultConversationTurnStoreStatus.Replay) || result.Record is null)
        {
            throw new InvalidOperationException($"Default-conversation checkpoint `{checkpoint}` conflicted with lifecycle version `{turn.LifecycleVersion}`.");
        }

        await InvokeFailpointAsync(boundary, result.Record, cancellationToken);
        return result.Record;
    }

    private async Task<ConversationMemorySnapshot?> PublishMessageAsync(
        DefaultConversationTurnRecord turn,
        IReadOnlyList<LlmMessage> expectedPrefix,
        DefaultConversationTurnMessage message,
        DefaultConversationTurnBoundary boundary,
        CancellationToken cancellationToken)
    {
        if (_conversationMemoryStore is null)
        {
            var messages = expectedPrefix.Concat([message.ToLlmMessage()]).ToArray();
            _conversationState.SynchronizeConversationTranscript(messages);
            await InvokeFailpointAsync(boundary, turn, cancellationToken);
            return new ConversationMemorySnapshot(turn.ConversationId, turn.ConversationVersion, messages);
        }

        var publicationId = message.Role == LlmMessageRole.User ? turn.UserPublicationId : turn.AssistantPublicationId;
        var result = await _conversationMemoryStore.TryPublishMessageAsync(
            turn.ConversationId,
            turn.ConversationVersion,
            expectedPrefix,
            new ConversationMessagePublication(message.MessageId, publicationId, message.ToLlmMessage()),
            cancellationToken);
        if (result.Status == ConversationPublicationAppendStatus.Appended)
        {
            await InvokeFailpointAsync(boundary, turn, cancellationToken);
        }

        return result.Status is ConversationPublicationAppendStatus.Appended or ConversationPublicationAppendStatus.AlreadyPresent
            ? result.Snapshot
            : null;
    }

    private async Task<ConversationMemorySnapshot> LoadConversationSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_conversationMemoryStore is not null)
        {
            return await _conversationMemoryStore.LoadCurrentConversationSnapshotAsync(cancellationToken);
        }

        var transcript = _conversationState.ContextMessages.Where(message => message.Source != RuntimeContextSource.StartupContext).Select(message => message.Message).ToArray();
        return new ConversationMemorySnapshot("volatile-current", _conversationState.DurableConversationVersion ?? "volatile-runtime", transcript);
    }

    private async Task SaveRunProjectionAsync(LoopRunRecord run, CancellationToken cancellationToken)
    {
        if (_loopRunStore is not null)
        {
            await _loopRunStore.SaveAsync(run, cancellationToken);
        }
    }

    private Task InvokeFailpointAsync(DefaultConversationTurnBoundary boundary, DefaultConversationTurnRecord turn, CancellationToken cancellationToken)
    {
        return _failpoint?.AfterBoundaryAsync(boundary, turn, cancellationToken) ?? Task.CompletedTask;
    }

    private static IReadOnlyList<LlmMessage> CanonicalUserTranscript(DefaultConversationTurnRecord turn)
    {
        return [.. turn.BaseTranscript, turn.UserMessage.ToLlmMessage()];
    }

    private static bool IdentityMatches(DefaultConversationTurnRecord turn, ConversationMemorySnapshot snapshot)
    {
        return string.Equals(turn.ConversationId, snapshot.ConversationId, StringComparison.Ordinal)
            && string.Equals(turn.ConversationVersion, snapshot.Version, StringComparison.Ordinal);
    }

    private static bool MessagesEqual(IReadOnlyList<LlmMessage> left, IReadOnlyList<LlmMessage> right)
    {
        return left.Count == right.Count && left.Zip(right).All(pair => pair.First.Role == pair.Second.Role && string.Equals(pair.First.Content, pair.Second.Content, StringComparison.Ordinal));
    }

    private static string TranscriptConflictDetail(DefaultConversationTurnRecord turn, DefaultConversationTurnMessage message)
    {
        var publicationId = message.Role == LlmMessageRole.User ? turn.UserPublicationId : turn.AssistantPublicationId;
        return $"Conversation `{turn.ConversationId}` version `{turn.ConversationVersion}` no longer has the exact expected prefix for publication `{publicationId}` and message `{message.MessageId}`. Existing user-owned content was preserved; inspect the durable turn record before reconciling.";
    }

    private static DefaultConversationLoopTurnResult ReplayExistingRequest(DefaultConversationLoopTurnRequest request, DefaultConversationTurnRecord record)
    {
        var runIdentity = new LoopRunIdentity(record.Run.LoopId, record.Run.RunId, record.Run.RoleId);
        if (!string.Equals(record.RequestId, request.RequestId, StringComparison.Ordinal)
            || record.UserMessage.Role != LlmMessageRole.User
            || !string.Equals(record.UserMessage.Content, request.Input, StringComparison.Ordinal))
        {
            return DefaultConversationLoopTurnResult.Failed($"Request id `{request.RequestId}` was already used for a different default-conversation payload.", runIdentity: runIdentity);
        }

        if (record.Checkpoint < DefaultConversationTurnCheckpoint.Terminal)
        {
            return DefaultConversationLoopTurnResult.Failed($"Request `{request.RequestId}` is already admitted as turn `{record.TurnId}` at checkpoint `{record.Checkpoint}`. Restart reconciliation must classify it before any retry.", runIdentity: runIdentity);
        }

        var userAccepted = record.Transitions.Any(transition => transition.Checkpoint == DefaultConversationTurnCheckpoint.UserMessageAccepted);
        if (record.Checkpoint == DefaultConversationTurnCheckpoint.ReviewResolved)
        {
            return DefaultConversationLoopTurnResult.Failed(record.ReviewResolution?.Detail ?? "The idempotently replayed turn was explicitly abandoned after review.", runIdentity: runIdentity, userMessageAccepted: userAccepted);
        }

        return record.Run.Status switch
        {
            LoopRunStatus.Completed when record.AssistantMessage is not null => DefaultConversationLoopTurnResult.Completed(record.AssistantMessage.Content, runIdentity: runIdentity),
            LoopRunStatus.Cancelled => DefaultConversationLoopTurnResult.Cancelled(record.Run.FailureDetail ?? "The idempotently replayed turn was cancelled.", runIdentity: runIdentity, userMessageAccepted: userAccepted),
            LoopRunStatus.NeedsReview => DefaultConversationLoopTurnResult.NeedsReview(record.ReviewDetail ?? "The idempotently replayed turn requires explicit review.", runIdentity: runIdentity, userMessageAccepted: userAccepted),
            _ => DefaultConversationLoopTurnResult.Failed(record.Run.FailureDetail ?? "The idempotently replayed turn failed.", runIdentity: runIdentity, userMessageAccepted: userAccepted)
        };
    }

    private LlmInferenceCorrelation CreateInferenceCorrelation(DefaultConversationTurnRecord turn, IReadOnlyList<ToolCommand> availableToolCommands)
    {
        var definitionJson = JsonSerializer.Serialize(_loopDefinition, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var definitionHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definitionJson))).ToLowerInvariant();
        var commands = string.Join(',', availableToolCommands.Select(ToolCommandFormatter.Format));
        var toolCorrelation = new ToolAuditCorrelation(
            turn.Run.RunId,
            _loopDefinition.Id,
            _loopDefinition.RoleId,
            _loopDefinition.SchemaVersion,
            definitionHash,
            1,
            DefaultConversationLoopGraphIds.DispatchInference,
            1,
            turn.ProviderCorrelationId,
            commands,
            commands,
            commands);
        return new LlmInferenceCorrelation(turn.ProviderAttemptId, turn.ProviderCorrelationId, toolCorrelation);
    }

    private async Task EmitVisibleContextAsync(
        DefaultConversationLoopTurnRequest request,
        LoopRunIdentity runIdentity,
        IReadOnlyList<RuntimeContextMessage> messages)
    {
        if (request.DiagnosticHandler is null)
        {
            return;
        }

        var content = RuntimeDiagnosticFormatter.FormatVerboseContext(new RuntimeVerboseContext(
            _loopDefinition,
            runIdentity,
            _surface,
            messages,
            CreateContextOmissions(messages),
            "No compaction engine or compaction artifact is active in the default conversation loop yet."));
        await request.DiagnosticHandler(new RuntimeDiagnosticMessage(RuntimeDiagnosticKind.VerboseContext, content, "Visible inference context"), request.CancellationToken);
    }

    private static IReadOnlyList<RuntimeContextOmission> CreateContextOmissions(IReadOnlyList<RuntimeContextMessage> messages)
    {
        var omissions = new List<RuntimeContextOmission>
        {
            new(
                "provider-adapter",
                "provider-formatting",
                "Codex app-server formatting can wrap restored context as lower-authority material and omit older restored messages when its adapter budget is exceeded; this diagnostic is emitted before that provider adapter formatting."),
            new(
                "higher-order-memory",
                "runtime-context-assembly",
                "No higher-order memory retrieval or consolidation artifact is active in this default loop path yet.")
        };

        omissions.Add(GetLocalMemoryStatus(messages));

        if (messages.Any(message => message.Message.Content.Contains("[truncated after", StringComparison.OrdinalIgnoreCase) || message.Message.Content.Contains("[truncated]", StringComparison.OrdinalIgnoreCase)))
        {
            omissions.Add(new RuntimeContextOmission(
                "startup-or-restored-context",
                "runtime-context-assembly",
                "At least one context message reports in-band truncation from its source reader."));
        }

        return omissions;
    }

    private static RuntimeContextOmission GetLocalMemoryStatus(IReadOnlyList<RuntimeContextMessage> messages)
    {
        var startupContent = string.Join(Environment.NewLine, messages.Where(message => message.Source == RuntimeContextSource.StartupContext).Select(message => message.Message.Content));
        var memorySectionHeader = $"## {AgentContextProvider.ContextualStateClassification}: .agent/MEMORY.md";
        if (startupContent.Contains(memorySectionHeader, StringComparison.Ordinal))
        {
            return new RuntimeContextOmission(
                "local-memory",
                "runtime-context-assembly",
                ".agent/MEMORY.md is present in the active startup context.");
        }

        return new RuntimeContextOmission(
            "local-memory",
            "runtime-context-assembly",
            ".agent/MEMORY.md is not included in the active startup context; it may be missing, empty, or not loaded in this workspace.");
    }
}
