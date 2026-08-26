using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EmbodySense.IntegrationTests.Architecture;

public sealed class LoopAuthoringFacadeRunStoreOwnershipTests
{
    [Fact]
    public void Production_authoring_facade_never_constructs_an_independent_custom_loop_run_store()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "EmbodySense.Core.Startup",
            "Loops",
            "LoopAuthoringFacade.cs"));

        Assert.Empty(FindCustomLoopRunStoreConstructions(source));
    }

    [Fact]
    public void Production_web_composition_never_constructs_a_loop_authoring_facade()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "EmbodySense.Web",
            "Program.cs"));

        Assert.Empty(FindLoopAuthoringFacadeConstructions(source));
    }

    [Fact]
    public void Production_runtime_factory_is_the_only_loop_authoring_facade_composition_path()
    {
        var sourceRoot = Path.Combine(FindRepositoryRoot(), "src");
        var constructions = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => FindLoopAuthoringFacadeConstructions(File.ReadAllText(path)).Select(creation => (Path: path, Creation: creation)))
            .ToArray();

        var construction = Assert.Single(constructions);
        Assert.Equal(Path.Combine("EmbodySense.Core.Startup", "Runtime", "AgentRuntimeFactory.cs"), Path.GetRelativePath(sourceRoot, construction.Path));
    }

    [Fact]
    public void Production_runtime_factory_constructs_one_canonical_custom_loop_run_store()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "EmbodySense.Core.Startup",
            "Runtime",
            "AgentRuntimeFactory.cs"));

        Assert.Single(FindCustomLoopRunStoreConstructions(source));
    }

    [Fact]
    public void Production_runtime_is_the_single_disposal_owner_for_its_canonical_custom_loop_run_store()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "EmbodySense.Core.Startup",
            "Runtime",
            "AgentRuntime.cs"));

        Assert.Single(FindCanonicalRunStoreDisposals(source));
    }

    [Theory]
    [InlineData("new CustomLoopRunStore(paths)")]
    [InlineData("new EmbodySense.Core.Persistence.Loops.CustomLoopRunStore(paths)")]
    [InlineData("new global::EmbodySense.Core.Persistence.Loops.CustomLoopRunStore(\n    paths)")]
    public void Constructor_guard_rejects_reformatted_and_qualified_custom_loop_run_store_construction(string construction)
    {
        var source = """
            internal sealed class AuthoringFacade
            {
                internal void Create(object paths)
                {
                    var runStore =
            """
            + construction
            + ";"
            + """
                }
            }
            """;

        Assert.Single(FindCustomLoopRunStoreConstructions(source));
    }

    [Fact]
    public void Constructor_guard_allows_other_types_that_only_end_with_the_protected_name()
    {
        const string Source = """
            internal sealed class AuthoringFacade
            {
                internal void Create(object paths)
                {
                    var runStore = new AlternateCustomLoopRunStore(paths);
                }
            }
            """;

        Assert.Empty(FindCustomLoopRunStoreConstructions(Source));
    }

    private static IReadOnlyList<ObjectCreationExpressionSyntax> FindCustomLoopRunStoreConstructions(string source)
        => CSharpSyntaxTree
            .ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(creation => string.Equals(creation.Type.GetLastToken().ValueText, "CustomLoopRunStore", StringComparison.Ordinal))
            .ToArray();

    private static IReadOnlyList<ObjectCreationExpressionSyntax> FindLoopAuthoringFacadeConstructions(string source)
        => CSharpSyntaxTree
            .ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(creation => string.Equals(creation.Type.GetLastToken().ValueText, "LoopAuthoringFacade", StringComparison.Ordinal))
            .ToArray();

    private static IReadOnlyList<InvocationExpressionSyntax> FindCanonicalRunStoreDisposals(string source)
        => CSharpSyntaxTree
            .ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Expression is MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.ValueText: "_customRunStore" },
                Name: IdentifierNameSyntax { Identifier.ValueText: "Dispose" }
            })
            .ToArray();

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
