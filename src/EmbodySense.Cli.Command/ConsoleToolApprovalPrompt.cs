using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Governance.Tools.Models;
using EmbodySense.Core.Application.Harness;

namespace EmbodySense.Cli.Command;

public sealed class ConsoleToolApprovalPrompt : IToolApprovalPrompt
{
    private readonly IHarnessClient _client;

    public ConsoleToolApprovalPrompt(IHarnessClient? client = null)
    {
        _client = client ?? ConsoleHarnessTerminal.Instance;
    }

    public Task<ToolApprovalResponse> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        _client.WriteLine();
        _client.WriteLine("Tool approval required");
        _client.WriteLine($"Tool:       {request.ToolRequest.Command.ToString().ToLowerInvariant()}");
        _client.WriteLine($"Target:     {request.ToolRequest.TargetPath}");
        _client.WriteLine($"Resolved:   {request.ResolvedPath}");
        _client.WriteLine($"Operation:  {request.Operation.ToString().ToLowerInvariant()}");
        _client.WriteLine($"Matched:    {FormatMatchedPath(request.PermissionEvaluation.MatchedPath)}");
        _client.WriteLine($"Reason:     {request.PermissionEvaluation.Detail}");
        _client.Write("Approve this tool request? [y/N] ");

        var answer = _client.ReadLine();
        var approved = string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
        var response = approved
            ? ToolApprovalResponse.Approve("human.console", "Approved at the console approval prompt.")
            : ToolApprovalResponse.Reject("human.console", "Rejected at the console approval prompt.");

        return Task.FromResult(response);
    }

    private static string FormatMatchedPath(string matchedPath)
    {
        return string.IsNullOrWhiteSpace(matchedPath) ? "(default policy)" : matchedPath;
    }
}
