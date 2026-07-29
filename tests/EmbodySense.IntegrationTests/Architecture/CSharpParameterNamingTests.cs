namespace EmbodySense.IntegrationTests.Architecture;

public sealed class CSharpParameterNamingTests
{
    [Fact]
    public void Authored_CSharp_parameters_follow_the_syntax_aware_naming_policy()
    {
        var root = FindRepositoryRoot();
        var violations = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => IsAuthoredSourceFile(root, file))
            .SelectMany(file => CSharpParameterNamingPolicy.FindViolations(File.ReadAllText(file), Path.GetRelativePath(root, file)))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Methods_and_class_or_struct_primary_constructors_accept_camel_case_while_records_accept_pascal_case()
    {
        const string source = """
            internal sealed class Worker(string workerName)
            {
                public void Run(string inputValue) { }
            }

            internal readonly struct Coordinate(int xValue, int yValue);

            internal sealed record Result(string DisplayName);

            internal readonly record struct Pair(int LeftValue, int RightValue);
            """;

        Assert.Empty(CSharpParameterNamingPolicy.FindViolations(source, "accepted.cs"));
    }

    [Fact]
    public void Wrong_parameter_casing_is_reported_by_syntax_role()
    {
        const string source = """
            internal sealed class Worker(string WorkerName)
            {
                public void Run(string InputValue) { }
            }

            internal readonly struct Coordinate(int XValue);

            internal sealed record Result(string displayName);
            """;

        var violations = CSharpParameterNamingPolicy.FindViolations(source, "rejected.cs");

        Assert.Equal(4, violations.Count);
        Assert.Contains(violations, violation => violation.Contains("class primary constructor parameter `WorkerName` must use camelCase", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("method parameter `InputValue` must use camelCase", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("struct primary constructor parameter `XValue` must use camelCase", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("positional record parameter `displayName` must use PascalCase", StringComparison.Ordinal));
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
