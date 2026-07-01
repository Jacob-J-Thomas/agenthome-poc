using System.Xml.Linq;

namespace EmbodySense.IntegrationTests.Architecture;

public sealed class ProjectReferenceGuardTests
{
    private static readonly string[] ForbiddenInterfaceCoreNamespaces =
    [
        "EmbodySense.Core.Application",
        "EmbodySense.Core.Clients",
        "EmbodySense.Core.Common",
        "EmbodySense.Core.Persistence"
    ];

    private static readonly string[] ForbiddenCoreSurfaceTokens =
    [
        "IAgentRuntimeConsole",
        "AgentRuntimeConsoleHost",
        "UserPrompt",
        "FormatRestoredConversation"
    ];

    private static readonly IReadOnlyDictionary<string, string[]> ExpectedProductionReferences = new Dictionary<string, string[]>
    {
        ["EmbodySense.Core.Common"] = [],
        ["EmbodySense.Core.Application"] = ["EmbodySense.Core.Common"],
        ["EmbodySense.Core.Clients"] = ["EmbodySense.Core.Application", "EmbodySense.Core.Common"],
        ["EmbodySense.Core.Persistence"] = ["EmbodySense.Core.Application", "EmbodySense.Core.Common"],
        ["EmbodySense.Core.Startup"] = ["EmbodySense.Core.Application", "EmbodySense.Core.Clients", "EmbodySense.Core.Common", "EmbodySense.Core.Persistence"],
        ["EmbodySense.Cli.Command"] = ["EmbodySense.Core.Startup"],
        ["EmbodySense.Cli"] = ["EmbodySense.Cli.Command"],
        ["EmbodySense.Web"] = ["EmbodySense.Core.Startup"]
    };

    [Fact]
    public void Production_project_references_match_the_allowed_dependency_graph()
    {
        var root = FindRepositoryRoot();

        foreach (var item in ExpectedProductionReferences)
        {
            var projectPath = Path.Combine(root, "src", item.Key, item.Key + ".csproj");
            var actual = ReadProjectReferences(projectPath);
            var expected = item.Value.Order(StringComparer.Ordinal).ToArray();

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Web_project_uses_startup_as_its_only_core_api()
    {
        var root = FindRepositoryRoot();
        var webDirectory = Path.Combine(root, "src", "EmbodySense.Web");
        var violations = Directory
            .EnumerateFiles(webDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => ContainsForbiddenCoreReference(file))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Cli_command_project_uses_startup_as_its_only_core_api()
    {
        var root = FindRepositoryRoot();
        var commandDirectory = Path.Combine(root, "src", "EmbodySense.Cli.Command");
        var violations = Directory
            .EnumerateFiles(commandDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => ContainsForbiddenCoreReference(file))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Clients_and_persistence_do_not_reference_application_orchestration()
    {
        var root = FindRepositoryRoot();
        var violations = new[] { "EmbodySense.Core.Clients", "EmbodySense.Core.Persistence" }
            .Select(projectName => Path.Combine(root, "src", projectName))
            .SelectMany(projectDirectory => Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
            .Where(ContainsApplicationOrchestrationReference)
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Core_projects_do_not_define_interface_surface_adapters()
    {
        var root = FindRepositoryRoot();
        var coreDirectories = new[] { "EmbodySense.Core.Application", "EmbodySense.Core.Startup" }
            .Select(projectName => Path.Combine(root, "src", projectName));
        var violations = coreDirectories
            .SelectMany(projectDirectory => Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
            .SelectMany(file => ForbiddenCoreSurfaceTokens
                .Where(token => File.ReadAllText(file).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(root, file)} contains {token}"))
            .ToArray();

        Assert.Empty(violations);
    }

    private static bool ContainsApplicationOrchestrationReference(string file)
    {
        var text = File.ReadAllText(file);
        var forbiddenNamespaces = new[]
        {
            "EmbodySense.Core.Application.Loops.Execution",
            "EmbodySense.Core.Application.Runtime"
        };

        return forbiddenNamespaces.Any(namespaceName => text.Contains(namespaceName, StringComparison.Ordinal));
    }

    private static bool ContainsForbiddenCoreReference(string file)
    {
        var text = File.ReadAllText(file);
        return ForbiddenInterfaceCoreNamespaces.Any(namespaceName => text.Contains(namespaceName, StringComparison.Ordinal));
    }

    private static string[] ReadProjectReferences(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        return document
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => GetReferencedProjectName(include!))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string GetReferencedProjectName(string projectReference)
    {
        return Path.GetFileNameWithoutExtension(projectReference.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
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
