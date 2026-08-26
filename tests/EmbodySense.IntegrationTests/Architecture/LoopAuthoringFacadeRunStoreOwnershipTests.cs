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
    public void Production_runtime_factory_transfers_its_exact_canonical_store_to_authoring_and_runtime()
    {
        var factorySource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "EmbodySense.Core.Startup",
            "Runtime",
            "AgentRuntimeFactory.cs"));
        var runtimeSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "EmbodySense.Core.Startup",
            "Runtime",
            "AgentRuntime.cs"));
        var factoryRoot = Parse(factorySource);
        var runtimeRoot = Parse(runtimeSource);
        var canonicalStoreConstruction = Assert.Single(FindCustomLoopRunStoreConstructions(factoryRoot));
        var canonicalStoreAssignment = Assert.IsType<AssignmentExpressionSyntax>(canonicalStoreConstruction.Parent);
        var authoringConstruction = Assert.Single(FindLoopAuthoringFacadeConstructions(factoryRoot));
        var authoringService = Assert.IsType<ObjectCreationExpressionSyntax>(authoringConstruction.ArgumentList!.Arguments[0].Expression);
        var authoringStoreArgument = Assert.Single(authoringService.ArgumentList!.Arguments, argument => argument.NameColon?.Name.Identifier.ValueText == "runStore");
        var runtimeConstruction = Assert.Single(FindObjectCreations(factoryRoot, "AgentRuntime"));
        var runtimeConstructor = Assert.Single(runtimeRoot.DescendantNodes().OfType<ConstructorDeclarationSyntax>(), constructor => constructor.Identifier.ValueText == "AgentRuntime");
        var runtimeStoreParameter = Assert.Single(runtimeConstructor.ParameterList.Parameters, parameter => parameter.Identifier.ValueText == "customRunStore");
        var runtimeStoreParameterIndex = runtimeConstructor.ParameterList.Parameters.IndexOf(runtimeStoreParameter);
        var transfer = Assert.Single(factoryRoot.DescendantNodes().OfType<AssignmentExpressionSyntax>(), assignment =>
            assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "customRunStore" }
            && assignment.Right.RawKind == (int)SyntaxKind.NullLiteralExpression);

        Assert.Equal("customRunStore", GetIdentifierValue(canonicalStoreAssignment.Left));
        Assert.Equal("CustomLoopAuthoringService", authoringService.Type.GetLastToken().ValueText);
        Assert.Equal(GetIdentifierValue(canonicalStoreAssignment.Left), GetIdentifierValue(authoringStoreArgument.Expression));
        Assert.Equal(GetIdentifierValue(canonicalStoreAssignment.Left), GetIdentifierValue(runtimeConstruction.ArgumentList!.Arguments[runtimeStoreParameterIndex].Expression));
        Assert.True(transfer.SpanStart > runtimeConstruction.SpanStart);
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
        var root = Parse(source);
        var runtimeConstructor = Assert.Single(root.DescendantNodes().OfType<ConstructorDeclarationSyntax>(), constructor => constructor.Identifier.ValueText == "AgentRuntime");
        var constructorStoreParameter = Assert.Single(runtimeConstructor.ParameterList.Parameters, parameter => parameter.Identifier.ValueText == "customRunStore");
        var fieldTransfer = Assert.Single(root.DescendantNodes().OfType<AssignmentExpressionSyntax>(), assignment =>
            assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "_customRunStore" }
            && assignment.Right is IdentifierNameSyntax { Identifier.ValueText: "customRunStore" });
        var disposal = Assert.Single(FindCanonicalRunStoreDisposals(root));
        var disposeMethod = Assert.IsType<MethodDeclarationSyntax>(disposal.FirstAncestorOrSelf<MethodDeclarationSyntax>());
        var ownershipFence = Assert.Single(disposeMethod.DescendantNodes().OfType<IfStatementSyntax>(), IsIdempotentDisposalFence);

        Assert.Equal("CustomLoopRunStore", constructorStoreParameter.Type!.GetLastToken().ValueText);
        Assert.True(fieldTransfer.SpanStart > runtimeConstructor.SpanStart);
        Assert.Equal("DisposeAsync", disposeMethod.Identifier.ValueText);
        Assert.True(ownershipFence.SpanStart < disposal.SpanStart);
        Assert.Contains(ownershipFence.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>(), _ => true);
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

    private static CompilationUnitSyntax Parse(string source)
        => CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

    private static IReadOnlyList<ObjectCreationExpressionSyntax> FindCustomLoopRunStoreConstructions(string source)
        => FindCustomLoopRunStoreConstructions(Parse(source));

    private static IReadOnlyList<ObjectCreationExpressionSyntax> FindCustomLoopRunStoreConstructions(CompilationUnitSyntax source)
        => source
            .DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(creation => string.Equals(creation.Type.GetLastToken().ValueText, "CustomLoopRunStore", StringComparison.Ordinal))
            .ToArray();

    private static IReadOnlyList<ObjectCreationExpressionSyntax> FindLoopAuthoringFacadeConstructions(string source)
        => FindLoopAuthoringFacadeConstructions(Parse(source));

    private static IReadOnlyList<ObjectCreationExpressionSyntax> FindLoopAuthoringFacadeConstructions(CompilationUnitSyntax source)
        => source
            .DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(creation => string.Equals(creation.Type.GetLastToken().ValueText, "LoopAuthoringFacade", StringComparison.Ordinal))
            .ToArray();

    private static IReadOnlyList<ObjectCreationExpressionSyntax> FindObjectCreations(CompilationUnitSyntax source, string typeName)
        => source
            .DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(creation => string.Equals(creation.Type.GetLastToken().ValueText, typeName, StringComparison.Ordinal))
            .ToArray();

    private static IReadOnlyList<InvocationExpressionSyntax> FindCanonicalRunStoreDisposals(CompilationUnitSyntax source)
        => source
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Expression is MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.ValueText: "_customRunStore" },
                Name: IdentifierNameSyntax { Identifier.ValueText: "Dispose" }
            })
            .ToArray();

    private static string GetIdentifierValue(ExpressionSyntax expression)
        => Assert.IsType<IdentifierNameSyntax>(expression).Identifier.ValueText;

    private static bool IsIdempotentDisposalFence(IfStatementSyntax statement)
    {
        var exchange = statement.Condition.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().SingleOrDefault(invocation =>
            invocation.Expression is MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.ValueText: "Interlocked" },
                Name: IdentifierNameSyntax { Identifier.ValueText: "Exchange" }
            });
        if (exchange is null)
        {
            return false;
        }

        return exchange.ArgumentList.Arguments.Count == 2
            && exchange.ArgumentList.Arguments[0].RefOrOutKeyword.RawKind == (int)SyntaxKind.RefKeyword
            && exchange.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax { Identifier.ValueText: "_disposed" }
            && exchange.ArgumentList.Arguments[1].Expression is LiteralExpressionSyntax { Token.ValueText: "1" };
    }

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
