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
        "Dto",
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
    public void Production_model_files_use_path_matching_models_namespaces()
    {
        var root = FindRepositoryRoot();
        var sourceRoot = Path.Combine(root, "src");
        var violations = ProductionProjectRoots(sourceRoot)
            .SelectMany(projectRoot => Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories))
            .Where(file => IsModelFile(sourceRoot, file))
            .Where(file => !HasExpectedNamespace(sourceRoot, file))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Production_model_declarations_are_not_left_outside_models_directories()
    {
        var root = FindRepositoryRoot();
        var sourceRoot = Path.Combine(root, "src");
        var violations = ProductionProjectRoots(sourceRoot)
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
            internal sealed class FeatureDTO { }
            internal sealed record FeatureResult(string Value)
            {
                public string Format() => Value;
            }
            internal sealed class FeatureService { }
            """;

        Assert.Equal(["FeatureState", "FeatureKind", "FeatureRequest", "FeatureDTO"], FindTopLevelModelCandidateNames(source));
    }

    [Fact]
    public void Production_model_files_do_not_own_behavior()
    {
        var root = FindRepositoryRoot();
        var sourceRoot = Path.Combine(root, "src");
        var violations = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => IsModelFile(sourceRoot, file))
            .SelectMany(file => FindTopLevelBehaviorBearingTypeNames(File.ReadAllText(file)).Select(name => $"{Path.GetRelativePath(root, file)} declares behavior-bearing type {name} in Models."))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
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

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Behavior_classification_catches_behavior_without_rejecting_storage_only_constructors()
    {
        const string source = """
            namespace Example;

            internal sealed record EventfulState
            {
                public event EventHandler? Changed;
            }

            internal sealed record NestedValidatorState
            {
                private sealed class Validator
                {
                    public bool IsValid() => true;
                }
            }

            internal sealed record NestedDataState
            {
                private sealed record Metadata(string Value);
            }

            internal sealed record StoredState
            {
                public StoredState()
                {
                }

                public StoredState(string value)
                {
                    Value = value;
                }

                public string Value { get; init; } = string.Empty;
            }

            internal sealed record NormalizedState
            {
                public NormalizedState(string value)
                {
                    Value = value.Trim();
                }

                public string Value { get; init; }
            }

            internal sealed record StaticMutationState
            {
                public StaticMutationState(string value)
                {
                    LastSeen = value;
                }

                private static string LastSeen { get; set; } = string.Empty;
            }
            """;

        Assert.Equal(["EventfulState", "NestedValidatorState", "NormalizedState", "StaticMutationState"], FindTopLevelBehaviorBearingTypeNames(source));
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
            .Where(declaration => !OwnsBehavior(declaration))
            .Where(declaration => declaration is RecordDeclarationSyntax or EnumDeclarationSyntax || ModelTypeSuffixes.Any(suffix => declaration.Identifier.ValueText.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            .Select(declaration => declaration.Identifier.ValueText)
            .ToArray();
    }

    private static IReadOnlyList<string> FindTopLevelBehaviorBearingTypeNames(string source)
    {
        return CSharpSyntaxTree
            .ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Where(declaration => declaration.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
            .Where(OwnsBehavior)
            .Select(declaration => declaration.Identifier.ValueText)
            .ToArray();
    }

    private static bool OwnsBehavior(BaseTypeDeclarationSyntax declaration)
    {
        if (declaration is not TypeDeclarationSyntax typeDeclaration)
        {
            return false;
        }

        return typeDeclaration.Members.Any(member => member is MethodDeclarationSyntax
            or DestructorDeclarationSyntax
            or OperatorDeclarationSyntax
            or ConversionOperatorDeclarationSyntax
            or IndexerDeclarationSyntax
            or EventFieldDeclarationSyntax
            || member is ConstructorDeclarationSyntax constructor && ConstructorOwnsBehavior(constructor)
            || member is PropertyDeclarationSyntax property && (property.ExpressionBody is not null || property.AccessorList?.Accessors.Any(accessor => accessor.Body is not null || accessor.ExpressionBody is not null) == true)
            || member is EventDeclarationSyntax eventDeclaration && eventDeclaration.AccessorList?.Accessors.Any(accessor => accessor.Body is not null || accessor.ExpressionBody is not null) == true
            || member is BaseTypeDeclarationSyntax nestedType && OwnsBehavior(nestedType));
    }

    private static bool ConstructorOwnsBehavior(ConstructorDeclarationSyntax constructor)
    {
        if (constructor.Initializer is not null)
        {
            return true;
        }

        var parameterNames = constructor.ParameterList.Parameters.Select(parameter => parameter.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        var instanceStorageNames = constructor.Parent is TypeDeclarationSyntax containingType
            ? FindInstanceStorageNames(containingType)
            : new HashSet<string>(StringComparer.Ordinal);
        if (constructor.ExpressionBody is not null)
        {
            return !IsStorageAssignment(constructor.ExpressionBody.Expression, parameterNames, instanceStorageNames);
        }

        return constructor.Body?.Statements.Any(statement => statement is not ExpressionStatementSyntax expressionStatement || !IsStorageAssignment(expressionStatement.Expression, parameterNames, instanceStorageNames)) == true;
    }

    private static IReadOnlySet<string> FindInstanceStorageNames(TypeDeclarationSyntax containingType)
    {
        return containingType.Members
            .SelectMany(member => member switch
            {
                PropertyDeclarationSyntax property when !property.Modifiers.Any(modifier => modifier.RawKind == (int)SyntaxKind.StaticKeyword) => [property.Identifier.ValueText],
                FieldDeclarationSyntax field when !field.Modifiers.Any(modifier => modifier.RawKind == (int)SyntaxKind.StaticKeyword) => field.Declaration.Variables.Select(variable => variable.Identifier.ValueText),
                _ => []
            })
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsStorageAssignment(ExpressionSyntax expression, IReadOnlySet<string> parameterNames, IReadOnlySet<string> instanceStorageNames)
    {
        if (expression is not AssignmentExpressionSyntax assignment || assignment.RawKind != (int)SyntaxKind.SimpleAssignmentExpression)
        {
            return false;
        }

        var targetIsStoredMember = assignment.Left switch
        {
            IdentifierNameSyntax identifier => instanceStorageNames.Contains(identifier.Identifier.ValueText),
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax member } => instanceStorageNames.Contains(member.Identifier.ValueText),
            _ => false
        };
        return targetIsStoredMember && assignment.Right is IdentifierNameSyntax value && parameterNames.Contains(value.Identifier.ValueText);
    }

    private static IReadOnlyList<string> ProductionProjectRoots(string sourceRoot)
    {
        return Directory
            .EnumerateDirectories(sourceRoot, "EmbodySense.*", SearchOption.TopDirectoryOnly)
            .Where(directory => File.Exists(Path.Combine(directory, $"{Path.GetFileName(directory)}.csproj")))
            .ToArray();
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

    private static readonly Regex NamespaceDeclarationPattern = new(@"^\s*namespace\s+(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*[;{]", RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex TypeDeclarationWithBaseListPattern = new(@"^\s*(?:(?:public|internal|private|protected|file|abstract|sealed|static|partial|readonly|ref|unsafe|new)\s+)*(?:class|record(?:\s+(?:class|struct))?|struct)\s+[A-Za-z_]\w*(?:\s*<[^>{;]+>)?(?:\s*\([^;{]*\))?\s*:\s*(?<bases>[^{]+)\{", RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex ComparerBaseTypePattern = new(@"\b(?:IComparer|IEqualityComparer|Comparer)\s*<", RegexOptions.CultureInvariant);

    private static bool DeclaresComparerType(string source)
    {
        return TypeDeclarationWithBaseListPattern.Matches(source).Any(match => ComparerBaseTypePattern.IsMatch(match.Groups["bases"].Value));
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
