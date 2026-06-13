using System.Diagnostics;
using EmbodySense.Core.Audit;
using EmbodySense.Core.Inference.Interfaces;
using EmbodySense.Core.Inference.Models;

namespace EmbodySense.Core.Inference.Implementations;

public sealed class LlmInferenceClient : ILlmInferenceClient
{
    private readonly LlmInferenceClientOptions _options;
    private readonly ILlmInferenceClient _innerClient;
    private readonly AuditLog? _auditLog;

    public LlmInferenceClient(LlmInferenceClientOptions options, ICodexCliProcessRunner? codexProcessRunner = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _innerClient = LlmInferenceClientFactory.CreateProvider(options, codexProcessRunner);
        _auditLog = AuditLog.TryCreateForExistingWorkspace(options.WorkingDirectory);
    }

    public async Task<LlmInferenceResponse> GenerateAsync(LlmInferenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();

        await RecordInferenceStartedAsync(requestId, request, cancellationToken);

        try
        {
            var response = await _innerClient.GenerateAsync(request, cancellationToken);
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

    private Task RecordInferenceStartedAsync(string requestId, LlmInferenceRequest request, CancellationToken cancellationToken)
    {
        return AppendAuditAsync(AuditEvent.Create(
            actor: "embodysense.llm",
            action: "llm.inference.start",
            target: _options.Surface.ToString(),
            outcome: "started",
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
            actor: "embodysense.llm",
            action: "llm.inference.complete",
            target: response.Surface.ToString(),
            outcome: "succeeded",
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
            actor: "embodysense.llm",
            action: "llm.inference.complete",
            target: _options.Surface.ToString(),
            outcome: "failed",
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
