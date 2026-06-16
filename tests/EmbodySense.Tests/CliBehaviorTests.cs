using System.Diagnostics;
using EmbodySense.Core.Audit;
using EmbodySense.Core.Audit.Models;
using EmbodySense.Core.Workspace;
using EmbodySense.Core.Workspace.Models;

namespace EmbodySense.Tests;

public sealed class CliBehaviorTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("HELP")]
    public async Task Help_tokens_print_root_help(string helpToken)
    {
        var result = await RunCliAsync(helpToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("EmbodySense POC CLI", result.Output);
        Assert.Contains("embodysense init [root]", result.Output);
    }

    [Fact]
    public async Task Unknown_command_is_normalized_before_error_output()
    {
        var result = await RunCliAsync("BOGUS");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("unknown command: bogus", result.Error);
        Assert.Contains("EmbodySense POC CLI", result.Output);
    }

    [Fact]
    public async Task Audit_tail_uses_tail_subcommand_root_operand_and_limit_option()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var auditLog = new AuditLog(new WorkspacePaths(workspace.RootPath));
        await auditLog.AppendAsync(AuditEvent.Create("test", "first.extra", "target", "ok", "first event"));
        await auditLog.AppendAsync(AuditEvent.Create("test", "second.extra", "target", "ok", "second event"));

        var result = await RunCliAsync("audit", "tail", workspace.RootPath, "--limit", "1");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("second.extra", result.Output);
        Assert.DoesNotContain("first.extra", result.Output);
    }

    [Fact]
    public async Task Run_command_accepts_app_server_options_and_exits_without_inference()
    {
        using var workspace = new TestWorkspace();

        var result = await RunCliWithInputAsync("y" + Environment.NewLine + "/exit" + Environment.NewLine, "run", "--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", "unused", "--sandbox", "workspace-write");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Initialize this workspace now?", result.Output);
        Assert.Contains("EMBODYSENSE HARNESS", result.Output);
        Assert.Equal("", result.Error);
        Assert.True(File.Exists(workspace.File(".agent", "permissions.json")));
        Assert.Contains("workspace.init", await File.ReadAllTextAsync(workspace.File(".agent", "audit", "events.ndjson")));
    }

    [Fact]
    public async Task Run_command_aborts_uninitialized_workspace_when_initialization_is_not_confirmed()
    {
        using var workspace = new TestWorkspace();

        var result = await RunCliWithInputAsync("n" + Environment.NewLine, "run", "--workdir", workspace.RootPath);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Warning: this EmbodySense workspace is not initialized.", result.Output);
        Assert.Contains("Workspace initialization cancelled.", result.Output);
        Assert.DoesNotContain("EMBODYSENSE HARNESS", result.Output);
        Assert.Equal("", result.Error);
        Assert.False(Directory.Exists(workspace.File(".agent")));
    }

    [Fact]
    public async Task Run_command_does_not_reinitialize_initialized_workspace()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var auditPath = workspace.File(".agent", "audit", "events.ndjson");
        var beforeInitEventCount = CountOccurrences(await File.ReadAllTextAsync(auditPath), "workspace.init");

        var result = await RunCliWithInputAsync("/exit" + Environment.NewLine, "run", "--workdir", workspace.RootPath);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("Initialize this workspace now?", result.Output);
        Assert.Equal(beforeInitEventCount, CountOccurrences(await File.ReadAllTextAsync(auditPath), "workspace.init"));
    }

    [Theory]
    [InlineData("--persist-session")]
    [InlineData("--approval")]
    [InlineData("--skip-git-repo-check")]
    public async Task Run_command_rejects_removed_codex_exec_options(string removedOption)
    {
        using var workspace = new TestWorkspace();

        var result = await RunCliWithInputAsync("/exit" + Environment.NewLine, "run", "--workdir", workspace.RootPath, removedOption);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"unsupported run option: {removedOption}", result.Error);
    }

    private static async Task<CliResult> RunCliAsync(params string[] arguments)
    {
        var cliPath = Path.Combine(AppContext.BaseDirectory, "EmbodySense.Cli.dll");
        Assert.True(File.Exists(cliPath), $"Expected CLI assembly at {cliPath}.");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(cliPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("CLI process did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CliResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static async Task<CliResult> RunCliWithInputAsync(string standardInput, params string[] arguments)
    {
        var cliPath = Path.Combine(AppContext.BaseDirectory, "EmbodySense.Cli.dll");
        Assert.True(File.Exists(cliPath), $"Expected CLI assembly at {cliPath}.");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(cliPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("CLI process did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(standardInput);
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();
        await process.WaitForExitAsync();

        return new CliResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static int CountOccurrences(string text, string value)
    {
        return text.Split(value, StringSplitOptions.None).Length - 1;
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);
}
