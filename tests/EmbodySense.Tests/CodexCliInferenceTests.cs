using System.Diagnostics;
using EmbodySense.Core.Inference.Implementations;
using EmbodySense.Core.Inference.Interfaces;
using EmbodySense.Core.Inference.Models;

namespace EmbodySense.Tests;

public sealed class CodexCliInferenceTests
{
    [Fact]
    public async Task GenerateAsync_sends_formatted_prompt_and_codex_arguments_to_process_runner()
    {
        using var workspace = new TestWorkspace();
        var runner = new RecordingCodexProcessRunner(new CodexCliProcessResult(0, "fake response" + Environment.NewLine, ""));
        var client = new LlmInferenceClient(new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.OpenAiCodex,
            Model = "gpt-5",
            WorkingDirectory = workspace.RootPath,
            CodexExecutablePath = "custom-codex",
            CodexSandbox = "workspace-write",
            CodexApprovalPolicy = "on-request",
            UseEphemeralCodexSession = false,
            SkipCodexGitRepositoryCheck = true
        }, runner);
        var request = new LlmInferenceRequest(
        [
            LlmMessage.System("system prompt"),
            LlmMessage.User("hello"),
            LlmMessage.Assistant("hi")
        ]);

        var response = await client.GenerateAsync(request);

        Assert.Equal("fake response", response.OutputText);
        Assert.NotNull(runner.StartInfo);
        Assert.Equal("custom-codex", runner.StartInfo.FileName);
        Assert.Equal(workspace.RootPath, runner.StartInfo.WorkingDirectory);
        Assert.True(runner.StartInfo.RedirectStandardInput);
        Assert.True(runner.StartInfo.RedirectStandardOutput);
        Assert.True(runner.StartInfo.RedirectStandardError);
        Assert.False(runner.StartInfo.UseShellExecute);
        Assert.True(runner.StartInfo.CreateNoWindow);
        Assert.Equal(
            ["--ask-for-approval", "on-request", "exec", "--sandbox", "workspace-write", "--skip-git-repo-check", "--model", "gpt-5", "-"],
            runner.StartInfo.ArgumentList);
        Assert.Equal(string.Join(Environment.NewLine, [
            "System:",
            "system prompt",
            "",
            "User:",
            "hello",
            "",
            "Assistant:",
            "hi"
        ]), runner.StandardInput);
    }

    private sealed class RecordingCodexProcessRunner(CodexCliProcessResult result) : ICodexCliProcessRunner
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public string? StandardInput { get; private set; }

        public Task<CodexCliProcessResult> RunAsync(ProcessStartInfo startInfo, string standardInput, CancellationToken cancellationToken = default)
        {
            StartInfo = startInfo;
            StandardInput = standardInput;
            return Task.FromResult(result);
        }
    }
}
