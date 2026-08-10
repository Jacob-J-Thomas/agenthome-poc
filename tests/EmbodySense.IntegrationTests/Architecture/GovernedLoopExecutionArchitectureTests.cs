using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EmbodySense.IntegrationTests.Architecture;

public sealed class GovernedLoopExecutionArchitectureTests
{
    private static readonly string[] _forbiddenConcreteNamespaces =
    [
        "EmbodySense.Core.Clients",
        "EmbodySense.Core.Persistence",
        "EmbodySense.Core.Startup",
        "EmbodySense.Web",
        "EmbodySense.Cli"
    ];

    private static readonly string[] _forbiddenCompatibilityBoundaryTypeSuffixes =
    [
        "Client",
        "Dispatcher",
        "Policy",
        "Publisher",
        "Queue",
        "RecoveryService",
        "Store",
        "Writer"
    ];

    private static readonly string[] _forbiddenSideEffectIdentifiers =
    [
        "Directory",
        "DirectoryInfo",
        "File",
        "FileInfo",
        "HttpClient",
        "Process",
        "Socket",
        "Stream"
    ];

    private static readonly string[] _mutatingRuntimeSourceTokens =
    [
        "Dispatch",
        "Execution",
        "Mutation",
        "Policy",
        "Queue",
        "Recovery",
        "Runtime"
    ];

