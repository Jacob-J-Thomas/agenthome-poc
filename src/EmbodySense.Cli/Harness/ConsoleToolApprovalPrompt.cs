using EmbodySense.Core.Tools;
using EmbodySense.Core.Tools.Models;

namespace EmbodySense.Cli.Harness;

public sealed class ConsoleToolApprovalPrompt : IToolApprovalPrompt
{
    private readonly IHarnessTerminal _terminal;

    public ConsoleToolApprovalPrompt(IHarnessTerminal? terminal = null)
    {
        _terminal = terminal ?? ConsoleHarnessTerminal.Instance;
    }

    public Task<ToolApprovalResponse> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        _terminal.WriteLine();
        _terminal.WriteLine("Tool approval required");
        _terminal.WriteLine($"Tool:       {request.ToolRequest.Command.ToString().ToLowerInvariant()}");
        _terminal.WriteLine($"Target:     {request.ToolRequest.TargetPath}");
        _terminal.WriteLine($"Resolved:   {request.ResolvedPath}");
        _terminal.WriteLine($"Operation:  {request.Operation.ToString().ToLowerInvariant()}");
        _terminal.WriteLine($"Matched:    {FormatMatchedPath(request.PermissionEvaluation.MatchedPath)}");
        _terminal.WriteLine($"Reason:     {request.PermissionEvaluation.Detail}");
        _terminal.Write("Approve this tool request? [y/N] ");

        var answer = _terminal.ReadLine();
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
