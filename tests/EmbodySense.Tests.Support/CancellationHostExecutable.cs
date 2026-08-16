using System.Runtime.CompilerServices;

namespace EmbodySense.Tests.Support;

public static class CancellationHostExecutable
{
    public static async Task<string> CreateAsync(
        TestWorkspace workspace,
        string relativeDirectory,
        string mode,
        string configurationFileName,
        string executableFileName = "cancellation-host")
    {
        ArgumentNullException.ThrowIfNull(workspace);
        relativeDirectory = RequireSafeRelativeDirectory(relativeDirectory, nameof(relativeDirectory));
        mode = RequireSafeSegment(mode, nameof(mode));
        configurationFileName = RequireSafeSegment(configurationFileName, nameof(configurationFileName));
        executableFileName = RequireSafeSegment(executableFileName, nameof(executableFileName));

        var directory = workspace.File(relativeDirectory);
        Directory.CreateDirectory(directory);
        var commandPath = Path.Combine(directory, OperatingSystem.IsWindows() ? $"{executableFileName}.cmd" : executableFileName);
        var hostAssembly = FindAssembly();
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(commandPath, $$"""
                @echo off
                dotnet "{{hostAssembly}}" {{mode}} "%~dp0{{configurationFileName}}" %*
                """);
        }
        else
        {
            await File.WriteAllTextAsync(commandPath, $$"""
                #!/bin/sh
                exec dotnet '{{ShellQuote(hostAssembly)}}' {{ShellQuote(mode)}} "$(dirname "$0")/{{configurationFileName}}" "$@"
                """);
            File.SetUnixFileMode(commandPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return commandPath;
    }

    private static string RequireSafeRelativeDirectory(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 512 || Path.IsPathFullyQualified(value) || Path.IsPathRooted(value))
        {
            throw new ArgumentException("Cancellation-host executable directories must be bounded relative paths.", parameterName);
        }

        var segments = value.Replace('\\', '/').Split('/');
        if (segments.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException("Cancellation-host executable directories must not contain empty path segments.", parameterName);
        }

        return Path.Combine(segments.Select(segment => RequireSafeSegment(segment, parameterName)).ToArray());
    }

    private static string ShellQuote(string value)
        => value.Replace("'", "'\"'\"'", StringComparison.Ordinal);

    private static string RequireSafeSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128
            || value is "." or ".."
            || value.Any(character => !((character is >= 'a' and <= 'z')
                || (character is >= 'A' and <= 'Z')
                || (character is >= '0' and <= '9')
                || character is '-' or '_' or '.')))
        {
            throw new ArgumentException("Cancellation-host executable segments must use only bounded ASCII letters, digits, dots, underscores, or hyphens.", parameterName);
        }

        return value;
    }

    private static string FindAssembly()
    {
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var redirectedOutput = Path.Combine(outputDirectory.Parent!.Parent!.FullName, "EmbodySense.CancellationHost", outputDirectory.Name);
        var redirectedAssembly = Path.Combine(redirectedOutput, "EmbodySense.CancellationHost.dll");
        if (File.Exists(redirectedAssembly))
        {
            return redirectedAssembly;
        }

        var configuration = outputDirectory.Parent.Name;
        var targetFramework = outputDirectory.Name;
        var assemblyPath = Path.Combine(FindRepositoryRoot(), "tests", "EmbodySense.CancellationHost", "bin", configuration, targetFramework, "EmbodySense.CancellationHost.dll");
        return File.Exists(assemblyPath) ? assemblyPath : throw new FileNotFoundException("The compiled cancellation host was not built.", assemblyPath);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        DirectoryInfo? directory = new(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbodySense.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
