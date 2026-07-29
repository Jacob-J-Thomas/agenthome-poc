using System.Text.RegularExpressions;

namespace EmbodySense.IntegrationTests.Architecture;

public sealed class ModelSourceLayoutTests
{
    [Fact]
    public void LocalWorkspace_model_files_use_a_models_namespace()
    {
        var root = FindRepositoryRoot();
        var modelRoot = Path.Combine(root, "src", "EmbodySense.Core.Clients", "LocalWorkspace", "Models");
        var violations = Directory
            .EnumerateFiles(modelRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !HasModelsNamespace(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        // TODO(https://github.com/Jacob-J-Thomas/agenthome-poc/issues/85): Expand this guard assembly by assembly as each model namespace slice is migrated.
        Assert.Empty(violations);
    }

    [Fact]
    public void Model_files_do_not_own_comparer_behavior()
    {
        var root = FindRepositoryRoot();
        var sourceRoot = Path.Combine(root, "src");
        var violations = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => IsModelFile(sourceRoot, file))
            .Where(file => ComparerTypePattern.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        Assert.Empty(violations);
    }

    private static bool IsModelFile(string sourceRoot, string file)
    {
        var relativePath = Path.GetRelativePath(sourceRoot, file);
        return relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => string.Equals(segment, "Models", StringComparison.Ordinal));
    }

    private static bool HasModelsNamespace(string source)
    {
        var match = NamespaceDeclarationPattern.Match(source);
        return match.Success && match.Groups["name"].Value.Split('.').Contains("Models", StringComparer.Ordinal);
    }

    private static readonly Regex NamespaceDeclarationPattern = new(@"^\s*namespace\s+(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*[;{]", RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex ComparerTypePattern = new(@"\b(?:IComparer|IEqualityComparer|Comparer|Comparison)\s*<", RegexOptions.CultureInvariant);

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