    [Fact]
    public void Canonical_execution_contract_preserves_dependency_height()
    {
        var root = FindRepositoryRoot();
        var commonProject = XDocument.Load(Path.Combine(root, "src", "EmbodySense.Core.Common", "EmbodySense.Core.Common.csproj"));
        var applicationProject = XDocument.Load(Path.Combine(root, "src", "EmbodySense.Core.Application", "EmbodySense.Core.Application.csproj"));

        Assert.Empty(commonProject.Descendants("PackageReference"));
        Assert.Empty(commonProject.Descendants("ProjectReference"));
        Assert.Equal(["EmbodySense.Core.Common"], ReadProjectReferences(applicationProject));

        var contractSources = ReadSources(root, Path.Combine(root, "src", "EmbodySense.Core.Common", "Loops", "Execution"))
            .Concat(ReadSources(root, Path.Combine(root, "src", "EmbodySense.Core.Application", "Loops", "Compatibility")));
        var violations = contractSources
            .SelectMany(source => source.Root.DescendantNodes()
                .OfType<NameSyntax>()
                .Select(name => name.ToString())
                .Where(name => _forbiddenConcreteNamespaces.Any(forbidden => name.StartsWith(forbidden, StringComparison.Ordinal)))
                .Select(name => $"{source.RelativePath} references {name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Unbound_payloads_and_compatibility_views_do_not_become_persistence_or_mutating_runtime_contracts()
    {
        var root = FindRepositoryRoot();
        var sourceRoot = Path.Combine(root, "src");
        var commonExecutionRoot = Path.Combine(sourceRoot, "EmbodySense.Core.Common", "Loops", "Execution");
        var compatibilityRoot = Path.Combine(sourceRoot, "EmbodySense.Core.Application", "Loops", "Compatibility");
        var applicationRoot = Path.Combine(sourceRoot, "EmbodySense.Core.Application");
        var persistenceRoot = Path.Combine(sourceRoot, "EmbodySense.Core.Persistence");
        var commonExecutionSources = ReadSources(root, commonExecutionRoot);
        var compatibilitySources = ReadSources(root, compatibilityRoot);
        var unboundPayloadNames = DeclaredTypeNames(commonExecutionSources.Concat(compatibilitySources))
            .Where(name => name.EndsWith("Payload", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var compatibilityTypeNames = DeclaredTypeNames(compatibilitySources)
            .Where(name => name.StartsWith("GovernedLoopCompatibility", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var persistenceForbiddenNames = unboundPayloadNames
            .Concat(compatibilityTypeNames)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("GovernedLoopEffectPayload", unboundPayloadNames);
        Assert.Contains("GovernedLoopFrontierPayload", unboundPayloadNames);
        Assert.Contains("GovernedLoopProjectionPayload", unboundPayloadNames);
        Assert.Contains("GovernedLoopRunLifecyclePayload", unboundPayloadNames);
        Assert.Contains("GovernedLoopCompatibilityEffectObservation", compatibilityTypeNames);
        Assert.Contains("GovernedLoopCompatibilityProjectionObservation", compatibilityTypeNames);
        Assert.Contains("GovernedLoopCompatibilityProjectionResult", compatibilityTypeNames);
        Assert.DoesNotContain("GovernedLoopExecutionEvidenceSet", persistenceForbiddenNames);
        Assert.DoesNotContain("GovernedLoopRunLifecycle", persistenceForbiddenNames);
        Assert.DoesNotContain("GovernedLoopFrontierPosture", persistenceForbiddenNames);
        Assert.DoesNotContain("GovernedLoopEffectPosture", persistenceForbiddenNames);
        Assert.DoesNotContain("GovernedLoopProjectionPosture", persistenceForbiddenNames);

        var persistenceViolations = ReadSources(root, persistenceRoot)
            .SelectMany(source => source.Root.DescendantTokens()
                .Where(token => token.IsKind(SyntaxKind.IdentifierToken) && persistenceForbiddenNames.Contains(token.ValueText))
                .Select(token => $"{source.RelativePath} persists or consumes noncanonical type {token.ValueText}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var applicationSources = ReadSources(root, applicationRoot)
            .Where(source => !IsWithin(source.FullPath, compatibilityRoot))
            .ToArray();
        var mutatingRuntimeSources = applicationSources.Where(IsMutatingRuntimeSource).ToArray();
        Assert.NotEmpty(mutatingRuntimeSources);
        var compatibilityRuntimeViolations = mutatingRuntimeSources
            .SelectMany(source => source.Root.DescendantTokens()
                .Where(token => token.IsKind(SyntaxKind.IdentifierToken) && compatibilityTypeNames.Contains(token.ValueText, StringComparer.Ordinal))
                .Select(token => $"{source.RelativePath} consumes read-only compatibility type {token.ValueText}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var applicationBoundaryViolations = mutatingRuntimeSources
            .SelectMany(source => source.Root.DescendantNodes()
                .OfType<TypeSyntax>()
                .Where(IsPublicOrPortSignatureType)
                .SelectMany(type => type.DescendantTokens())
                .Where(token => token.IsKind(SyntaxKind.IdentifierToken) && persistenceForbiddenNames.Contains(token.ValueText))
                .Select(token => $"{source.RelativePath} exposes noncanonical boundary type {token.ValueText}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(persistenceViolations.Length == 0, string.Join(Environment.NewLine, persistenceViolations));
        Assert.True(compatibilityRuntimeViolations.Length == 0, string.Join(Environment.NewLine, compatibilityRuntimeViolations));
        Assert.True(applicationBoundaryViolations.Length == 0, string.Join(Environment.NewLine, applicationBoundaryViolations));
    }

    [Fact]
    public void Compatibility_projection_has_no_store_or_mutating_adapter_boundary()
    {
        var root = FindRepositoryRoot();
        var sourceRoot = Path.Combine(root, "src");
        var compatibilityRoot = Path.Combine(sourceRoot, "EmbodySense.Core.Application", "Loops", "Compatibility");
        var allProductionSources = ReadSources(root, sourceRoot);
        var compatibilitySources = ReadSources(root, compatibilityRoot);
        var storeViolations = allProductionSources
            .SelectMany(source => DeclaredTypeNames([source])
                .Where(name => name.Contains("GovernedLoopCompatibility", StringComparison.Ordinal) && name.Contains("Store", StringComparison.Ordinal))
                .Select(name => $"{source.RelativePath} declares compatibility store {name}"))
            .Concat(allProductionSources
                .Where(source => Path.GetFileNameWithoutExtension(source.FullPath).Contains("GovernedLoopCompatibilityStore", StringComparison.Ordinal))
                .Select(source => $"{source.RelativePath} is a compatibility store source"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var mutableStateViolations = compatibilitySources
            .SelectMany(source => source.Root.DescendantNodes()
                .OfType<FieldDeclarationSyntax>()
                .Where(field => !field.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.ConstKeyword) || modifier.IsKind(SyntaxKind.ReadOnlyKeyword)))
                .Select(_ => $"{source.RelativePath} declares mutable field state"))
            .Concat(compatibilitySources.SelectMany(source => source.Root.DescendantNodes()
                .OfType<AccessorDeclarationSyntax>()
                .Where(accessor => accessor.IsKind(SyntaxKind.SetAccessorDeclaration) || accessor.IsKind(SyntaxKind.InitAccessorDeclaration))
                .Select(_ => $"{source.RelativePath} exposes a mutable property accessor")))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var adapterViolations = compatibilitySources
            .SelectMany(source => source.Root.DescendantNodes()
                .OfType<TypeSyntax>()
                .SelectMany(type => type.DescendantTokens())
                .Where(token => token.IsKind(SyntaxKind.IdentifierToken)
                    && _forbiddenCompatibilityBoundaryTypeSuffixes.Any(suffix => token.ValueText.EndsWith(suffix, StringComparison.Ordinal)))
                .Select(token => $"{source.RelativePath} references boundary type {token.ValueText}"))
            .Concat(compatibilitySources.SelectMany(source => source.Root.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Where(identifier => _forbiddenSideEffectIdentifiers.Contains(identifier.Identifier.ValueText, StringComparer.Ordinal))
                .Select(identifier => $"{source.RelativePath} references side-effect API {identifier.Identifier.ValueText}")))
            .Concat(compatibilitySources.SelectMany(source => source.Root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(method => method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.AsyncKeyword))
                    || method.DescendantNodes().OfType<AwaitExpressionSyntax>().Any())
                .Select(method => $"{source.RelativePath} declares asynchronous method {method.Identifier.ValueText}")))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(storeViolations.Length == 0, string.Join(Environment.NewLine, storeViolations));
        Assert.True(mutableStateViolations.Length == 0, string.Join(Environment.NewLine, mutableStateViolations));
        Assert.True(adapterViolations.Length == 0, string.Join(Environment.NewLine, adapterViolations));
    }

    [Fact]
    public void Compatibility_projector_remains_a_read_only_application_projection()
    {
        var root = FindRepositoryRoot();
        var compatibilityRoot = Path.Combine(root, "src", "EmbodySense.Core.Application", "Loops", "Compatibility");
        var sources = ReadSources(root, compatibilityRoot);
        var namespaceViolations = sources
            .SelectMany(source => source.Root.DescendantNodes()
                .OfType<BaseNamespaceDeclarationSyntax>()
                .Where(declaration => !declaration.Name.ToString().StartsWith("EmbodySense.Core.Application.Loops.Compatibility", StringComparison.Ordinal))
                .Select(declaration => $"{source.RelativePath} declares {declaration.Name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(namespaceViolations.Length == 0, string.Join(Environment.NewLine, namespaceViolations));

        var projectorSource = Assert.Single(sources, source => string.Equals(Path.GetFileName(source.FullPath), "GovernedLoopCompatibilityProjector.cs", StringComparison.Ordinal));
        var projector = Assert.Single(
            projectorSource.Root.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            declaration => string.Equals(declaration.Identifier.ValueText, "GovernedLoopCompatibilityProjector", StringComparison.Ordinal));
        Assert.Contains(projector.Modifiers, modifier => modifier.IsKind(SyntaxKind.PublicKeyword));
        Assert.Contains(projector.Modifiers, modifier => modifier.IsKind(SyntaxKind.StaticKeyword));
        Assert.DoesNotContain(projector.Members, member => member is not MethodDeclarationSyntax);

        var publicMethods = projector.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(method => method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword)))
            .OrderBy(method => method.Identifier.ValueText, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["ProjectCustomLoop", "ProjectDefaultConversation"], publicMethods.Select(method => method.Identifier.ValueText));
        foreach (var method in publicMethods)
        {
            Assert.Contains(method.Modifiers, modifier => modifier.IsKind(SyntaxKind.StaticKeyword));
            Assert.DoesNotContain(method.Modifiers, modifier => modifier.IsKind(SyntaxKind.AsyncKeyword));
            Assert.Equal("GovernedLoopCompatibilityProjectionResult", method.ReturnType.ToString());
            var parameter = Assert.Single(method.ParameterList.Parameters);
            Assert.Empty(parameter.Modifiers);
        }
    }

    private static IReadOnlyList<string> DeclaredTypeNames(IEnumerable<SourceDocument> sources)
    {
        return sources
            .SelectMany(source => source.Root.DescendantNodes()
                .OfType<BaseTypeDeclarationSyntax>()
                .Select(declaration => declaration.Identifier.ValueText)
                .Concat(source.Root.DescendantNodes().OfType<DelegateDeclarationSyntax>().Select(declaration => declaration.Identifier.ValueText)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static SourceDocument[] ReadSources(string repositoryRoot, string directory)
    {
        return Directory
            .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => IsAuthoredSource(repositoryRoot, file))
            .Order(StringComparer.Ordinal)
            .Select(file => new SourceDocument(
                file,
                NormalizeRepositoryRelativePath(Path.GetRelativePath(repositoryRoot, file)),
                (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot()))
            .ToArray();
    }

    private static string[] ReadProjectReferences(XDocument project)
    {
        return project
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsPublicOrPortSignatureType(TypeSyntax type)
    {
        var owner = type.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        if (owner is null
            || type.Ancestors().TakeWhile(node => node != owner).Any(node => node is BlockSyntax or ArrowExpressionClauseSyntax or EqualsValueClauseSyntax or AccessorDeclarationSyntax))
        {
            return false;
        }

        if (owner.Parent is InterfaceDeclarationSyntax || owner is DelegateDeclarationSyntax)
        {
            return true;
        }

        var modifiers = owner switch
        {
            BaseMethodDeclarationSyntax declaration => declaration.Modifiers,
            BasePropertyDeclarationSyntax declaration => declaration.Modifiers,
            BaseTypeDeclarationSyntax declaration => declaration.Modifiers,
            EventFieldDeclarationSyntax declaration => declaration.Modifiers,
            FieldDeclarationSyntax declaration => declaration.Modifiers,
            _ => default
        };
        return modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword) || modifier.IsKind(SyntaxKind.ProtectedKeyword));
    }

    private static bool IsMutatingRuntimeSource(SourceDocument source)
    {
        return _mutatingRuntimeSourceTokens.Any(candidate => source.RelativePath.Contains(candidate, StringComparison.OrdinalIgnoreCase)
            || source.Root.DescendantTokens().Any(token => token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText.Contains(candidate, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsWithin(string file, string directory)
    {
        var relative = Path.GetRelativePath(directory, file);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IsAuthoredSource(string repositoryRoot, string file)
    {
        var segments = Path.GetRelativePath(repositoryRoot, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !segments.Any(segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeRepositoryRelativePath(string path) => path.Replace('\\', '/');

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

    private sealed record SourceDocument(string FullPath, string RelativePath, CompilationUnitSyntax Root);
}
