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
    public async Task Run_command_accepts_options_and_exits_without_inference()
    {
        using var workspace = new TestWorkspace();

        var result = await RunCliWithInputAsync("/exit" + Environment.NewLine, "run", "--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", "unused", "--sandbox", "workspace-write", "--approval", "on-request", "--persist-session", "--skip-git-repo-check");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("EMBODYSENSE HARNESS", result.Output);
        Assert.Equal("", result.Error);
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

    private sealed record CliResult(int ExitCode, string Output, string Error);
}
