using EmbodySense.Core.Startup.Workspace.Models;
using EmbodySense.Cli.Command;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;

namespace EmbodySense.Cli.Command;

/// <summary>
/// Composes and hosts the supported interactive CLI runtime.
/// </summary>
public static class RunCommand
{
    /// <summary>
    /// Validates runtime compatibility, optionally initializes the workspace with confirmation, and starts the console host.
    /// </summary>
    /// <param name="arguments">The complete CLI token sequence, including the root <c>run</c> token.</param>
    /// <returns>Zero after an orderly session exit; one when the user declines required workspace initialization.</returns>
    /// <exception cref="ArgumentException">The run arguments do not provide a nonblank configured model.</exception>
    /// <exception cref="CodexRuntimeUnavailableException">
    /// Runtime resolution is not compatible because the executable is unavailable, its compatibility probe failed, or it does not advertise the requested model.
    /// </exception>
    public static async Task<int> RunAsync(CliArguments arguments)
    {
        var options = RunOptions.FromArguments(arguments);
        var configuredModel = options.Model;
        var codexRuntimeStatus = await new CodexRuntimeStatusReader().ReadAsync(options.CodexExecutablePath, configuredModel);
        if (codexRuntimeStatus.Compatibility != CodexRuntimeCompatibility.Compatible)
        {
            throw new CodexRuntimeUnavailableException(codexRuntimeStatus);
        }

        var client = ConsoleRuntimeTerminal.Instance;
        WriteCodexRuntimeStatus(client, codexRuntimeStatus);
        var status = new WorkspaceStatusReader().Read(options.WorkingDirectory);

        if (!status.IsInitialized)
        {
            if (!ConfirmWorkspaceInitialization(status))
            {
                Console.WriteLine("Workspace initialization cancelled. Run `embodysense init <root>` to initialize explicitly.");
                return 1;
            }

            await WorkspaceInitializer.ForCli().InitializeAsync(options.WorkingDirectory);
        }

        await using var runtime = await new AgentRuntimeFactory(new ConsoleToolApprovalPrompt(client), codexRuntimeStatus).CreateAsync(
            configuredModel,
            options.WorkingDirectory,
            options.CodexExecutablePath,
            options.CodexSandbox,
            AgentRuntimeSurface.Cli);
        return await new AgentRuntimeConsoleHost(runtime, client).RunAsync(banner: Constants.Banner, verbose: options.Verbose);
    }

    private static void WriteCodexRuntimeStatus(IAgentRuntimeConsole client, CodexRuntimeStatus status)
    {
        client.WriteLine($"Codex executable: {status.ResolvedExecutablePath}");
        client.WriteLine($"Codex version: {status.Version ?? "unknown"}");
        client.WriteLine($"Codex model: {status.ConfiguredModel ?? "configured externally"}");
        client.WriteLine($"Codex compatibility: {status.Compatibility}");
    }

    private static bool ConfirmWorkspaceInitialization(WorkspaceStatusSnapshot status)
    {
        Console.WriteLine("Warning: this EmbodySense workspace is not initialized.");
        Console.WriteLine($"Root: {status.RootPath}");
        Console.WriteLine("Initializing will create .agent/ scaffolding and root-level workspace folders with a default permissions policy.");
        Console.Write("Initialize this workspace now? [y/N] ");

        var answer = Console.ReadLine()?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
