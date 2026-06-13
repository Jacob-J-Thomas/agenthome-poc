using EmbodySense.Core.Audit;
using EmbodySense.Core.Audit.Models;
using EmbodySense.Core.Permissions;
using EmbodySense.Core.Permissions.Models;
using EmbodySense.Core.Tools.Models;
using EmbodySense.Core.Workspace.Models;

namespace EmbodySense.Core.Tools;

public sealed class ToolBroker : IToolBroker
{
    private readonly WorkspacePaths _paths;
    private readonly ToolPermissionService _permissionService;
    private readonly IToolApprovalPrompt _approvalPrompt;
    private readonly AuditLog _auditLog;

    public ToolBroker(WorkspacePaths paths, ToolPermissionService permissionService, IToolApprovalPrompt approvalPrompt)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(permissionService);
        ArgumentNullException.ThrowIfNull(approvalPrompt);

        _paths = paths;
        _permissionService = permissionService;
        _approvalPrompt = approvalPrompt;
        _auditLog = new AuditLog(paths);
    }

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestId = Guid.NewGuid().ToString("N");
        var check = _permissionService.Evaluate(request);

        await RecordPermissionAsync(requestId, request, check, cancellationToken);

        if (check.Evaluation.Decision == PermissionDecision.Deny)
        {
            await RecordExecutionAsync(requestId, request, check, false, AuditSchema.Outcomes.Denied, new Dictionary<string, object?>(), cancellationToken);
            return new ToolResult(ToolExecutionOutcome.Denied, $"denied: {check.Evaluation.Detail}", requestId, check.ResolvedPath, request);
        }

        var approvedByHuman = false;

        if (check.Evaluation.Decision == PermissionDecision.RequiresApproval)
        {
            var approvalRequest = new ToolApprovalRequest(requestId, request, check.ResolvedPath, check.Operation, check.Evaluation);
            await RecordApprovalRequestAsync(approvalRequest, cancellationToken);
            var approval = await _approvalPrompt.RequestApprovalAsync(approvalRequest, cancellationToken);
            await RecordApprovalDecisionAsync(approvalRequest, approval, cancellationToken);

            if (!approval.Approved)
            {
                await RecordExecutionAsync(requestId, request, check, false, AuditSchema.Outcomes.ApprovalRejected, new Dictionary<string, object?>(), cancellationToken);
                return new ToolResult(ToolExecutionOutcome.ApprovalRejected, $"rejected: {approval.Detail}", requestId, check.ResolvedPath, request);
            }

            approvedByHuman = true;
        }

        return await ExecuteAuthorizedAsync(requestId, request, check, approvedByHuman, cancellationToken);
    }

    private async Task<ToolResult> ExecuteAuthorizedAsync(string requestId, ToolRequest request, ToolPermissionCheck check, bool approvedByHuman, CancellationToken cancellationToken)
    {
        try
        {
            var output = request.Command switch
            {
                ToolCommand.List => await ListAsync(check.ResolvedPath, cancellationToken),
                ToolCommand.Read => await ReadAsync(check.ResolvedPath, cancellationToken),
                ToolCommand.Search => await SearchAsync(check.ResolvedPath, request.Pattern ?? request.Content, cancellationToken),
                ToolCommand.Append => await AppendAsync(check.ResolvedPath, request.Content, cancellationToken),
                ToolCommand.Write => await WriteAsync(check.ResolvedPath, request.Content, cancellationToken),
                ToolCommand.Delete => await DeleteAsync(check.ResolvedPath, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(request), request.Command, "Unsupported tool command.")
            };

            await RecordExecutionAsync(requestId, request, check, approvedByHuman, AuditSchema.Outcomes.Succeeded, output.Metadata, cancellationToken);
            return new ToolResult(ToolExecutionOutcome.Succeeded, output.Text, requestId, check.ResolvedPath, request);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            await RecordExecutionAsync(
                requestId,
                request,
                check,
                approvedByHuman,
                AuditSchema.Outcomes.Failed,
                new Dictionary<string, object?> { ["error_type"] = exception.GetType().Name },
                cancellationToken);
            return new ToolResult(ToolExecutionOutcome.Failed, $"failed: {exception.Message}", requestId, check.ResolvedPath, request);
        }
    }

    private static Task<ToolOutput> ListAsync(string resolvedPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(resolvedPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {resolvedPath}");
        }

        var entries = Directory.EnumerateFileSystemEntries(resolvedPath)
            .OrderBy(path => Directory.Exists(path) ? 0 : 1)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => Directory.Exists(path) ? Path.GetFileName(path) + Path.DirectorySeparatorChar : Path.GetFileName(path))
            .ToList();
        var text = entries.Count == 0 ? "(empty)" : string.Join(Environment.NewLine, entries);
        return Task.FromResult(new ToolOutput(text, new Dictionary<string, object?> { ["entry_count"] = entries.Count }));
    }

    private static async Task<ToolOutput> ReadAsync(string resolvedPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"File not found: {resolvedPath}", resolvedPath);
        }

        var text = await File.ReadAllTextAsync(resolvedPath, cancellationToken);
        return new ToolOutput(text, new Dictionary<string, object?> { ["character_count"] = text.Length });
    }

    private async Task<ToolOutput> SearchAsync(string resolvedPath, string? pattern, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new IOException("Search requires a non-empty pattern.");
        }

        var matches = new List<string>();

        if (File.Exists(resolvedPath))
        {
            await SearchFileAsync(resolvedPath, pattern, matches, cancellationToken);
        }
        else if (Directory.Exists(resolvedPath))
        {
            var options = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
                RecurseSubdirectories = true
            };

            foreach (var file in Directory.EnumerateFiles(resolvedPath, "*", options).Order(StringComparer.OrdinalIgnoreCase))
            {
                await SearchFileAsync(file, pattern, matches, cancellationToken);
            }
        }
        else
        {
            throw new DirectoryNotFoundException($"Search target not found: {resolvedPath}");
        }

        var text = matches.Count == 0 ? "(no matches)" : string.Join(Environment.NewLine, matches);
        return new ToolOutput(text, new Dictionary<string, object?> { ["match_count"] = matches.Count, ["pattern_length"] = pattern.Length });
    }

    private async Task SearchFileAsync(string file, string pattern, List<string> matches, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(file, cancellationToken);
        var displayPath = Path.GetRelativePath(_paths.RootPath, file);

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add($"{displayPath}:{i + 1}: {lines[i]}");
            }
        }
    }

    private static async Task<ToolOutput> AppendAsync(string resolvedPath, string? content, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            throw new IOException("Append requires text content.");
        }

        var directory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.AppendAllTextAsync(resolvedPath, content, cancellationToken);
        return new ToolOutput($"appended {content.Length} characters", new Dictionary<string, object?> { ["character_count"] = content.Length });
    }

    private static async Task<ToolOutput> WriteAsync(string resolvedPath, string? content, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            throw new IOException("Write requires text content.");
        }

        var directory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(resolvedPath, content, cancellationToken);
        return new ToolOutput($"wrote {content.Length} characters", new Dictionary<string, object?> { ["character_count"] = content.Length });
    }

    private static Task<ToolOutput> DeleteAsync(string resolvedPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(resolvedPath))
        {
            File.Delete(resolvedPath);
            return Task.FromResult(new ToolOutput("deleted file", new Dictionary<string, object?> { ["deleted_kind"] = "file" }));
        }

        if (Directory.Exists(resolvedPath))
        {
            Directory.Delete(resolvedPath, recursive: true);
            return Task.FromResult(new ToolOutput("deleted directory", new Dictionary<string, object?> { ["deleted_kind"] = "directory" }));
        }

        throw new FileNotFoundException($"Delete target not found: {resolvedPath}", resolvedPath);
    }

    private Task RecordPermissionAsync(string requestId, ToolRequest request, ToolPermissionCheck check, CancellationToken cancellationToken)
    {
        return AppendAuditAsync(AuditEvent.Create(
            actor: AuditSchema.Actors.Tool,
            action: AuditSchema.Actions.ToolPermissionEvaluate,
            target: check.ResolvedPath,
            outcome: FormatDecision(check.Evaluation.Decision),
            detail: check.Evaluation.Detail,
            metadata: BaseMetadata(requestId, request, check)), cancellationToken);
    }

    private Task RecordApprovalRequestAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        return AppendAuditAsync(AuditEvent.Create(
            actor: AuditSchema.Actors.Tool,
            action: AuditSchema.Actions.ToolApprovalRequest,
            target: request.ResolvedPath,
            outcome: AuditSchema.Outcomes.Requested,
            detail: request.PermissionEvaluation.Detail,
            metadata: BaseMetadata(request.RequestId, request.ToolRequest, request.ResolvedPath, request.Operation, request.PermissionEvaluation.MatchedPath)), cancellationToken);
    }

    private Task RecordApprovalDecisionAsync(ToolApprovalRequest request, ToolApprovalResponse response, CancellationToken cancellationToken)
    {
        var metadata = BaseMetadata(request.RequestId, request.ToolRequest, request.ResolvedPath, request.Operation, request.PermissionEvaluation.MatchedPath);
        metadata["decision_by"] = response.DecisionBy;

        return AppendAuditAsync(AuditEvent.Create(
            actor: AuditSchema.Actors.Tool,
            action: AuditSchema.Actions.ToolApprovalDecision,
            target: request.ResolvedPath,
            outcome: response.Approved ? AuditSchema.Outcomes.Approved : AuditSchema.Outcomes.Rejected,
            detail: response.Detail,
            metadata: metadata), cancellationToken);
    }

    private Task RecordExecutionAsync(
        string requestId,
        ToolRequest request,
        ToolPermissionCheck check,
        bool approvedByHuman,
        string outcome,
        IReadOnlyDictionary<string, object?> executionMetadata,
        CancellationToken cancellationToken)
    {
        var metadata = BaseMetadata(requestId, request, check);
        metadata["approved_by_human"] = approvedByHuman;

        foreach (var item in executionMetadata)
        {
            metadata[item.Key] = item.Value;
        }

        return AppendAuditAsync(AuditEvent.Create(
            actor: AuditSchema.Actors.Tool,
            action: AuditSchema.Actions.ToolExecute,
            target: check.ResolvedPath,
            outcome: outcome,
            detail: $"Executed {FormatCommand(request.Command)} tool request.",
            metadata: metadata), cancellationToken);
    }

    private Task AppendAuditAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        return _auditLog.AppendAsync(auditEvent, cancellationToken);
    }

    private Dictionary<string, object?> BaseMetadata(string requestId, ToolRequest request, ToolPermissionCheck check)
    {
        return BaseMetadata(requestId, request, check.ResolvedPath, check.Operation, check.Evaluation.MatchedPath);
    }

    private Dictionary<string, object?> BaseMetadata(string requestId, ToolRequest request, string resolvedPath, FileSystemOperation operation, string matchedPath)
    {
        return new Dictionary<string, object?>
        {
            ["request_id"] = requestId,
            ["command"] = FormatCommand(request.Command),
            ["target_path"] = request.TargetPath,
            ["resolved_path"] = resolvedPath,
            ["workspace_root"] = _paths.RootPath,
            ["filesystem_operation"] = operation.ToString().ToLowerInvariant(),
            ["matched_path"] = matchedPath
        };
    }

    private static string FormatDecision(PermissionDecision decision)
    {
        return decision switch
        {
            PermissionDecision.Allow => AuditSchema.Outcomes.Allowed,
            PermissionDecision.RequiresApproval => AuditSchema.Outcomes.RequiresApproval,
            PermissionDecision.Deny => AuditSchema.Outcomes.Denied,
            _ => AuditSchema.Outcomes.Unknown
        };
    }

    private static string FormatCommand(ToolCommand command)
    {
        return command.ToString().ToLowerInvariant();
    }

    private sealed record ToolOutput(string Text, IReadOnlyDictionary<string, object?> Metadata);
}
