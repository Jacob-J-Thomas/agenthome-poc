using EmbodySense.Core.Startup.Governance;

namespace EmbodySense.Cli.Command;

/// <summary>
/// Projects governed tool approval requests to an interactive console.
/// </summary>
public sealed class ConsoleToolApprovalPrompt : IAgentToolApprovalPrompt
{
    private readonly IAgentRuntimeConsole _client;

    /// <summary>
    /// Initializes an approval prompt.
    /// </summary>
    /// <param name="client">The console boundary, or <see langword="null"/> to use the process console.</param>
    public ConsoleToolApprovalPrompt(IAgentRuntimeConsole? client = null)
    {
        _client = client ?? ConsoleRuntimeTerminal.Instance;
    }

    /// <summary>
    /// Displays one approval request and accepts only <c>y</c> or <c>yes</c> as approval.
    /// </summary>
    /// <param name="request">The bounded governed request to present.</param>
    /// <param name="cancellationToken">The token checked before prompting.</param>
    /// <returns>The decision, the fixed <c>human.console</c> actor, and an audit-facing detail.</returns>
    /// <exception cref="OperationCanceledException">The token is already cancelled.</exception>
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
