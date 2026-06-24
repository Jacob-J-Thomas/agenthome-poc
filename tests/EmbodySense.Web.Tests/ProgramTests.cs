using EmbodySense.Tests.Support;
using EmbodySense.Web.Hubs;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EmbodySense.Web.Tests;

public sealed class ProgramTests
{
    [Fact]
    public async Task Main_prints_help_without_starting_server()
    {
        var output = new StringWriter();
        var originalOutput = Console.Out;
        Console.SetOut(output);

        try
        {
            var exitCode = await Program.Main(["--help"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("EmbodySense Web UI", output.ToString());
            Assert.Contains("embodysense-web", output.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    [Fact]
    public async Task ConfigureServices_registers_web_runtime_services()
    {
        using var workspace = new TestWorkspace();
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath]);
        var services = new ServiceCollection();
        services.AddLogging();

        Program.ConfigureServices(services, options);
        await using var provider = services.BuildServiceProvider();

        Assert.NotEmpty(provider.GetRequiredService<WebSessionSecurity>().Token);
        Assert.Empty(provider.GetRequiredService<WebApprovalCoordinator>().GetPending());
        Assert.Equal(workspace.RootPath, provider.GetRequiredService<WebAgentRuntimeHost>().GetStatus().WorkspaceRoot);
        Assert.NotNull(provider.GetRequiredService<IWebClientNotifier>());
        Assert.NotNull(provider.GetRequiredService<IHubContext<WebSessionHub, IWebSessionClient>>());
    }

    [Fact]
    public void ResolveContentRoot_finds_static_web_assets()
    {
        var contentRoot = Program.ResolveContentRoot();

        Assert.True(Directory.Exists(Path.Combine(contentRoot, "wwwroot")));
        Assert.True(File.Exists(Path.Combine(contentRoot, "wwwroot", "index.html")));
    }

    [Fact]
    public void ResolveContentRoot_prefers_base_directory_when_static_assets_are_present()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("wwwroot"));

        var contentRoot = Program.ResolveContentRoot(workspace.RootPath, "fallback");

        Assert.Equal(workspace.RootPath, contentRoot);
    }

    [Fact]
    public void ResolveContentRoot_finds_repo_style_source_project_from_ancestor()
    {
        using var workspace = new TestWorkspace();
        var nestedProject = workspace.File("src", "EmbodySense.Web");
        Directory.CreateDirectory(Path.Combine(nestedProject, "wwwroot"));
        File.WriteAllText(Path.Combine(nestedProject, "EmbodySense.Web.csproj"), "<Project />");

        var contentRoot = Program.ResolveContentRoot(workspace.RootPath, "fallback");

        Assert.Equal(nestedProject, contentRoot);
    }

    [Fact]
    public void ResolveContentRoot_finds_project_directory_from_nested_child()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.File("wwwroot"));
        Directory.CreateDirectory(workspace.File("bin", "Debug"));
        File.WriteAllText(workspace.File("EmbodySense.Web.csproj"), "<Project />");

        var contentRoot = Program.ResolveContentRoot(workspace.File("bin", "Debug"), "fallback");

        Assert.Equal(workspace.RootPath, contentRoot);
    }

    [Fact]
    public void ResolveContentRoot_uses_fallback_when_static_assets_are_missing()
    {
        using var workspace = new TestWorkspace();

        var contentRoot = Program.ResolveContentRoot(workspace.RootPath, "fallback");

        Assert.Equal("fallback", contentRoot);
    }

    [Fact]
    public void PrintHelp_writes_usage()
    {
        var writer = new StringWriter();

        Program.PrintHelp(writer);

        Assert.Contains("usage:", writer.ToString());
        Assert.Contains("--workdir path", writer.ToString());
        Assert.Contains("--host host", writer.ToString());
        Assert.Contains("--port port", writer.ToString());
    }
}
