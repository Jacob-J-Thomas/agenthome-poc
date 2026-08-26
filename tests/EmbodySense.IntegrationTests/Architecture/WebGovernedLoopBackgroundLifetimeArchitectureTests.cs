using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EmbodySense.IntegrationTests.Architecture;

public sealed class WebGovernedLoopBackgroundLifetimeArchitectureTests
{
    [Fact]
    public void Web_program_registers_one_process_hosted_service_over_the_singleton_runtime_host()
    {
        var root = ParseWebFile("Program.cs");
        var backgroundRegistrations = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.ToString().Contains("AddSingleton(provider => new WebGovernedLoopBackgroundHostedService", StringComparison.Ordinal))
            .ToArray();
        var hostedServiceRegistrations = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.ToString().Contains("AddSingleton<IHostedService>", StringComparison.Ordinal)
                && invocation.ToString().Contains("WebGovernedLoopBackgroundHostedService", StringComparison.Ordinal))
            .ToArray();

        Assert.Single(backgroundRegistrations);
        Assert.Single(hostedServiceRegistrations);
    }

    [Fact]
    public void Web_background_lifetime_uses_only_the_typed_startup_lifecycle_contract_and_no_store_composition()
    {
        var source = ReadWebFile("WebGovernedLoopBackgroundHostedService.cs");

        Assert.Contains("StartGovernedLoopLocalBackgroundForProcessAsync", source, StringComparison.Ordinal);
        Assert.Contains("ReadGovernedLoopLocalBackgroundForProcessAsync", source, StringComparison.Ordinal);
        Assert.Contains("StopGovernedLoopLocalBackgroundForProcessAsync", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseGovernedLoopLocalBackgroundForProcessAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new AgentRuntimeFactory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new AgentRuntime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new CustomLoopRunStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new TriggerQueue", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new GovernedLoopLocalCoordinator", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Web_runtime_host_pins_the_existing_runtime_before_typed_startup_activation_and_releases_it_once()
    {
        var root = ParseWebFile("WebAgentRuntimeHost.cs");
        var start = Assert.Single(root.DescendantNodes().OfType<MethodDeclarationSyntax>(), method => method.Identifier.ValueText == "StartGovernedLoopLocalBackgroundForProcessAsync");
        var release = Assert.Single(root.DescendantNodes().OfType<MethodDeclarationSyntax>(), method => method.Identifier.ValueText == "ReleaseGovernedLoopLocalBackgroundForProcessAsync");
        var pinnedAssignments = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(assignment => assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "_governedLoopBackgroundRuntimePinned" })
            .ToArray();
        var activation = Assert.Single(start.DescendantNodes().OfType<InvocationExpressionSyntax>(), invocation => invocation.Expression is MemberAccessExpressionSyntax
        {
            Name: IdentifierNameSyntax { Identifier.ValueText: "StartGovernedLoopLocalBackgroundWithStatusAsync" }
        });
        var runtimeComposition = Assert.Single(start.DescendantNodes().OfType<InvocationExpressionSyntax>(), invocation => invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "GetOrCreateRuntimeUnderGateAsync" });
        var releaseDisposal = Assert.Single(release.DescendantNodes().OfType<InvocationExpressionSyntax>(), invocation => invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "DisposeRuntimeUnderGateAsync" });

        Assert.Equal("true", Assert.IsType<LiteralExpressionSyntax>(pinnedAssignments[0].Right).Token.ValueText);
        Assert.Equal("false", Assert.IsType<LiteralExpressionSyntax>(pinnedAssignments[1].Right).Token.ValueText);
        Assert.True(runtimeComposition.SpanStart < activation.SpanStart);
        Assert.True(pinnedAssignments[0].SpanStart < activation.SpanStart);
        Assert.True(releaseDisposal.SpanStart > pinnedAssignments[1].SpanStart);
    }

    private static CompilationUnitSyntax ParseWebFile(string fileName)
        => (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(ReadWebFile(fileName)).GetRoot();

    private static string ReadWebFile(string fileName)
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "EmbodySense.Web", fileName));

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EmbodySense.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
