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
        var configurationPath = Path.Combine(directory, configurationFileName);
        var commandPath = Path.Combine(directory, OperatingSystem.IsWindows() ? $"{executableFileName}.cmd" : executableFileName);
        var hostExecutablePath = FindHostExecutable();
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(commandPath, $$"""
                @echo off
                "{{hostExecutablePath}}" {{mode}} "%~dp0{{configurationFileName}}" %*
                """);
        }
        else
        {
            await File.WriteAllTextAsync(commandPath, $$"""
                #!/bin/sh
                exec '{{ShellQuote(hostExecutablePath)}}' {{ShellQuote(mode)}} '{{ShellQuote(configurationPath)}}' "$@"
                """);
            File.SetUnixFileMode(commandPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return commandPath;
    }

    private static string FindHostExecutable()
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "CancellationHost");
        var executableName = OperatingSystem.IsWindows() ? "EmbodySense.CancellationHost.exe" : "EmbodySense.CancellationHost";
        var executablePath = Path.Combine(fixtureDirectory, executableName);
        foreach (var requiredFileName in new[]
        {
            executableName,
            "EmbodySense.CancellationHost.dll",
            "EmbodySense.CancellationHost.deps.json",
            "EmbodySense.CancellationHost.runtimeconfig.json"
        })
        {
            var requiredPath = Path.Combine(fixtureDirectory, requiredFileName);
            if (!File.Exists(requiredPath))
            {
                throw new FileNotFoundException("The authenticated cancellation-host fixture bundle is incomplete.", requiredPath);
            }
        }

        return executablePath;
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
}
