using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Application.Governance.Tools;

public sealed class ToolResultRetentionService
{
    private readonly IAuditLog _auditLog;
    private readonly LoopDefinition _loopDefinition;
    private readonly IToolResultRetentionStore _store;

    public ToolResultRetentionService(IAuditLog auditLog, LoopDefinition loopDefinition, IToolResultRetentionStore store)
    {
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(loopDefinition);
        ArgumentNullException.ThrowIfNull(store);

        _auditLog = auditLog;
        _loopDefinition = loopDefinition;
        _store = store;
    }

    public async Task<ToolResult> RetainAsync(ToolResult result, IReadOnlyDictionary<string, object?>? additionalAuditMetadata = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ToolResultRetentionReference retention;
        try
        {
            retention = await _store.RetainAsync(result, _loopDefinition, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            retention = new ToolResultRetentionReference(
                ToolResultRetentionStatus.Unavailable,
                null,
                null,
                result.OutputText.Length,
                null,
                null,
                null,
                0,
                $"Durable full-response retention failed with {exception.GetType().Name}; the model-facing result reports this evidence gap.");
        }

        result = result with { Retention = retention };
        var metadata = CreateAuditMetadata(result, retention);
        if (additionalAuditMetadata is not null)
        {
            foreach (var item in additionalAuditMetadata)
            {
                metadata[item.Key] = item.Value;
            }
        }

        await _auditLog.AppendAsync(AuditEvent.Create(
            AuditSchema.Actors.Tool,
            AuditSchema.Actions.ToolResponseRetain,
            result.ResolvedPath,
            retention.Status == ToolResultRetentionStatus.Retained ? AuditSchema.Outcomes.Succeeded : AuditSchema.Outcomes.Failed,
            retention.Detail,
            metadata), cancellationToken);
        return result;
    }

    private Dictionary<string, object?> CreateAuditMetadata(ToolResult result, ToolResultRetentionReference retention)
    {
        var correlation = result.Request.AuditCorrelation;
        return new Dictionary<string, object?>
        {
            ["request_id"] = result.RequestId,
            ["command"] = ToolCommandFormatter.Format(result.Request.Command),
            ["target_path"] = result.Request.TargetPath,
            ["resolved_path"] = result.ResolvedPath,
            ["tool_request_correlation_id"] = result.Request.CorrelationId,
            ["loop_id"] = correlation?.LoopId ?? _loopDefinition.Id,
            ["role_id"] = correlation?.RoleId ?? _loopDefinition.RoleId,
            ["run_id"] = correlation?.RunId,
            ["definition_version"] = correlation?.DefinitionVersion,
            ["definition_hash"] = correlation?.DefinitionHash,
            ["iteration"] = correlation?.Iteration,
            ["step_id"] = correlation?.StepId,
            ["attempt"] = correlation?.Attempt,
            ["attempt_correlation_id"] = correlation?.AttemptCorrelationId,
            ["retention_status"] = retention.Status.ToString().ToLowerInvariant(),
            ["manifest_path"] = retention.ManifestPath,
            ["content_sha256"] = retention.ContentSha256,
            ["character_count"] = retention.CharacterCount,
            ["utf8_byte_count"] = retention.Utf8ByteCount,
            ["chunk_count"] = retention.ChunkCount,
            ["evicted_artifact_count"] = retention.EvictedArtifactCount
        };
    }
}
