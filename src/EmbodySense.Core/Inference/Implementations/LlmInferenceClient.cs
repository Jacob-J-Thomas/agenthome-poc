using System.Diagnostics;
using EmbodySense.Core.Audit;
using EmbodySense.Core.Audit.Models;
using EmbodySense.Core.Inference.Interfaces;
using EmbodySense.Core.Inference.Models;
using EmbodySense.Core.Tools;

namespace EmbodySense.Core.Inference.Implementations;

public sealed class LlmInferenceClient : ILlmInferenceClient, IAsyncDisposable
{
    private readonly LlmInferenceClientOptions _options;
    private readonly ILlmInferenceClient _innerClient;
    private readonly AuditLog? _auditLog;

    public LlmInferenceClient(LlmInferenceClientOptions options, IToolBroker? toolBroker = null, ICodexAppServerTransport? codexAppServerTransport = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _innerClient = LlmInferenceClientFactory.CreateProvider(options, toolBroker, codexAppServerTransport);
        _auditLog = AuditLog.TryCreateForExistingWorkspace(options.WorkingDirectory);
    }

    public async Task<LlmInferenceResponse> GenerateAsync(
        LlmInferenceRequest request,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();

        await RecordInferenceStartedAsync(requestId, request, cancellationToken);

        try
        {
            var response = await _innerClient.GenerateAsync(request, responseChunkHandler, cancellationToken);
            stopwatch.Stop();
            await RecordInferenceSucceededAsync(requestId, request, response, stopwatch.Elapsed, cancellationToken);
            return response;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            await RecordInferenceFailedAsync(requestId, request, exception, stopwatch.Elapsed, cancellationToken);
            throw;
        }
    }

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
        return new Dictionary<string, object?>
        {
            ["request_id"] = requestId,
            ["surface"] = _options.Surface.ToString(),
            ["model"] = _options.Model,
            ["working_directory"] = _options.WorkingDirectory,
            ["message_count"] = request.Messages.Count,
            ["input_character_count"] = request.Messages.Sum(message => message.Content.Length)
        };
    }
}
