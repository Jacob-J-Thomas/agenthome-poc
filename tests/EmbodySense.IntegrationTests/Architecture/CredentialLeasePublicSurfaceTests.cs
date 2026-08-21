using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EmbodySense.IntegrationTests.Architecture;

public sealed class CredentialLeasePublicSurfaceTests
{
    private static readonly string[] _forbiddenMaterialTerms = ["Bearer", "CredentialMaterial", "EncryptedEnvelope", "GetLease", "GetSecret", "PrivateLocator", "ReadSecret", "Renewal"];
    private static readonly string[] _surfaceProjects = ["EmbodySense.Web", "EmbodySense.Cli", "EmbodySense.Cli.Command"];
    private static readonly string[] _serverOnlyCredentialTypes = ["CredentialBroker", "CredentialLease", "CredentialUseRequest", "ICredentialBroker", "ICredentialValueProvider"];
    private static readonly string[] _contractDirectories =
    [
        "src/EmbodySense.Core.Common/Credentials/Leases",
        "src/EmbodySense.Core.Application/Credentials",
        "src/EmbodySense.Core.Startup/Credentials",
    ];
    private static readonly string[] _materialBoundaryTypes = ["ICredentialBroker", "ICredentialValueProvider", "ICredentialTrustedUseConsumer"];

    [Fact]
    public void Public_lease_and_broker_source_contracts_expose_no_material_bearer_locator_or_renewal_path()
    {
        var root = FindRepositoryRoot();
        var syntaxRoots = ContractSourceFiles(root)
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot())
            .ToArray();
        var signatures = syntaxRoots
            .SelectMany(syntaxRoot => syntaxRoot.DescendantNodes().OfType<MemberDeclarationSyntax>())
            .Where(IsPublicContractDeclaration)
            .Select(declaration => declaration.WithoutTrivia().NormalizeWhitespace().ToFullString())
            .ToArray();

        foreach (var term in _forbiddenMaterialTerms)
        {
            Assert.DoesNotContain(signatures, signature => signature.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var boundaryMethods = syntaxRoots
            .SelectMany(syntaxRoot => syntaxRoot.DescendantNodes().OfType<TypeDeclarationSyntax>())
            .Where(type => _materialBoundaryTypes.Contains(type.Identifier.ValueText, StringComparer.Ordinal))
            .SelectMany(type => type.Members.OfType<MethodDeclarationSyntax>())
            .ToArray();
        Assert.DoesNotContain(boundaryMethods, method => method.ReturnType.WithoutTrivia().ToString() is "byte[]" or "Memory<byte>" or "ReadOnlyMemory<byte>");

        var broker = Assert.Single(
            syntaxRoots.SelectMany(syntaxRoot => syntaxRoot.DescendantNodes().OfType<InterfaceDeclarationSyntax>()),
            type => type.Identifier.ValueText == "ICredentialBroker");
        var use = Assert.Single(broker.Members.OfType<MethodDeclarationSyntax>());
        Assert.Equal("UseAsync", use.Identifier.ValueText);
        Assert.Equal("ValueTask<CredentialUseResult>", use.ReturnType.WithoutTrivia().ToString());
        Assert.Contains(use.ParameterList.Parameters, parameter => parameter.Type?.WithoutTrivia().ToString() == "ICredentialTrustedUseConsumer");
    }

    [Fact]
    public void Browser_cli_and_public_http_sources_have_no_broker_lease_or_provider_access_path()
    {
        var root = FindRepositoryRoot();
        var violations = _surfaceProjects
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories))
            .SelectMany(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()
                .DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Where(identifier => _serverOnlyCredentialTypes.Any(term => identifier.Identifier.ValueText.Contains(term, StringComparison.Ordinal)))
                .Select(identifier => $"{Path.GetRelativePath(root, path)} references server-only credential type {identifier.Identifier.ValueText}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
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

    private static IEnumerable<string> ContractSourceFiles(string root)
        => _contractDirectories.SelectMany(relative => Directory.EnumerateFiles(Path.Combine(root, relative), "*.cs", SearchOption.AllDirectories));

    private static bool IsPublicContractDeclaration(MemberDeclarationSyntax declaration)
    {
        if (declaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword)))
        {
            return true;
        }

        return declaration.Parent is InterfaceDeclarationSyntax parent
            && parent.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword));
    }
}
