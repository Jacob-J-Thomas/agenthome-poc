using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EmbodySense.IntegrationTests.Architecture;

public sealed class AgentRuntimeBackgroundCoordinatorArchitectureTests
{
    [Fact]
    public void Agent_runtime_does_not_expose_or_construct_an_alternate_local_background_coordinator()
    {
        var root = ParseProductionFile("Runtime", "AgentRuntime.cs");
        var declarations = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToArray();
        var constructions = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Select(creation => creation.Type.GetLastToken().ValueText)
            .ToArray();

        Assert.DoesNotContain(declarations, declaration => declaration.Identifier.ValueText == "CreateGovernedLoopLocalBackgroundRuntime");
        Assert.DoesNotContain("GovernedLoopLocalBackgroundRuntimeFactory", constructions);
        Assert.DoesNotContain("GovernedLoopLocalCoordinator", constructions);
    }

    [Fact]
    public void Startup_does_not_publish_a_second_local_background_runtime_factory()
    {
        var startupRoot = Path.Combine(FindRepositoryRoot(), "src", "EmbodySense.Core.Startup", "Loops", "Execution", "Sleep");

        Assert.False(File.Exists(Path.Combine(startupRoot, "GovernedLoopLocalBackgroundRuntimeFactory.cs")));
        Assert.False(File.Exists(Path.Combine(startupRoot, "GovernedLoopLocalBackgroundRuntime.cs")));
    }

    [Fact]
    public void Factory_binds_one_canonical_background_work_composition_before_returning_the_runtime()
    {
        var root = ParseProductionFile("Runtime", "AgentRuntimeFactory.cs");
        var hostConstructions = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(creation => creation.Type.GetLastToken().ValueText == "GovernedLoopBackgroundRuntimeHost")
            .ToArray();
        var bindings = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Expression is MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.ValueText: "governedBackgroundRuntimeHost" },
                Name: IdentifierNameSyntax { Identifier.ValueText: "BindBackgroundWork" }
            })
            .ToArray();
        var runtimeConstruction = Assert.Single(root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>(), creation => creation.Type.GetLastToken().ValueText == "AgentRuntime");

        Assert.Single(hostConstructions);
        var binding = Assert.Single(bindings);
        Assert.True(binding.SpanStart < runtimeConstruction.SpanStart);
    }

    [Fact]
    public void Agent_runtime_exposes_only_typed_non_sensitive_background_lifecycle_contracts()
    {
        var root = ParseProductionFile("Runtime", "AgentRuntime.cs");
        var declarations = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToArray();
        var startupRoot = Path.Combine(FindRepositoryRoot(), "src", "EmbodySense.Core.Startup");
        var modelsRoot = Path.Combine(startupRoot, "Runtime", "Models");

        Assert.Contains(declarations, declaration => declaration.Identifier.ValueText == "StartGovernedLoopLocalBackgroundWithStatusAsync"
            && declaration.ReturnType.ToString().Contains("AgentRuntimeGovernedLoopBackgroundStartResult", StringComparison.Ordinal));
        Assert.Contains(declarations, declaration => declaration.Identifier.ValueText == "ReadGovernedLoopLocalBackgroundStatusAsync"
            && declaration.ReturnType.ToString().Contains("AgentRuntimeGovernedLoopBackgroundStatus", StringComparison.Ordinal));
        Assert.Contains(declarations, declaration => declaration.Identifier.ValueText == "StopGovernedLoopLocalBackgroundAsync"
            && declaration.ReturnType.ToString().Contains("AgentRuntimeGovernedLoopBackgroundStopResult", StringComparison.Ordinal));
        Assert.DoesNotContain(root.DescendantNodes().OfType<IdentifierNameSyntax>(), identifier => identifier.Identifier.ValueText == "GovernedLoopCoordinatorEvidenceStore");

        foreach (var typeName in new[]
        {
            "AgentRuntimeGovernedLoopBackgroundReadiness",
            "AgentRuntimeGovernedLoopBackgroundOwnership",
            "AgentRuntimeGovernedLoopBackgroundStartStatus",
            "AgentRuntimeGovernedLoopBackgroundStartResult",
            "AgentRuntimeGovernedLoopBackgroundStopStatus",
            "AgentRuntimeGovernedLoopBackgroundStopResult",
            "AgentRuntimeGovernedLoopBackgroundStatus",
        })
        {
            Assert.True(File.Exists(Path.Combine(modelsRoot, typeName + ".cs")), $"Missing one-type-per-file Startup model for {typeName}.");
        }
    }

    private static CompilationUnitSyntax ParseProductionFile(params string[] relativePath)
        => (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine([FindRepositoryRoot(), "src", "EmbodySense.Core.Startup", .. relativePath]))).GetRoot();

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
