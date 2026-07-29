using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EmbodySense.IntegrationTests.Architecture;

internal static class CSharpParameterNamingPolicy
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp14);

    public static IReadOnlyList<string> FindViolations(string source, string sourcePath)
    {
        var root = CSharpSyntaxTree.ParseText(source, ParseOptions, sourcePath).GetRoot();
        // Destructors, accessors, and function-pointer signatures expose no authored ParameterSyntax identifier, so no naming rule applies to them.
        var parameters = GetParameterRules(root);

        return parameters
            .Where(item => !HasExpectedStyle(item.Parameter.Identifier.ValueText, item.ExpectedStyle, item.AllowsUnusedUnderscore))
            .Select(item => DescribeViolation(item.Parameter, sourcePath, item.ExpectedStyle, item.Context))
            .ToArray();
    }

    private static IEnumerable<(ParameterSyntax Parameter, string ExpectedStyle, string Context, bool AllowsUnusedUnderscore)> GetParameterRules(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            (IEnumerable<ParameterSyntax> Parameters, string ExpectedStyle, string Context, bool AllowsUnusedUnderscore) rule = node switch
            {
                MethodDeclarationSyntax method => (method.ParameterList.Parameters, "camelCase", "method", false),
                ConstructorDeclarationSyntax constructor => (constructor.ParameterList.Parameters, "camelCase", "constructor", false),
                OperatorDeclarationSyntax @operator => (@operator.ParameterList.Parameters, "camelCase", "operator", false),
                ConversionOperatorDeclarationSyntax conversion => (conversion.ParameterList.Parameters, "camelCase", "conversion operator", false),
                LocalFunctionStatementSyntax localFunction => (localFunction.ParameterList.Parameters, "camelCase", "local function", false),
                DelegateDeclarationSyntax @delegate => (@delegate.ParameterList.Parameters, "camelCase", "delegate", false),
                IndexerDeclarationSyntax indexer => (indexer.ParameterList.Parameters, "camelCase", "indexer", false),
                // Repository convention: `_` marks an intentionally unused anonymous-function parameter, including the lone addressable form.
                ParenthesizedLambdaExpressionSyntax lambda => (lambda.ParameterList.Parameters, "camelCase", "parenthesized lambda", true),
                SimpleLambdaExpressionSyntax lambda => ([lambda.Parameter], "camelCase", "simple lambda", true),
                AnonymousMethodExpressionSyntax anonymousMethod when anonymousMethod.ParameterList is not null => (anonymousMethod.ParameterList.Parameters, "camelCase", "anonymous method", true),
                ExtensionBlockDeclarationSyntax extension when extension.ParameterList is not null => (extension.ParameterList.Parameters, "camelCase", "extension receiver", false),
                ClassDeclarationSyntax @class when @class.ParameterList is not null => (@class.ParameterList.Parameters, "camelCase", "class primary constructor", false),
                StructDeclarationSyntax @struct when @struct.ParameterList is not null => (@struct.ParameterList.Parameters, "camelCase", "struct primary constructor", false),
                RecordDeclarationSyntax record when record.ParameterList is not null => (record.ParameterList.Parameters, "PascalCase", "positional record", false),
                _ => ([], "", "", false)
            };

            foreach (var parameter in rule.Parameters)
            {
                yield return (parameter, rule.ExpectedStyle, rule.Context, rule.AllowsUnusedUnderscore);
            }
        }
    }

    private static bool HasExpectedStyle(string identifier, string expectedStyle, bool allowsUnusedUnderscore)
    {
        if (allowsUnusedUnderscore && identifier == "_")
        {
            return true;
        }

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
