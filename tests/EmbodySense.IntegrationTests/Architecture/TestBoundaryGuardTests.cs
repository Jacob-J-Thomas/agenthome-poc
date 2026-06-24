using System.Xml.Linq;

namespace EmbodySense.IntegrationTests.Architecture;

public sealed class TestBoundaryGuardTests
{
    private static readonly string[] ForbiddenPrivateAccessTokens =
    [
        "InternalsVisibleTo",
        "System.Reflection",
        "BindingFlags",
        "NonPublic",
        "PrivateObject",
        "PrivateType",
        "GetMethod(",
        "GetField(",
        "GetConstructor(",
        "GetConstructors("
    ];

    private static readonly string[] ForbiddenFrontendPrivateAccessTokens =
    [
        "__appTestApi",
        "createApiExport",
        "globalThis.__appTestApi"
    ];

    private static readonly IReadOnlyDictionary<string, string[]> ExpectedTestProjectReferences = new Dictionary<string, string[]>
    {
        ["EmbodySense.Tests.Support"] = [],
        ["EmbodySense.Core.Common.Tests"] = ["EmbodySense.Core.Common", "EmbodySense.Tests.Support"],
        ["EmbodySense.Core.Application.Tests"] = ["EmbodySense.Core.Application", "EmbodySense.Core.Common", "EmbodySense.Tests.Support"],
        ["EmbodySense.Core.Clients.Tests"] = ["EmbodySense.Core.Clients", "EmbodySense.Core.Common", "EmbodySense.Tests.Support"],
        ["EmbodySense.Core.Persistence.Tests"] = ["EmbodySense.Core.Application", "EmbodySense.Core.Common", "EmbodySense.Core.Persistence", "EmbodySense.Tests.Support"],
        ["EmbodySense.Core.Startup.Tests"] = ["EmbodySense.Core.Application", "EmbodySense.Core.Common", "EmbodySense.Core.Persistence", "EmbodySense.Core.Startup", "EmbodySense.Tests.Support"],
        ["EmbodySense.Cli.Command.Tests"] = ["EmbodySense.Cli.Command", "EmbodySense.Core.Startup", "EmbodySense.Tests.Support"],
        ["EmbodySense.Web.Tests"] = ["EmbodySense.Core.Startup", "EmbodySense.Tests.Support", "EmbodySense.Web"],
        ["EmbodySense.IntegrationTests"] = ["EmbodySense.Cli", "EmbodySense.Cli.Command", "EmbodySense.Core.Application", "EmbodySense.Core.Clients", "EmbodySense.Core.Common", "EmbodySense.Core.Persistence", "EmbodySense.Core.Startup", "EmbodySense.Tests.Support"],
        ["EmbodySense.E2ETests"] = ["EmbodySense.Tests.Support", "EmbodySense.Web"]
    };

    [Fact]
    public void Source_does_not_expose_friend_assemblies_for_tests()
    {
        var root = FindRepositoryRoot();
        var violations = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("InternalsVisibleTo", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Tests_do_not_use_reflection_or_private_access_shortcuts()
    {
        var root = FindRepositoryRoot();
        var violations = Directory
            .EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(file => IsAuthoredSourceFile(root, file))
            .Where(file => !string.Equals(Path.GetFileName(file), nameof(TestBoundaryGuardTests) + ".cs", StringComparison.Ordinal))
            .SelectMany(file => ForbiddenPrivateAccessTokens
                .Where(token => File.ReadAllText(file).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(root, file)} contains {token}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Test_projects_do_not_reference_compiled_production_assemblies()
    {
        var root = FindRepositoryRoot();
        var violations = Directory
            .EnumerateFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(ReadCompiledAssemblyReferences)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Test_project_references_match_their_intended_layer()
    {
        var root = FindRepositoryRoot();

        foreach (var item in ExpectedTestProjectReferences)
        {
            var projectPath = Path.Combine(root, "tests", item.Key, item.Key + ".csproj");
            var actual = ReadProjectReferences(projectPath);
            var expected = item.Value.Order(StringComparer.Ordinal).ToArray();

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Frontend_tests_do_not_export_private_script_scope()
    {
        var root = FindRepositoryRoot();
        var frontendTestPath = Path.Combine(root, "tests", "frontend");
        var violations = Directory
            .EnumerateFiles(frontendTestPath, "*.mjs", SearchOption.AllDirectories)
            .SelectMany(file => ForbiddenFrontendPrivateAccessTokens
                .Where(token => File.ReadAllText(file).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(root, file)} contains {token}"))
            .ToArray();

        Assert.Empty(violations);
    }

    private static IEnumerable<string> ReadCompiledAssemblyReferences(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        foreach (var reference in document.Descendants("Reference"))
        {
            var include = reference.Attribute("Include")?.Value ?? "";
            if (include.StartsWith("System.", StringComparison.Ordinal) || include.StartsWith("Microsoft.", StringComparison.Ordinal))
            {
                continue;
            }

            yield return $"{Path.GetFileName(projectPath)} references compiled assembly {include}";
        }
    }

    private static string[] ReadProjectReferences(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        return document
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsAuthoredSourceFile(string root, string file)
    {
        var segments = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !segments.Any(segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EmbodySense.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
