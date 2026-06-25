using System.Globalization;
using System.Text.Json;
using EmbodySense.Cli.Command;
using EmbodySense.Cli.Command.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Cli.Command.Tests;

public sealed class CliCommandTests
{
    [Fact]
    public async Task InitCommand_initializes_workspace_and_prints_paths()
    {
        using var workspace = new TestWorkspace();

        var result = await CaptureAsync(() => InitCommand.RunAsync(new CliArguments(["init", workspace.RootPath])));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Initialized EmbodySense workspace at", result.Output, StringComparison.Ordinal);
        Assert.Contains("Permissions:", result.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(workspace.File(".agent", "permissions.json")));
        Assert.Contains("embodysense.cli", await File.ReadAllTextAsync(workspace.File(".agent", "audit", "events.ndjson")));
        Assert.Equal("", result.Error);
    }

    [Fact]
    public async Task StatusCommand_reports_initialized_workspace_and_policy_summary()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);

        var result = Capture(() => StatusCommand.Run(new CliArguments(["status", workspace.RootPath])));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Initialized:   True", result.Output, StringComparison.Ordinal);
        Assert.Contains("Default access:", result.Output, StringComparison.Ordinal);
        Assert.Contains("Approved:", result.Output, StringComparison.Ordinal);
        Assert.Contains("Denied:", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusCommand_returns_two_for_uninitialized_workspace()
    {
        using var workspace = new TestWorkspace();

        var result = Capture(() => StatusCommand.Run(new CliArguments(["status", workspace.RootPath])));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Initialized:   False", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditCommand_prints_help_and_rejects_reserved_subcommands()
    {
        var help = await CaptureAsync(() => AuditCommand.RunAsync(new CliArguments(["audit", "--help"])));
        var rejected = await CaptureAsync(() => AuditCommand.RunAsync(new CliArguments(["audit", "summary"])));

        Assert.Equal(0, help.ExitCode);
        Assert.Contains("EmbodySense audit commands", help.Output, StringComparison.Ordinal);
        Assert.Equal(1, rejected.ExitCode);
        Assert.Contains("unknown audit command: summary", rejected.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditCommand_prints_tail_events_and_metadata_values()
    {
        using var workspace = new TestWorkspace();
        var auditDirectory = workspace.File(".agent", "audit");
        Directory.CreateDirectory(auditDirectory);
        await File.WriteAllTextAsync(Path.Combine(auditDirectory, "events.ndjson"), JsonSerializer.Serialize(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            actor = "test",
            action = "metadata.event",
            target = "target",
            outcome = "ok",
            detail = "has metadata",
            metadata = new Dictionary<string, object?>
            {
                ["flag"] = true,
                ["count"] = 3,
                ["name"] = "sample"
            }
        }) + Environment.NewLine);

        var result = await CaptureAsync(() => AuditCommand.RunAsync(new CliArguments(["audit", workspace.RootPath, "--limit", "1"])));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("metadata.event", result.Output, StringComparison.Ordinal);
        Assert.Contains("flag: true", result.Output, StringComparison.Ordinal);
        Assert.Contains("count: 3", result.Output, StringComparison.Ordinal);
        Assert.Contains("name: sample", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditCommand_reports_empty_log()
    {
        using var workspace = new TestWorkspace();

        var result = await CaptureAsync(() => AuditCommand.RunAsync(new CliArguments(["audit", workspace.RootPath])));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("No audit events found", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpCommand_prints_root_usage()
    {
        var result = Capture(() =>
        {
            HelpCommand.PrintRoot();
            return 0;
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("EmbodySense POC CLI", result.Output, StringComparison.Ordinal);
        Assert.Contains("embodysense run", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void RunOptions_parses_supported_options_and_rejects_removed_flags()
    {
        var options = RunOptions.FromArguments(new CliArguments(["run", "--workdir", "work", "--model", "gpt-test", "--codex-path", "codex-test", "--sandbox", "workspace-write", "--verbose"]));

        Assert.Equal("gpt-test", options.Model);
        Assert.Equal("work", options.WorkingDirectory);
        Assert.Equal("codex-test", options.CodexExecutablePath);
        Assert.Equal("workspace-write", options.CodexSandbox);
        Assert.True(options.Verbose);
        Assert.Throws<ArgumentException>(() => RunOptions.FromArguments(new CliArguments(["run", "--persist-session"])));
    }

    [Fact]
    public void RunOptions_rejects_missing_option_values_and_unknown_sandbox_modes()
    {
        Assert.Throws<ArgumentException>(() => RunOptions.FromArguments(new CliArguments(["run", "--workdir", "--model"])));
        var exception = Assert.Throws<ArgumentException>(() => RunOptions.FromArguments(new CliArguments(["run", "--sandbox", "loose"])));

        Assert.Contains("unsupported sandbox mode", exception.Message);
    }

    [Fact]
    public void CliArguments_finds_operands_options_and_flags()
    {
        var arguments = new CliArguments(["audit", "tail", "--limit", "5", "root"]);

        Assert.Equal("audit", arguments.Command);
        Assert.True(arguments.IsTokenAt(1, "TAIL"));
        Assert.True(arguments.IsAnyTokenAt(1, "show", "tail"));
        Assert.Equal("5", arguments.OptionValue("--limit"));
        Assert.True(CliArguments.IsOption("--limit"));
        Assert.Equal("root", arguments.FirstOperand(1, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tail" }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--limit" }));
    }

    private static ConsoleResult Capture(Func<int> action)
    {
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = action();
            return new ConsoleResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
        }
    }

    private static async Task<ConsoleResult> CaptureAsync(Func<Task<int>> action)
    {
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await action();
            return new ConsoleResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
        }
    }

    private sealed record ConsoleResult(int ExitCode, string Output, string Error);
}
