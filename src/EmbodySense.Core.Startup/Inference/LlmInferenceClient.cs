using EmbodySense.Core.Common.Inference;
using System.Diagnostics;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Clients.CodexAppServer;
using EmbodySense.Core.Persistence.Audit;

namespace EmbodySense.Core.Startup.Inference;

/// <summary>
/// Provides the startup-owned, audited inference facade over the provider selected by
/// <see cref="LlmInferenceClientOptions"/>.
/// </summary>
/// <remarks>
/// When the working directory is already an initialized workspace, each non-canceled request is
/// audited with model, surface, path, timing, and character-count metadata. Prompt and response
/// text are not written to those audit events. Provider, callback, and audit failures propagate.
/// Dispose this instance to dispose the selected provider client.
/// </remarks>
public sealed class LlmInferenceClient : ILlmInferenceClient, IResettableInferenceClient, IQuarantinableInferenceClient, IAsyncDisposable
{
    private readonly LlmInferenceClientOptions _options;
    private readonly ILlmInferenceClient _innerClient;
    private readonly IAuditLog? _auditLog;

    /// <summary>
    /// Selects and owns the configured provider client.
    /// </summary>
    /// <param name="options">The validated provider, model, surface, and workspace configuration.</param>
    /// <param name="toolBroker">The optional governed tool broker exposed to a compatible provider.</param>
    /// <param name="codexAppServerTransport">An optional transport override used by the Codex app-server provider.</param>
    /// <param name="providerRequestStarted">An optional callback invoked when the selected provider starts a request.</param>
    public LlmInferenceClient(LlmInferenceClientOptions options, IToolBroker? toolBroker = null, ICodexAppServerTransport? codexAppServerTransport = null, Action? providerRequestStarted = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _auditLog = AuditLog.TryCreateForExistingWorkspace(options.WorkingDirectory);
        _innerClient = LlmInferenceClientFactory.CreateProvider(options, toolBroker, codexAppServerTransport, _auditLog, providerRequestStarted);
    }

    /// <summary>
    /// Generates one provider response and records the audited request lifecycle when auditing is available.
    /// </summary>
    /// <param name="request">The messages and trusted instruction context sent to the provider.</param>
    /// <param name="responseChunkHandler">An optional asynchronous callback for streamed response text.</param>
    /// <param name="cancellationToken">The token used to cancel provider work and audit writes.</param>
    /// <returns>A task whose result is the provider response.</returns>
    /// <remarks>
    /// Cancellation propagates and is not recorded as a failed inference. Other provider failures
    /// are audited before being rethrown. Once a terminal provider outcome is observed, a completion-audit
    /// failure is attached to that conclusive outcome rather than replacing it with outcome-unknown evidence.
    /// </remarks>
    public Task<LlmInferenceResponse> GenerateAsync(
        LlmInferenceRequest request,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        CancellationToken cancellationToken = default)
    {
        return GenerateAsync(request, responseChunkHandler, cancellationToken, providerRequestStarting: null);
    }

    /// <inheritdoc />
    public async Task<LlmInferenceResponse> GenerateAsync(
        LlmInferenceRequest request,
        Func<string, CancellationToken, Task>? responseChunkHandler,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? providerRequestStarting)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestId = request.Correlation?.ProviderAttemptId ?? Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();

        await RecordInferenceStartedAsync(requestId, request, cancellationToken);

