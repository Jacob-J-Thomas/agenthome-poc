using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EmbodySense.IntegrationTests.Architecture;

internal static class CSharpParameterNamingPolicy
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp14);

    public static IReadOnlyList<string> FindViolations(string source, string sourcePath)
    {
        var root = CSharpSyntaxTree.ParseText(source, ParseOptions, sourcePath).GetRoot();
        // TODO: https://github.com/Jacob-J-Thomas/agenthome-poc/issues/99 Extend this gate to the remaining non-record parameter-bearing syntax roles.
        var parameters = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .SelectMany(method => method.ParameterList.Parameters.Select(parameter => (Parameter: parameter, ExpectedStyle: "camelCase", Context: "method")))
            .Concat(root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Where(declaration => declaration.ParameterList is not null)
                .SelectMany(declaration => declaration.ParameterList!.Parameters.Select(parameter => (Parameter: parameter, ExpectedStyle: "camelCase", Context: "class primary constructor"))))
            .Concat(root.DescendantNodes()
                .OfType<StructDeclarationSyntax>()
                .Where(declaration => declaration.ParameterList is not null)
                .SelectMany(declaration => declaration.ParameterList!.Parameters.Select(parameter => (Parameter: parameter, ExpectedStyle: "camelCase", Context: "struct primary constructor"))))
            .Concat(root.DescendantNodes()
                .OfType<RecordDeclarationSyntax>()
                .Where(declaration => declaration.ParameterList is not null)
                .SelectMany(declaration => declaration.ParameterList!.Parameters.Select(parameter => (Parameter: parameter, ExpectedStyle: "PascalCase", Context: "positional record"))));

        return parameters
            .Where(item => !HasExpectedStyle(item.Parameter.Identifier.ValueText, item.ExpectedStyle))
            .Select(item => DescribeViolation(item.Parameter, sourcePath, item.ExpectedStyle, item.Context))
            .ToArray();
    }

    private static bool HasExpectedStyle(string identifier, string expectedStyle)
    {
        if (identifier.Length == 0 || !identifier.All(char.IsLetterOrDigit))
        {
            return false;
        }

        return expectedStyle == "camelCase" ? char.IsLower(identifier[0]) : char.IsUpper(identifier[0]);
    }

    private static string DescribeViolation(ParameterSyntax parameter, string sourcePath, string expectedStyle, string context)
    {
        var position = parameter.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        return $"{sourcePath}:{position.Line + 1}:{position.Character + 1}: {context} parameter `{parameter.Identifier.ValueText}` must use {expectedStyle}.";
    }
}
