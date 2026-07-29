using System.Text.RegularExpressions;

namespace EmbodySense.IntegrationTests.Architecture;

public sealed partial class ProductionSourceLayoutTests
{
    [GeneratedRegex("^(?:public|internal|protected|private)?\\s*(?<modifiers>(?:(?:sealed|abstract|static|partial|file)\\s+)*)(?:class|struct|interface|enum|record(?:\\s+(?:class|struct))?)\\s+(?<name>@?[A-Za-z_]\\w*)", RegexOptions.Multiline)]
    private static partial Regex TopLevelTypeDeclaration();

    [Fact]
    public void Production_source_files_contain_one_matching_top_level_type()
    {
        var root = FindRepositoryRoot();
        var violations = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => IsAuthoredSourceFile(root, file))
            .Select(file => new { File = file, Declarations = TopLevelTypeDeclaration().Matches(File.ReadAllText(file)) })
            .Where(item => item.Declarations.Count != 1 || !HasExpectedFileName(item.File, item.Declarations[0]))
            .Select(item => DescribeViolation(root, item.File, item.Declarations))
            .ToArray();

        Assert.Empty(violations);
    }

    private static string DescribeViolation(string root, string file, MatchCollection declarations)
    {
        var names = declarations.Select(match => match.Groups["name"].Value).ToArray();
        return $"{Path.GetRelativePath(root, file)} declares [{string.Join(", ", names)}] instead of one type named {Path.GetFileNameWithoutExtension(file)}.";
    }

    private static bool HasExpectedFileName(string file, Match declaration)
    {
        var fileName = Path.GetFileNameWithoutExtension(file);
        var typeName = declaration.Groups["name"].Value.TrimStart('@');
        return string.Equals(fileName, typeName, StringComparison.Ordinal)
            || declaration.Groups["modifiers"].Value.Contains("partial", StringComparison.Ordinal)
            && fileName.StartsWith(typeName + ".", StringComparison.Ordinal);
    }

    private static bool IsAuthoredSourceFile(string root, string file)
    {
        var segments = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !segments.Any(segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
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
