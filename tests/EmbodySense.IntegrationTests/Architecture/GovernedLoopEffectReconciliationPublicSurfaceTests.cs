using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EmbodySense.IntegrationTests.Architecture;

public sealed class GovernedLoopEffectReconciliationPublicSurfaceTests
{
    private const string CommonProject = "EmbodySense.Core.Common";
    private const string ApplicationProject = "EmbodySense.Core.Application";
    private const string ReconciliationPath = "Loops/Execution/Reconciliation";
    private static readonly IReadOnlyDictionary<string, string[]> _expectedApplicationPorts = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["IGovernedLoopEffectReconciliationAuthorizationSource"] = ["AuthorizeAsync"],
        ["IGovernedLoopEffectReconciliationCaseStore"] = ["CompareExchangeAsync", "ListAsync", "ReadAsync"],
        ["IGovernedLoopEffectReconciliationInputSource"] = ["ReadAsync"],
        ["IGovernedLoopEffectReconciliationProbe"] = ["ProbeAsync"],
        ["IGovernedLoopEffectReconciliationProbeRegistry"] = ["ListAsync", "ReadAsync"],
        ["IGovernedLoopEffectReconciliationResolutionReader"] = ["ReadAsync"],
    };
    private static readonly string[] _forbiddenApplicationDependencies =
    [
        "EmbodySense.Core.Application.CommandActions",
        "EmbodySense.Core.Application.LocalWorkspace.Actions",
        "EmbodySense.Core.Application.Loops.EffectAttempts",
        "EmbodySense.Core.Application.Loops.EffectAuthorityEvidence",
        "EmbodySense.Core.Application.Loops.EffectAuthorityUsage",
        "EmbodySense.Core.Application.Loops.Execution.Effects",
        "EmbodySense.Core.Application.Triggers",
        "EmbodySense.Core.Clients",
        "EmbodySense.Core.Persistence",
        "EmbodySense.Core.Startup",
        "EmbodySense.Cli",
        "EmbodySense.Web",
        "GovernedActuatorDispatchBoundary",
        "ICommandActionNativeHost",
        "ICommandActionNativeLaunchBoundary",
        "ICustomLoopRunStore",
        "IGovernedActuatorDispatchBoundary",
        "IGovernedLoopEffectAttemptPreparationClaimStore",
        "IGovernedLoopEffectAttemptReadStore",
        "IGovernedLoopEffectAttemptStore",
        "ILoopRunStore",
        "ITriggerWorkerDispatcher",
        "IWorkspaceActionNativeDispatchBoundary",
        "IServiceCollection",
    ];
    private static readonly string[] _forbiddenOperationTerms = ["Dispatch", "Recover", "Recovery", "Retry"];
    private static readonly string[] _forbiddenTestHookTerms = ["Fake", "ForTest", "Hook", "Mock", "Stub", "TestOnly"];

    [Fact]
    public void Common_reconciliation_contracts_remain_dependency_free()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "src", CommonProject, $"{CommonProject}.csproj"));
        var sources = ReadContractSources(root, CommonProject);
        var foreignUsings = sources
            .SelectMany(source => source.Root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Select(usingDirective => usingDirective.Name?.ToString())
                .Where(namespaceName => namespaceName?.StartsWith("EmbodySense.", StringComparison.Ordinal) == true
                    && !namespaceName.StartsWith("EmbodySense.Core.Common", StringComparison.Ordinal))
                .Select(namespaceName => $"{source.Path} imports {namespaceName}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.NotEmpty(sources.SelectMany(source => PublicTopLevelTypes(source.Root)));
        Assert.Empty(foreignUsings);
    }

    [Fact]
    public void Application_reconciliation_ports_expose_only_abstract_contract_dependencies()
    {
        var root = FindRepositoryRoot();
        var sources = ReadContractSources(root, ApplicationProject);
        var ports = sources
            .SelectMany(source => PublicTopLevelTypes(source.Root).OfType<InterfaceDeclarationSyntax>()
                .Select(declaration => (source.Path, Declaration: declaration)))
            .OrderBy(port => port.Declaration.Identifier.ValueText, StringComparer.Ordinal)
            .ToArray();
        var forbiddenUsings = sources
            .SelectMany(source => source.Root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Select(usingDirective => usingDirective.Name?.ToString())
                .Where(namespaceName => namespaceName?.StartsWith("EmbodySense.", StringComparison.Ordinal) == true
                    && !namespaceName.StartsWith("EmbodySense.Core.Common", StringComparison.Ordinal)
                    && !namespaceName.StartsWith("EmbodySense.Core.Application.Loops.Execution.Reconciliation", StringComparison.Ordinal))
                .Select(namespaceName => $"{source.Path} imports non-contract namespace {namespaceName}"))
            .ToArray();
        var violations = ports
            .SelectMany(port => _forbiddenApplicationDependencies
                .Where(term => ContractText(sources.Single(source => source.Path == port.Path), port.Declaration).Contains(term, StringComparison.Ordinal))
                .Select(term => $"{port.Path} exposes forbidden dependency {term}"))
            .ToArray();

        Assert.Equal(_expectedApplicationPorts.Keys.Order(StringComparer.Ordinal), ports.Select(port => port.Declaration.Identifier.ValueText));
        Assert.All(ports, port => Assert.Null(port.Declaration.BaseList));
        Assert.All(ports, port => Assert.All(port.Declaration.Members, member =>
        {
            var method = Assert.IsType<MethodDeclarationSyntax>(member);
            Assert.Null(method.Body);
            Assert.Null(method.ExpressionBody);
        }));
        Assert.All(ports, port => Assert.Equal(
            _expectedApplicationPorts[port.Declaration.Identifier.ValueText].Order(StringComparer.Ordinal),
            port.Declaration.Members.OfType<MethodDeclarationSyntax>().Select(method => method.Identifier.ValueText).Order(StringComparer.Ordinal)));
        Assert.Empty(forbiddenUsings);
        Assert.Empty(violations);
    }

    [Fact]
    public void Reconciliation_contracts_use_one_matching_public_type_per_file_and_models_layout()
    {
        var root = FindRepositoryRoot();
        var sources = ReadContractSources(root, CommonProject).Concat(ReadContractSources(root, ApplicationProject));
        var violations = new List<string>();

        foreach (var source in sources)
        {
            var publicTypes = PublicTopLevelTypes(source.Root).ToArray();
            if (publicTypes.Length > 1)
            {
                violations.Add($"{source.Path} contains {publicTypes.Length} public top-level types.");
            }

            foreach (var type in publicTypes)
            {
                var expectedName = Path.GetFileNameWithoutExtension(source.Path);
                if (!string.Equals(type.Identifier.ValueText, expectedName, StringComparison.Ordinal))
                {
                    violations.Add($"{source.Path} declares public type {type.Identifier.ValueText} instead of {expectedName}.");
                }

                var isModelsPath = Path.GetDirectoryName(source.Path)?.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("Models", StringComparer.Ordinal) == true;
                var namespaceName = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().SingleOrDefault()?.Name.ToString();
                var isModelsNamespace = namespaceName?.EndsWith(".Models", StringComparison.Ordinal) == true;
                if (isModelsPath != isModelsNamespace)
                {
                    violations.Add($"{source.Path} does not match its declared namespace {namespaceName}.");
                }

                if (!isModelsPath && type is (RecordDeclarationSyntax or EnumDeclarationSyntax))
                {
                    violations.Add($"{source.Path} leaves public model {type.Identifier.ValueText} outside Models.");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Reconciliation_public_contracts_expose_no_dispatch_retry_or_recovery_surface()
    {
        var root = FindRepositoryRoot();
        var sources = ReadContractSources(root, CommonProject).Concat(ReadContractSources(root, ApplicationProject));
        var violations = sources
            .SelectMany(source => PublicTopLevelTypes(source.Root)
                .SelectMany(declaration => PublicOperationNames(declaration)
                    .SelectMany(name => _forbiddenOperationTerms
                        .Where(term => name.Contains(term, StringComparison.OrdinalIgnoreCase))
                        .Select(term => $"{source.Path} exposes forbidden operation {name} through term {term}"))))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    private static IEnumerable<string> PublicOperationNames(BaseTypeDeclarationSyntax declaration)
    {
        yield return declaration.Identifier.ValueText;
        if (declaration is EnumDeclarationSyntax enumDeclaration)
        {
            foreach (var member in enumDeclaration.Members)
            {
                yield return member.Identifier.ValueText;
            }

            yield break;
        }

        if (declaration is not TypeDeclarationSyntax typeDeclaration)
        {
            yield break;
        }

        foreach (var member in typeDeclaration.Members)
        {
            var isPublic = declaration is InterfaceDeclarationSyntax || member.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword));
            if (!isPublic)
            {
                continue;
            }

            var name = member switch
            {
                ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
                EventDeclarationSyntax eventDeclaration => eventDeclaration.Identifier.ValueText,
                MethodDeclarationSyntax method => method.Identifier.ValueText,
                PropertyDeclarationSyntax property => property.Identifier.ValueText,
                _ => null,
            };
            if (name is not null)
            {
                yield return name;
            }
        }
    }

    [Fact]
    public void Reconciliation_public_contracts_are_confined_to_common_and_application_without_test_hooks()
    {
        var root = FindRepositoryRoot();
        var contractSources = ReadContractSources(root, CommonProject).Concat(ReadContractSources(root, ApplicationProject)).ToArray();
        var contractTypeNames = contractSources.SelectMany(source => PublicTopLevelTypes(source.Root)).Select(type => type.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        var sourceRoot = Path.Combine(root, "src");
        var allowedDirectories = new[]
        {
            NormalizePath(Path.Combine(sourceRoot, CommonProject, ReconciliationPath)),
            NormalizePath(Path.Combine(sourceRoot, ApplicationProject, ReconciliationPath)),
        };
        var duplicateDeclarations = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !allowedDirectories.Any(directory => NormalizePath(path).StartsWith(directory + '/', StringComparison.Ordinal)))
            .SelectMany(path => PublicTopLevelTypes(CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot())
                .Where(type => contractTypeNames.Contains(type.Identifier.ValueText))
                .Select(type => $"{Path.GetRelativePath(root, path)} redeclares reconciliation contract {type.Identifier.ValueText}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var testHooks = contractSources
            .SelectMany(source => PublicTopLevelTypes(source.Root)
                .SelectMany(declaration => _forbiddenTestHookTerms
                    .Where(term => PublicSurfaceText(declaration).Contains(term, StringComparison.OrdinalIgnoreCase))
                    .Select(term => $"{source.Path} exposes test-hook term {term}")))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(duplicateDeclarations);
        Assert.Empty(testHooks);
    }

    private static string ContractText(ContractSource source, BaseTypeDeclarationSyntax declaration)
    {
        var imports = source.Root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(usingDirective => usingDirective.WithoutTrivia().ToString());
        return string.Join(Environment.NewLine, imports.Append(declaration.WithoutTrivia().NormalizeWhitespace().ToFullString()));
    }

    private static string PublicSurfaceText(BaseTypeDeclarationSyntax declaration)
    {
        if (declaration is InterfaceDeclarationSyntax or EnumDeclarationSyntax)
        {
            return declaration.WithoutTrivia().NormalizeWhitespace().ToFullString();
        }

        if (declaration is not TypeDeclarationSyntax typeDeclaration)
        {
            return declaration.Identifier.ValueText;
        }

        var signatureParts = new List<string> { declaration.Identifier.ValueText };
        if (declaration.BaseList is not null)
        {
            signatureParts.Add(declaration.BaseList.WithoutTrivia().NormalizeWhitespace().ToFullString());
        }

        if (declaration is RecordDeclarationSyntax record && record.ParameterList is not null)
        {
            signatureParts.Add(record.ParameterList.WithoutTrivia().NormalizeWhitespace().ToFullString());
        }

        signatureParts.AddRange(typeDeclaration.Members
            .Where(member => member.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword)))
            .Select(PublicMemberSignature));
        return string.Join(Environment.NewLine, signatureParts);
    }

    private static string PublicMemberSignature(MemberDeclarationSyntax member)
    {
        return member switch
        {
            BaseFieldDeclarationSyntax field => field.Declaration.WithoutTrivia().NormalizeWhitespace().ToFullString(),
            ConstructorDeclarationSyntax constructor => $"{constructor.Identifier.ValueText}{constructor.ParameterList.WithoutTrivia().NormalizeWhitespace()}",
            ConversionOperatorDeclarationSyntax conversion => $"{conversion.Type.WithoutTrivia().NormalizeWhitespace()}{conversion.ParameterList.WithoutTrivia().NormalizeWhitespace()}",
            EventDeclarationSyntax eventDeclaration => $"{eventDeclaration.Type.WithoutTrivia().NormalizeWhitespace()} {eventDeclaration.Identifier.ValueText}",
            IndexerDeclarationSyntax indexer => $"{indexer.Type.WithoutTrivia().NormalizeWhitespace()}{indexer.ParameterList.WithoutTrivia().NormalizeWhitespace()}",
            MethodDeclarationSyntax method => $"{method.ReturnType.WithoutTrivia().NormalizeWhitespace()} {method.Identifier.ValueText}{method.TypeParameterList?.WithoutTrivia().NormalizeWhitespace()}{method.ParameterList.WithoutTrivia().NormalizeWhitespace()}",
            OperatorDeclarationSyntax operation => $"{operation.ReturnType.WithoutTrivia().NormalizeWhitespace()} {operation.OperatorToken.ValueText}{operation.ParameterList.WithoutTrivia().NormalizeWhitespace()}",
            PropertyDeclarationSyntax property => $"{property.Type.WithoutTrivia().NormalizeWhitespace()} {property.Identifier.ValueText}",
            _ => member.WithoutTrivia().NormalizeWhitespace().ToFullString(),
        };
    }

    private static ContractSource[] ReadContractSources(string root, string project)
    {
        var directory = Path.Combine(root, "src", project, ReconciliationPath);
        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => new ContractSource(Path.GetRelativePath(root, path), CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()))
            .ToArray();
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static IEnumerable<BaseTypeDeclarationSyntax> PublicTopLevelTypes(SyntaxNode root)
    {
        return root.DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Where(declaration => declaration.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax
                && declaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword)));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbodySense.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed record ContractSource(string Path, SyntaxNode Root);
}
