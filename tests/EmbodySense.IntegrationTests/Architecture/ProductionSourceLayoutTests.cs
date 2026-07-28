using System.Text.RegularExpressions;

namespace EmbodySense.IntegrationTests.Architecture;

public sealed partial class ProductionSourceLayoutTests
{
    [GeneratedRegex("^(?:public|internal|protected|private)?\\s*(?:(?:sealed|abstract|static|partial|file)\\s+)*(?:class|struct|interface|enum|record(?:\\s+(?:class|struct))?)\\s+(?<name>@?[A-Za-z_]\\w*)", RegexOptions.Multiline)]
    private static partial Regex TopLevelTypeDeclaration();

    [Fact]
    public void Production_source_files_contain_one_matching_top_level_type()
    {
        var root = FindRepositoryRoot();
        var violations = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(file => new { File = file, Declarations = TopLevelTypeDeclaration().Matches(File.ReadAllText(file)) })
            .Where(item => item.Declarations.Count != 1 || !string.Equals(Path.GetFileNameWithoutExtension(item.File), item.Declarations[0].Groups["name"].Value.TrimStart('@'), StringComparison.Ordinal))
            .Select(item => DescribeViolation(root, item.File, item.Declarations))
            .ToArray();

        Assert.Empty(violations);
    }

    private static string DescribeViolation(string root, string file, MatchCollection declarations)
    {
        var names = declarations.Select(match => match.Groups["name"].Value).ToArray();
        return $"{Path.GetRelativePath(root, file)} declares [{string.Join(", ", names)}] instead of one type named {Path.GetFileNameWithoutExtension(file)}.";
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
