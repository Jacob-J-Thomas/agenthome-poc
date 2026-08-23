using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Inference;
using System.Diagnostics;
using System.Text.Json;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Common.Memory.Models;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.IntegrationTests.Cli;

public sealed class CliBehaviorTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

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
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
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
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);

        var result = await RunCliWithInputUsingTrustRootAsync("y" + Environment.NewLine + "/exit" + Environment.NewLine, workspace.ServerStatePath, "run", "--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", codexPath, "--sandbox", "workspace-write");

        Assert.True(result.ExitCode == 0, $"CLI failed with exit code {result.ExitCode}. Output: {result.Output} Error: {result.Error}");
        Assert.Contains("Initialize this workspace now?", result.Output);
        Assert.Contains($"Codex executable: {Path.GetFullPath(codexPath)}", result.Output);
        Assert.Contains("Codex version: codex-cli integration-test", result.Output);
        Assert.Contains("Codex model: gpt-test", result.Output);
        Assert.Contains("Codex compatibility: Compatible", result.Output);
        Assert.Contains("EMBODYSENSE HARNESS", result.Output);
        Assert.Equal("", result.Error);
        Assert.True(File.Exists(workspace.File(".agent", "permissions.json")));
        Assert.True(File.Exists(workspace.File(".agent", "memory", "README.md")));
        Assert.True(Directory.Exists(workspace.File(".agent", "memory", "conversations")));
        var auditText = await File.ReadAllTextAsync(workspace.File(".agent", "audit", "events.ndjson"));
        Assert.Contains("workspace.init", auditText);
        Assert.Contains("embodysense.cli", auditText);
    }

    [Fact]
    public async Task Run_command_aborts_uninitialized_workspace_when_initialization_is_not_confirmed()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);

        var result = await RunCliWithInputAsync("n" + Environment.NewLine, "run", "--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", codexPath);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Warning: this EmbodySense workspace is not initialized.", result.Output);
        Assert.Contains("Workspace initialization cancelled.", result.Output);
        Assert.DoesNotContain("EMBODYSENSE HARNESS", result.Output);
        Assert.Equal("", result.Error);
        Assert.False(Directory.Exists(workspace.File(".agent")));
    }

    [Fact]
    public async Task Run_command_reports_incompatible_executable_model_before_initialization()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace, advertiseConfiguredModel: false);

        var result = await RunCliWithInputAsync("", "run", "--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", codexPath);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Contains(Path.GetFullPath(codexPath), result.Error);
        Assert.Contains("codex-cli integration-test", result.Error);
        Assert.Contains("gpt-test", result.Error);
        Assert.Contains("Update Codex", result.Error);
        Assert.False(Directory.Exists(workspace.File(".agent")));
    }

    [Fact]
    public async Task Run_command_rejects_a_missing_model_before_runtime_resolution()
    {
        using var workspace = new TestWorkspace();

        var result = await RunCliAsync("run", "--workdir", workspace.RootPath);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Contains("nonblank configured model", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(workspace.File(".agent")));
    }

    [Fact]
    public async Task Run_command_does_not_reinitialize_initialized_workspace()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var auditPath = workspace.File(".agent", "audit", "events.ndjson");
        var beforeInitEventCount = CountOccurrences(await File.ReadAllTextAsync(auditPath), "workspace.init");
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);

        var result = await RunCliWithInputAsync("/exit" + Environment.NewLine, "run", "--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", codexPath);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("Initialize this workspace now?", result.Output);
        Assert.Equal(beforeInitEventCount, CountOccurrences(await File.ReadAllTextAsync(auditPath), "workspace.init"));
    }

    [Fact]
    public async Task Run_command_starts_fresh_conversation_instead_of_restoring_current_transcript()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ConversationMemoryStore(paths);
        await store.AppendMessageAsync(LlmMessage.User("old prompt"));
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);

        var result = await RunCliWithInputAsync("/exit" + Environment.NewLine, "run", "--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", codexPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", await File.ReadAllTextAsync(paths.CurrentConversationPath));
        var archivedPath = Assert.Single(Directory.EnumerateFiles(paths.ArchivedConversationMemoryPath, "*.ndjson"));
        Assert.Contains("old prompt", await File.ReadAllTextAsync(archivedPath));
    }

    [Fact]
    public async Task Run_command_history_command_lists_and_loads_saved_conversation()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var longPrompt = "Alpha prompt for picker " + new string('x', 120) + " hidden suffix";
        await WriteConversationAsync(
            paths,
            "saved-conversation",
            Entry("saved-conversation", 1, "user", longPrompt),
            Entry("saved-conversation", 2, "assistant", "saved answer"));
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);

        var result = await RunCliWithInputAsync("/history" + Environment.NewLine + "1" + Environment.NewLine + "/exit" + Environment.NewLine, "run", "--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", codexPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Stored conversations:", result.Output);
        Assert.Contains("saved-conversation", result.Output);
        Assert.Contains("Alpha prompt for picker", result.Output);
        Assert.Contains("...", result.Output);
        Assert.Contains("hidden suffix", result.Output);
        Assert.Contains("Loaded conversation transcript:", result.Output);
        Assert.Contains("Assistant:", result.Output);
        Assert.Contains("saved answer", result.Output);
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
    public async Task Run_command_help_command_lists_runtime_commands()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);

        var result = await RunCliWithInputAsync("/help" + Environment.NewLine + "/exit" + Environment.NewLine, "run", "--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", codexPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Runtime commands:", result.Output);
        Assert.Contains("/new, /new-session", result.Output);
        Assert.Contains("/history, /conversations, /load", result.Output);
        Assert.Contains("/exit, /quit", result.Output);
    }

    [Fact]
    public async Task Run_command_new_command_starts_fresh_conversation_without_exiting()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteConversationAsync(
            paths,
            "saved-conversation",
            Entry("saved-conversation", 1, "user", "saved prompt"),
            Entry("saved-conversation", 2, "assistant", "saved answer"));
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);

        var result = await RunCliWithInputAsync("/history" + Environment.NewLine + "1" + Environment.NewLine + "/new" + Environment.NewLine + "/exit" + Environment.NewLine, "run", "--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", codexPath);

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

        var result = await RunCliAsync("run", "--workdir", workspace.RootPath, removedOption);

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
        return await RunCliWithInputAsync(standardInput, null, arguments);
    }

    private static async Task<CliResult> RunCliWithInputUsingTrustRootAsync(string standardInput, string capabilityCatalogTrustRoot, params string[] arguments)
    {
        return await RunCliWithInputAsync(standardInput, capabilityCatalogTrustRoot, arguments);
    }

    private static async Task<CliResult> RunCliWithInputAsync(string standardInput, string? capabilityCatalogTrustRoot, string[] arguments)
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

        if (capabilityCatalogTrustRoot is not null)
        {
            startInfo.Environment[FileCapabilityCatalogTrustProvider.DefaultRootEnvironmentVariable] = capabilityCatalogTrustRoot;
        }

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

    private static async Task<string> CreateFakeCodexExecutableAsync(TestWorkspace workspace, bool advertiseConfiguredModel = true)
    {
        var scriptPath = workspace.File("fake-cli-codex.js");
        var commandPath = workspace.File(OperatingSystem.IsWindows() ? "fake-cli-codex.cmd" : "fake-cli-codex");
        var advertisedModel = advertiseConfiguredModel ? "gpt-test" : "older-model";
        await File.WriteAllTextAsync(scriptPath, $$"""
            if (process.argv.slice(2).includes("--version")) {
              process.stdout.write("codex-cli integration-test\n");
              process.exit(0);
            }

            const readline = require("node:readline");
            const advertisedModel = {{JsonSerializer.Serialize(advertisedModel)}};
            const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });

            function write(value) {
              process.stdout.write(`${JSON.stringify(value)}\n`);
            }

            input.on("line", (line) => {
              const message = JSON.parse(line);
              switch (message.method) {
                case "initialize":
                  write({ id: message.id, result: {} });
                  break;
                case "model/list":
                  write({ id: message.id, result: { data: [{ id: advertisedModel, model: advertisedModel }] } });
                  break;
                case "thread/start": {
                  const model = String(message.params?.model ?? "");
                  const modelProvider = String(message.params?.modelProvider ?? "");
                  write({
                    id: message.id,
                    result: {
                      model,
                      modelProvider,
                      thread: { id: "thread-cli-probe", modelProvider }
                    }
                  });
                  break;
                }
                default:
                  break;
                }
            });
            """);
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(commandPath, """
                @echo off
                node "%~dp0fake-cli-codex.js" %*
                """);
        }
        else
        {
            await File.WriteAllTextAsync(commandPath, """
                #!/bin/sh
                exec node "$(dirname "$0")/fake-cli-codex.js" "$@"
                """);
            File.SetUnixFileMode(commandPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return commandPath;
    }

    private static async Task WriteConversationAsync(
        WorkspacePaths paths,
        string conversationId,
        params ConversationMemoryEntry[] entries)
    {
        Directory.CreateDirectory(paths.ConversationMemoryPath);
        var path = Path.Combine(paths.ConversationMemoryPath, conversationId + ".ndjson");
        var lines = entries.Select(entry => JsonSerializer.Serialize(entry, _jsonOptions));
        await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private static ConversationMemoryEntry Entry(string conversationId, int sequence, string role, string content)
    {
        return new ConversationMemoryEntry(1, conversationId, sequence, DateTimeOffset.Parse("2026-06-01T00:00:00+00:00").AddMinutes(sequence), $"message-{sequence}", $"publication-{sequence}", role, content);
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);
}
