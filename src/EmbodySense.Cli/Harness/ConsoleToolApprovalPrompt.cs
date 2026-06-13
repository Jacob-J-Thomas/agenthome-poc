using EmbodySense.Core.Tools;
using EmbodySense.Core.Tools.Models;

namespace EmbodySense.Cli.Harness;

internal sealed class ConsoleToolApprovalPrompt : IToolApprovalPrompt
{
    public Task<ToolApprovalResponse> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        Console.WriteLine();
        Console.WriteLine("Tool approval required");
        Console.WriteLine($"Tool:       {request.ToolRequest.Command.ToString().ToLowerInvariant()}");
        Console.WriteLine($"Target:     {request.ToolRequest.TargetPath}");
        Console.WriteLine($"Resolved:   {request.ResolvedPath}");
        Console.WriteLine($"Operation:  {request.Operation.ToString().ToLowerInvariant()}");
        Console.WriteLine($"Matched:    {FormatMatchedPath(request.PermissionEvaluation.MatchedPath)}");
        Console.WriteLine($"Reason:     {request.PermissionEvaluation.Detail}");
        Console.Write("Approve this tool request? [y/N] ");

        var answer = Console.ReadLine();
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
