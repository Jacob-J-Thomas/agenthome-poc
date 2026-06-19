using System.Diagnostics;
using System.Text.Json;
using EmbodySense.Core.Audit;
using EmbodySense.Core.Audit.Models;
using EmbodySense.Core.Inference.Models;
using EmbodySense.Core.Memory;
using EmbodySense.Core.Memory.Models;
using EmbodySense.Core.Workspace;
using EmbodySense.Core.Workspace.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Tests.Cli;

public sealed class CliBehaviorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
        Assert.True(File.Exists(workspace.File(".agent", "memory", "README.md")));
        Assert.True(Directory.Exists(workspace.File(".agent", "memory", "conversations")));
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

    [Fact]
    public async Task Run_command_starts_fresh_conversation_instead_of_restoring_current_transcript()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ConversationMemoryStore(paths);
        await store.AppendMessageAsync(LlmMessage.User("old prompt"));

        var result = await RunCliWithInputAsync("/exit" + Environment.NewLine, "run", "--workdir", workspace.RootPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", await File.ReadAllTextAsync(paths.CurrentConversationPath));
        var archivedPath = Assert.Single(Directory.EnumerateFiles(paths.ArchivedConversationMemoryPath, "*.ndjson"));
        Assert.Contains("old prompt", await File.ReadAllTextAsync(archivedPath));
    }

    [Fact]
    public async Task Run_command_history_command_lists_and_loads_saved_conversation()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var longPrompt = "Alpha prompt for picker " + new string('x', 120) + " hidden suffix";
        await WriteConversationAsync(
            paths,
            "saved-conversation",
            Entry("saved-conversation", 1, "user", longPrompt),
            Entry("saved-conversation", 2, "assistant", "saved answer"));

        var result = await RunCliWithInputAsync("/history" + Environment.NewLine + "1" + Environment.NewLine + "/exit" + Environment.NewLine, "run", "--workdir", workspace.RootPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Stored conversations:", result.Output);
        Assert.Contains("saved-conversation", result.Output);
        Assert.Contains("Alpha prompt for picker", result.Output);
        Assert.Contains("...", result.Output);
        Assert.DoesNotContain("hidden suffix", result.Output);
        Assert.Contains("Loaded conversation `saved-conversation` (2 messages).", result.Output);

        var loadedMessages = await new ConversationMemoryStore(paths).LoadCurrentConversationAsync();
        Assert.Collection(
            loadedMessages,
            message =>
            {
                Assert.StartsWith("Alpha prompt for picker", message.Content);
            },
            message =>
            {
                Assert.Equal("saved answer", message.Content);
            });
    }

    [Fact]
    public async Task Run_command_help_command_lists_harness_commands()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);

        var result = await RunCliWithInputAsync("/help" + Environment.NewLine + "/exit" + Environment.NewLine, "run", "--workdir", workspace.RootPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Harness commands:", result.Output);
        Assert.Contains("/new, /new-session", result.Output);
        Assert.Contains("/history, /conversations, /load", result.Output);
        Assert.Contains("/exit, /quit", result.Output);
    }

    [Fact]
    public async Task Run_command_new_command_starts_fresh_conversation_without_exiting()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteConversationAsync(
            paths,
            "saved-conversation",
            Entry("saved-conversation", 1, "user", "saved prompt"),
            Entry("saved-conversation", 2, "assistant", "saved answer"));

        var result = await RunCliWithInputAsync("/history" + Environment.NewLine + "1" + Environment.NewLine + "/new" + Environment.NewLine + "/exit" + Environment.NewLine, "run", "--workdir", workspace.RootPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Loaded conversation `saved-conversation` (2 messages).", result.Output);
        Assert.Contains("Started a new conversation.", result.Output);
        Assert.Equal("", await File.ReadAllTextAsync(paths.CurrentConversationPath));
        var archivedPath = Assert.Single(Directory.EnumerateFiles(paths.ArchivedConversationMemoryPath, "*.ndjson"));
        Assert.Contains("saved prompt", await File.ReadAllTextAsync(archivedPath));
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

    private static async Task WriteConversationAsync(
        WorkspacePaths paths,
        string conversationId,
        params ConversationMemoryEntry[] entries)
    {
        Directory.CreateDirectory(paths.ConversationMemoryPath);
        var path = Path.Combine(paths.ConversationMemoryPath, conversationId + ".ndjson");
        var lines = entries.Select(entry => JsonSerializer.Serialize(entry, JsonOptions));
        await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private static ConversationMemoryEntry Entry(string conversationId, int sequence, string role, string content)
    {
        return new ConversationMemoryEntry(1, conversationId, sequence, DateTimeOffset.Parse("2026-06-01T00:00:00+00:00").AddMinutes(sequence), role, content);
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);
}
