using System.Text.RegularExpressions;

namespace EmbodySense.IntegrationTests.Architecture;

public sealed class ModelSourceLayoutTests
{
    private const string LocalWorkspaceModelsNamespace = "EmbodySense.Core.Clients.LocalWorkspace.Models";

    [Fact]
    public void LocalWorkspace_model_files_use_a_models_namespace()
    {
        var root = FindRepositoryRoot();
        var modelRoot = Path.Combine(root, "src", "EmbodySense.Core.Clients", "LocalWorkspace", "Models");
        var violations = Directory
            .EnumerateFiles(modelRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !HasExpectedNamespace(File.ReadAllText(file), LocalWorkspaceModelsNamespace))
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
            .Where(file => DeclaresComparerType(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        Assert.Empty(violations);
    }

    private static bool IsModelFile(string sourceRoot, string file)
    {
        var relativePath = Path.GetRelativePath(sourceRoot, file);
        return relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => string.Equals(segment, "Models", StringComparison.Ordinal));
    }

    private static bool HasExpectedNamespace(string source, string expectedNamespace)
    {
        var match = NamespaceDeclarationPattern.Match(source);
        if (!match.Success)
        {
            return false;
        }

        var declaredNamespace = match.Groups["name"].Value;
        return string.Equals(declaredNamespace, expectedNamespace, StringComparison.Ordinal) || declaredNamespace.StartsWith($"{expectedNamespace}.", StringComparison.Ordinal);
    }

    private static bool DeclaresComparerType(string source)
    {
        return TypeDeclarationWithBaseListPattern.Matches(source).Any(match => ComparerBaseTypePattern.IsMatch(match.Groups["bases"].Value));
    }

    private static readonly Regex NamespaceDeclarationPattern = new(@"^\s*namespace\s+(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*[;{]", RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex TypeDeclarationWithBaseListPattern = new(@"^\s*(?:(?:public|internal|private|protected|file|abstract|sealed|static|partial|readonly|ref|unsafe|new)\s+)*(?:class|record(?:\s+(?:class|struct))?|struct)\s+[A-Za-z_]\w*(?:\s*<[^>{;]+>)?(?:\s*\([^;{]*\))?\s*:\s*(?<bases>[^{]+)\{", RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex ComparerBaseTypePattern = new(@"\b(?:IComparer|IEqualityComparer|Comparer)\s*<", RegexOptions.CultureInvariant);

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
