using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EmbodySense.IntegrationTests.Architecture;

public sealed class TestBoundaryGuardTests
{
    private static readonly string[] _forbiddenPrivateAccessTokens =
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

    private static readonly string[] _forbiddenFrontendPrivateAccessTokens =
    [
        "__appTestApi",
        "createApiExport",
        "globalThis.__appTestApi"
    ];

    private static readonly IReadOnlyDictionary<string, string[]> _expectedTestProjectReferences = new Dictionary<string, string[]>
    {
        ["EmbodySense.Tests.Support"] = [],
        ["EmbodySense.Core.Common.Tests"] = ["EmbodySense.Core.Common", "EmbodySense.Tests.Support"],
        ["EmbodySense.Core.Application.Tests"] = ["EmbodySense.Core.Application", "EmbodySense.Core.Common", "EmbodySense.Tests.Support"],
        ["EmbodySense.Core.Clients.Tests"] = ["EmbodySense.CancellationHost", "EmbodySense.Core.Clients", "EmbodySense.Core.Common", "EmbodySense.Tests.Support"],
        ["EmbodySense.Core.Persistence.Tests"] = ["EmbodySense.CancellationHost", "EmbodySense.Core.Application", "EmbodySense.Core.Common", "EmbodySense.Core.Persistence", "EmbodySense.HumanInputContinuationHost", "EmbodySense.Tests.Support"],
        ["EmbodySense.Core.Startup.Tests"] = ["EmbodySense.Core.Application", "EmbodySense.Core.Common", "EmbodySense.Core.Persistence", "EmbodySense.Core.Startup", "EmbodySense.Tests.Support"],
        ["EmbodySense.Cli.Command.Tests"] = ["EmbodySense.Cli.Command", "EmbodySense.Core.Startup", "EmbodySense.Tests.Support"],
        ["EmbodySense.Web.Tests"] = ["EmbodySense.CancellationHost", "EmbodySense.Core.Startup", "EmbodySense.Tests.Support", "EmbodySense.Web"],
        ["EmbodySense.IntegrationTests"] = ["EmbodySense.Cli", "EmbodySense.Cli.Command", "EmbodySense.Core.Application", "EmbodySense.Core.Clients", "EmbodySense.Core.Common", "EmbodySense.Core.Persistence", "EmbodySense.Core.Startup", "EmbodySense.Tests.Support"],
        ["EmbodySense.E2ETests"] = ["EmbodySense.CancellationHost", "EmbodySense.E2EBrowserHost", "EmbodySense.Tests.Support", "EmbodySense.Web"]
    };

    [Fact]
    public void Production_source_does_not_declare_friend_assemblies()
    {
        var root = FindRepositoryRoot();
        var declarations = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadLines(file)
                .Where(line => line.Contains("InternalsVisibleTo", StringComparison.Ordinal))
                .Select(line => $"{NormalizeRepositoryRelativePath(Path.GetRelativePath(root, file))}|{line.Trim()}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(declarations);
        var persistenceConstructionSites = Directory
            .EnumerateFiles(Path.Combine(root, "src", "EmbodySense.Core.Persistence"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => (Line: line, Number: index + 1))
                .Where(item => item.Line.Contains("new CredentialLifecycleService(", StringComparison.Ordinal))
                .Select(item => $"{NormalizeRepositoryRelativePath(Path.GetRelativePath(root, file))}:{item.Number}"))
            .ToArray();

        Assert.Single(persistenceConstructionSites);
        Assert.StartsWith("src/EmbodySense.Core.Persistence/Credentials/CredentialLifecyclePersistenceFactory.cs:", persistenceConstructionSites[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/EmbodySense.Core.Application/Properties/AssemblyInfo.cs", "src/EmbodySense.Core.Application/Properties/AssemblyInfo.cs")]
    [InlineData("src\\EmbodySense.Core.Application\\Properties\\AssemblyInfo.cs", "src/EmbodySense.Core.Application/Properties/AssemblyInfo.cs")]
    [InlineData("src/EmbodySense.Core.Persistence/Credentials/CredentialLifecyclePersistenceFactory.cs", "src/EmbodySense.Core.Persistence/Credentials/CredentialLifecyclePersistenceFactory.cs")]
    [InlineData("src\\EmbodySense.Core.Persistence\\Credentials\\CredentialLifecyclePersistenceFactory.cs", "src/EmbodySense.Core.Persistence/Credentials/CredentialLifecyclePersistenceFactory.cs")]
    public void Repository_relative_source_paths_are_normalized_for_allowlist_and_prefix_comparisons(string sourcePath, string expectedPath)
    {
        Assert.Equal(expectedPath, NormalizeRepositoryRelativePath(sourcePath));
        Assert.StartsWith(expectedPath + ":", $"{NormalizeRepositoryRelativePath(sourcePath)}:123", StringComparison.Ordinal);
    }

    [Fact]
    public void Tests_do_not_use_reflection_or_private_access_shortcuts()
    {
        var root = FindRepositoryRoot();
        var violations = Directory
            .EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(file => IsAuthoredSourceFile(root, file))
            .Where(file => !string.Equals(Path.GetFileName(file), nameof(TestBoundaryGuardTests) + ".cs", StringComparison.Ordinal))
            .SelectMany(file => _forbiddenPrivateAccessTokens
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

        foreach (var item in _expectedTestProjectReferences)
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
            .SelectMany(file => _forbiddenFrontendPrivateAccessTokens
                .Where(token => File.ReadAllText(file).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(root, file)} contains {token}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Public_capability_catalog_store_construction_cannot_override_physical_workspace_identity()
    {
        var root = FindRepositoryRoot();
        var sourcePath = Path.Combine(root, "src", "EmbodySense.Core.Persistence", "Capabilities", "CapabilityCatalogStore.cs");
        var store = CSharpSyntaxTree.ParseText(File.ReadAllText(sourcePath)).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single(declaration => declaration.Identifier.ValueText == "CapabilityCatalogStore");
        var publicConstructorParameters = store.Members
            .OfType<ConstructorDeclarationSyntax>()
            .Where(constructor => constructor.Modifiers.Any(modifier => modifier.RawKind == (int)SyntaxKind.PublicKeyword))
            .SelectMany(constructor => constructor.ParameterList.Parameters)
            .Select(parameter => parameter.Type?.ToString() ?? "")
            .ToArray();

        Assert.DoesNotContain(publicConstructorParameters, type => type.Contains("WorkspaceIdentity", StringComparison.Ordinal) || type.Contains("Func<", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(root, "src", "EmbodySense.Core.Persistence", "Capabilities", "ICapabilityCatalogWorkspaceIdentityProvider.cs")));
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

    private static string NormalizeRepositoryRelativePath(string path) => path.Replace('\\', '/');

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