        try
        {
            var response = providerRequestStarting is null
                ? await _innerClient.GenerateAsync(request, responseChunkHandler, cancellationToken)
                : await _innerClient.GenerateAsync(request, responseChunkHandler, cancellationToken, providerRequestStarting);
            stopwatch.Stop();
            try
            {
                await RecordInferenceSucceededAsync(requestId, request, response, stopwatch.Elapsed, CancellationToken.None);
            }
            catch (Exception auditException)
            {
                throw new LlmInferenceObservedResponseException("The terminal provider response was observed, but its completion audit could not be persisted. The response must be retained for review and the provider attempt must not be redispatched.", response, auditException);
            }

            return response;
        }
        catch (LlmInferenceObservedResponseException)
        {
            throw;
        }
        catch (LlmInferenceTerminalFailureException exception)
        {
            stopwatch.Stop();
            try
            {
                await RecordInferenceFailedAsync(requestId, request, exception, stopwatch.Elapsed, CancellationToken.None);
            }
            catch (Exception auditException)
            {
                var detail = $"{exception.Message} The conclusive provider failure was observed, but its completion audit could not be persisted; the provider attempt must not be redispatched.";
                throw new LlmInferenceTerminalFailureException(detail, exception.ProviderResponseId, new AggregateException(exception, auditException));
            }

            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            await RecordInferenceFailedAsync(requestId, request, exception, stopwatch.Elapsed, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Disposes the owned provider client when it implements synchronous or asynchronous disposal.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_innerClient is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_innerClient is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// Resets provider conversation state when the selected provider supports reset semantics.
    /// </summary>
    /// <remarks>Calling this method is a no-op for a provider that is not resettable.</remarks>
    public void ResetConversation()
    {
        if (_innerClient is IResettableInferenceClient resettableClient)
        {
            resettableClient.ResetConversation();
        }
    }

    private Task RecordInferenceStartedAsync(string requestId, LlmInferenceRequest request, CancellationToken cancellationToken)
    {
        return AppendAuditAsync(AuditEvent.Create(
            actor: AuditSchema.Actors.Llm,
            action: AuditSchema.Actions.LlmInferenceStart,
            target: _options.Surface.ToString(),
            outcome: AuditSchema.Outcomes.Started,
            detail: "Started LLM inference request.",
            metadata: CreateBaseMetadata(requestId, request)), cancellationToken);
    }

    private Task RecordInferenceSucceededAsync(
        string requestId,
        LlmInferenceRequest request,
        LlmInferenceResponse response,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var metadata = CreateCompletedMetadata(requestId, request, duration);
        metadata["output_character_count"] = response.OutputText.Length;
        metadata["provider_response_id"] = response.ProviderResponseId;

        return AppendAuditAsync(AuditEvent.Create(
            actor: AuditSchema.Actors.Llm,
            action: AuditSchema.Actions.LlmInferenceComplete,
            target: response.Surface.ToString(),
            outcome: AuditSchema.Outcomes.Succeeded,
            detail: "Completed LLM inference request.",
            metadata: metadata), cancellationToken);
    }

    private Task RecordInferenceFailedAsync(
        string requestId,
        LlmInferenceRequest request,
        Exception exception,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var metadata = CreateCompletedMetadata(requestId, request, duration);
        metadata["error_type"] = exception.GetType().Name;

        return AppendAuditAsync(AuditEvent.Create(
            actor: AuditSchema.Actors.Llm,
            action: AuditSchema.Actions.LlmInferenceComplete,
            target: _options.Surface.ToString(),
            outcome: AuditSchema.Outcomes.Failed,
            detail: "LLM inference request failed.",
            metadata: metadata), cancellationToken);
    }

    private async Task AppendAuditAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        if (_auditLog is null)
        {
            return;
        }

        await _auditLog.AppendAsync(auditEvent, cancellationToken);
    }

    private Dictionary<string, object?> CreateCompletedMetadata(string requestId, LlmInferenceRequest request, TimeSpan duration)
    {
        var metadata = CreateBaseMetadata(requestId, request);
        metadata["duration_ms"] = (long)duration.TotalMilliseconds;

        return metadata;
    }

    private Dictionary<string, object?> CreateBaseMetadata(string requestId, LlmInferenceRequest request)
    {
        var messageCharacterCount = request.Messages.Sum(message => message.Content.Length);
        var trustedInstructionCount = request.InstructionContext?.TrustedInstructions.Count ?? 0;
        var trustedInstructionCharacterCount = request.InstructionContext?.TrustedInstructions.Sum(instruction => instruction.Content.Length) ?? 0;
        var instructionCharacterCount = request.InstructionContext is null
            ? 0
            : EmbodySenseDeveloperInstructions.Compose(request.InstructionContext.Governance, request.InstructionContext.TrustedInstructions).Length;

        var metadata = new Dictionary<string, object?>
        {
            ["request_id"] = requestId,
            ["surface"] = _options.Surface.ToString(),
            ["model"] = _options.Model,
            ["working_directory"] = _options.WorkingDirectory,
            ["message_count"] = request.Messages.Count,
            ["message_character_count"] = messageCharacterCount,
            ["trusted_instruction_count"] = trustedInstructionCount,
            ["trusted_instruction_character_count"] = trustedInstructionCharacterCount,
            ["instruction_character_count"] = instructionCharacterCount,
            ["input_character_count"] = messageCharacterCount + instructionCharacterCount
        };

        if (request.Correlation is { } correlation)
        {
            metadata["provider_attempt_id"] = correlation.ProviderAttemptId;
            metadata["provider_correlation_id"] = correlation.ProviderCorrelationId;
            metadata["run_id"] = correlation.ToolAuditCorrelation?.RunId;
            metadata["loop_id"] = correlation.ToolAuditCorrelation?.LoopId;
            metadata["role_id"] = correlation.ToolAuditCorrelation?.RoleId;
            metadata["attempt_correlation_id"] = correlation.ToolAuditCorrelation?.AttemptCorrelationId;
        }

        return metadata.Where(item => item.Value is not null).ToDictionary(item => item.Key, item => item.Value);
    }

    /// <summary>
    /// Abandons provider transport state after an outcome-unknown attempt.
    /// </summary>
    public async Task QuarantineAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_innerClient is not IQuarantinableInferenceClient quarantinableClient)
        {
            throw new NotSupportedException("The selected inference provider cannot quarantine ambiguous transport state.");
        }

        await quarantinableClient.QuarantineAsync(cancellationToken);
    }
}
