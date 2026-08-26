using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EmbodySense.IntegrationTests.Architecture;

public sealed class ScheduleRuntimeFactoryRunStoreOwnershipTests
{
    [Fact]
    public void Production_schedule_factory_never_constructs_an_independent_custom_loop_run_store()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "EmbodySense.Core.Startup",
            "Triggers",
            "Schedules",
            "ScheduleRuntimeFactory.cs"));

        Assert.Empty(FindCustomLoopRunStoreConstructions(source));
    }

    [Theory]
    [InlineData("new CustomLoopRunStore(paths)")]
    [InlineData("new EmbodySense.Core.Persistence.Loops.CustomLoopRunStore(paths)")]
    [InlineData("new global::EmbodySense.Core.Persistence.Loops.CustomLoopRunStore(\n    paths)")]
    public void Constructor_guard_rejects_reformatted_and_qualified_custom_loop_run_store_construction(string construction)
    {
        var source = """
            internal sealed class ScheduleFactory
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

    private static IReadOnlyList<ObjectCreationExpressionSyntax> FindCustomLoopRunStoreConstructions(string source)
        => CSharpSyntaxTree
            .ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(creation => creation.Type.GetLastToken().ValueText.EndsWith("CustomLoopRunStore", StringComparison.Ordinal))
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
