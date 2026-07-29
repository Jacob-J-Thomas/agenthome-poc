using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EmbodySense.IntegrationTests.Architecture;

public sealed class ModelSourceLayoutTests
{
    private static readonly string[] ModelTypeSuffixes =
    [
        "Config",
        "Configuration",
        "Decision",
        "Definition",
        "Descriptor",
        "Entry",
        "Event",
        "Evidence",
        "Identity",
        "Manifest",
        "Message",
        "Operation",
        "Options",
        "Outcome",
        "Page",
        "Quota",
        "Receipt",
        "Record",
        "Reference",
        "Request",
        "Response",
        "Result",
        "Snapshot",
        "Status"
    ];

    [Fact]
    public void Foundation_model_files_use_path_matching_models_namespaces()
    {
        var root = FindRepositoryRoot();
        var sourceRoot = Path.Combine(root, "src");
        var violations = FoundationProjectRoots(sourceRoot)
            .SelectMany(projectRoot => Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories))
            .Where(file => IsModelFile(sourceRoot, file))
            .Where(file => !HasExpectedNamespace(sourceRoot, file))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        // TODO(https://github.com/Jacob-J-Thomas/agenthome-poc/issues/85): Add Core.Application, Core.Startup, CLI, and Web as their model slices are migrated.
        Assert.Empty(violations);
    }

    [Fact]
    public void Foundation_model_declarations_are_not_left_outside_models_directories()
    {
        var root = FindRepositoryRoot();
        var sourceRoot = Path.Combine(root, "src");
        var violations = FoundationProjectRoots(sourceRoot)
            .SelectMany(projectRoot => Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsModelFile(sourceRoot, file))
            .SelectMany(file => FindTopLevelModelCandidateNames(File.ReadAllText(file)).Select(name => $"{Path.GetRelativePath(root, file)} declares model candidate {name} outside Models."))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Model_candidate_classification_catches_records_enums_and_dto_suffixes()
    {
        const string source = """
            namespace Example;

            internal sealed record FeatureState(string Value);
            internal enum FeatureKind { Unknown }
            internal sealed class FeatureRequest { }
            internal sealed class FeatureService { }
            """;

        Assert.Equal(["FeatureState", "FeatureKind", "FeatureRequest"], FindTopLevelModelCandidateNames(source));
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

    private static IReadOnlyList<string> FindTopLevelModelCandidateNames(string source)
    {
        return CSharpSyntaxTree
            .ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Where(declaration => declaration.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
            .Where(declaration => declaration is RecordDeclarationSyntax or EnumDeclarationSyntax || ModelTypeSuffixes.Any(suffix => declaration.Identifier.ValueText.EndsWith(suffix, StringComparison.Ordinal)))
            .Select(declaration => declaration.Identifier.ValueText)
            .ToArray();
    }

    private static IReadOnlyList<string> FoundationProjectRoots(string sourceRoot)
    {
        return
        [
            Path.Combine(sourceRoot, "EmbodySense.Core.Common"),
            Path.Combine(sourceRoot, "EmbodySense.Core.Clients"),
            Path.Combine(sourceRoot, "EmbodySense.Core.Persistence")
        ];
    }

    private static bool HasExpectedNamespace(string sourceRoot, string file)
    {
        var match = NamespaceDeclarationPattern.Match(File.ReadAllText(file));
        if (!match.Success)
        {
            return false;
        }

        var relativeDirectory = Path.GetDirectoryName(Path.GetRelativePath(sourceRoot, file)) ?? string.Empty;
        var expectedNamespace = relativeDirectory.Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.');
        var declaredNamespace = match.Groups["name"].Value;
        return string.Equals(declaredNamespace, expectedNamespace, StringComparison.Ordinal);
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
