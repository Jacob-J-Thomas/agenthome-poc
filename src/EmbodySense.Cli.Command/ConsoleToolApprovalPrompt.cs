using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Runtime;

namespace EmbodySense.Cli.Command;

public sealed class ConsoleToolApprovalPrompt : IAgentToolApprovalPrompt
{
    private readonly IAgentRuntimeConsole _client;

    public ConsoleToolApprovalPrompt(IAgentRuntimeConsole? client = null)
    {
        _client = client ?? ConsoleHarnessTerminal.Instance;
    }

    public Task<(bool Approved, string DecisionBy, string Detail)> RequestApprovalAsync(AgentToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        _client.WriteLine();
        _client.WriteLine("Tool approval required");
        _client.WriteLine($"Tool:       {request.Command}");
        _client.WriteLine($"Target:     {request.TargetPath}");
        _client.WriteLine($"Resolved:   {request.ResolvedPath}");
        _client.WriteLine($"Operation:  {request.Operation}");
        _client.WriteLine($"Matched:    {request.MatchedPath}");
        _client.WriteLine($"Reason:     {request.Reason}");
        _client.Write("Approve this tool request? [y/N] ");

        var answer = _client.ReadLine();
        var approved = string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
        var detail = approved ? "Approved at the console approval prompt." : "Rejected at the console approval prompt.";
        return Task.FromResult((approved, "human.console", detail));
    }
}
