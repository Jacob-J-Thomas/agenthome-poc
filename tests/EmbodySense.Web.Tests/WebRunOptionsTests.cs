using EmbodySense.Core.Application.Inference.Models;

namespace EmbodySense.Web.Tests;

public sealed class WebRunOptionsTests
{
    [Fact]
    public void FromArguments_uses_localhost_defaults()
    {
        var options = WebRunOptions.FromArguments([]);

        Assert.Equal(WebRunOptions.DefaultHost, options.Host);
        Assert.Equal(WebRunOptions.DefaultPort, options.Port);
        Assert.Equal(Directory.GetCurrentDirectory(), options.WorkingDirectory);
        Assert.Equal($"http://{WebRunOptions.DefaultHost}:{WebRunOptions.DefaultPort}", options.Url);
        Assert.False(options.PrintHelp);
    }

    [Fact]
    public void FromArguments_parses_runtime_options()
    {
        var options = WebRunOptions.FromArguments(["--workdir", "work", "--host", "localhost", "--port", "4567", "--model", "gpt-test", "--codex-path", "codex-test", "--sandbox", "workspace-write"]);
        var inferenceOptions = options.ToInferenceClientOptions();

        Assert.Equal("localhost", options.Host);
        Assert.Equal(4567, options.Port);
        Assert.Equal("http://localhost:4567", options.Url);
        Assert.Equal("work", inferenceOptions.WorkingDirectory);
        Assert.Equal("gpt-test", inferenceOptions.Model);
        Assert.Equal("codex-test", inferenceOptions.CodexExecutablePath);
        Assert.Equal("workspace-write", inferenceOptions.CodexSandbox);
        Assert.Equal(LlmInferenceSurface.OpenAiCodex, inferenceOptions.Surface);
    }

    [Fact]
    public void FromArguments_formats_ipv6_localhost_url()
    {
        var options = WebRunOptions.FromArguments(["--host", "::1", "--port", "4567"]);

        Assert.Equal("http://[::1]:4567", options.Url);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.10")]
    public void FromArguments_rejects_remote_bind_hosts(string host)
    {
        var exception = Assert.Throws<ArgumentException>(() => WebRunOptions.FromArguments(["--host", host]));

        Assert.Contains("only binds to localhost", exception.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("nope")]
    public void FromArguments_rejects_invalid_ports(string port)
    {
        var exception = Assert.Throws<ArgumentException>(() => WebRunOptions.FromArguments(["--port", port]));

        Assert.Contains("Port must be", exception.Message);
    }

    [Fact]
    public void FromArguments_prints_help_without_validating_other_options()
    {
        var options = WebRunOptions.FromArguments(["--help", "--host", "0.0.0.0"]);

        Assert.True(options.PrintHelp);
        Assert.Equal(WebRunOptions.DefaultHost, options.Host);
    }
}
