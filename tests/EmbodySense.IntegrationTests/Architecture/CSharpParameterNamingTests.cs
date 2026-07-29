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

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Methods_and_class_or_struct_primary_constructors_accept_camel_case_while_records_accept_pascal_case()
    {
        const string source = """
            internal sealed class Worker(string workerName)
            {
                public Worker(int retryCount) { }
                public string this[int itemIndex] => itemIndex.ToString();

                public void Run(string inputValue) { }

                public static bool operator !(Worker workerValue) => false;
                public static explicit operator int(Worker workerValue) => 0;
            }

            internal readonly struct Coordinate(int xValue, int yValue);

            internal delegate void WorkDelegate(string workValue);

            internal sealed record Result(string DisplayName);

            internal readonly record struct Pair(int LeftValue, int RightValue);

            internal static class WorkerExtensions
            {
                extension(Worker workerValue)
                {
                    public void Extend() { }
                }
            }
            """;

        Assert.Empty(CSharpParameterNamingPolicy.FindViolations(source, "accepted.cs"));
    }

    [Fact]
    public void Local_functions_and_anonymous_functions_accept_camel_case_and_temporary_underscore_placeholders()
    {
        const string source = """
            internal sealed class Worker
            {
                public void Run()
                {
                    void Execute(string localValue) { }
                    Func<string, string> simple = inputValue => inputValue;
                    Func<string, string> parenthesized = (inputValue) => inputValue;
                    Func<string, string, string> discarded = (_, _) => string.Empty;
                    Action<string> anonymous = delegate(string inputValue) { };
                    Action<string> anonymousPlaceholder = delegate(string _) { };
                }
            }
            """;

        Assert.Empty(CSharpParameterNamingPolicy.FindViolations(source, "anonymous-accepted.cs"));
    }

    [Fact]
    public void Wrong_parameter_casing_is_reported_by_syntax_role()
    {
        const string source = """
            internal sealed class Worker(string WorkerName)
            {
                public Worker(int RetryCount) { }
                public string this[int ItemIndex] => ItemIndex.ToString();

                public void Run(string InputValue, string input_Value) { }

                public static bool operator !(Worker WorkerValue) => false;
                public static explicit operator int(Worker WorkerValue) => 0;
            }

            internal readonly struct Coordinate(int XValue);

            internal delegate void WorkDelegate(string WorkValue);

            internal sealed record Result(string displayName, string Display_Name);

            internal static class WorkerExtensions
            {
                extension(Worker WorkerValue)
                {
                    public void Extend() { }
                }
            }
            """;

        var violations = CSharpParameterNamingPolicy.FindViolations(source, "rejected.cs");

        Assert.Equal(12, violations.Count);
        Assert.Contains(violations, violation => violation.Contains("class primary constructor parameter `WorkerName` must use camelCase", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("constructor parameter `RetryCount` must use camelCase", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("indexer parameter `ItemIndex` must use camelCase", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("method parameter `InputValue` must use camelCase", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("method parameter `input_Value` must use camelCase", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("operator parameter `WorkerValue` must use camelCase", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("conversion operator parameter `WorkerValue` must use camelCase", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("struct primary constructor parameter `XValue` must use camelCase", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("delegate parameter `WorkValue` must use camelCase", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("positional record parameter `displayName` must use PascalCase", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("positional record parameter `Display_Name` must use PascalCase", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("extension receiver parameter `WorkerValue` must use camelCase", StringComparison.Ordinal));
    }

    [Fact]
    public void Local_and_anonymous_function_violations_are_reported()
    {
        const string source = """
            internal sealed class Worker
            {
                public void Run()
                {
                    void Execute(string LocalValue) { }
                    Func<string, string> simple = InputValue => InputValue;
                    Func<string, string> parenthesized = (InputValue) => InputValue;
                    Func<string, string> invalidDiscard = (__) => string.Empty;
                    Action<string> anonymous = delegate(string InputValue) { };
                }
            }
            """;

        var violations = CSharpParameterNamingPolicy.FindViolations(source, "anonymous-rejected.cs");

        Assert.Equal(5, violations.Count);
        Assert.Contains(violations, violation => violation.Contains("local function parameter `LocalValue` must use camelCase", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("simple lambda parameter `InputValue` must use camelCase", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("parenthesized lambda parameter `InputValue` must use camelCase", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("parenthesized lambda parameter `__` must use camelCase", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("anonymous method parameter `InputValue` must use camelCase", StringComparison.Ordinal));
    }

    [Fact]
    public void Syntax_without_an_authored_parameter_identifier_needs_no_additional_rule()
    {
        const string source = """
            internal unsafe sealed class Worker
            {
                ~Worker() { }
                public string Name { set { } }
                public void Invoke(delegate*<int, void> callback) { }
            }
            """;

        Assert.Empty(CSharpParameterNamingPolicy.FindViolations(source, "identifier-free-syntax.cs"));
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
