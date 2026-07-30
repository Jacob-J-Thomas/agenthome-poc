using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Runtime;

public sealed class CodexRuntimeUnavailableException : InvalidOperationException
{
    public CodexRuntimeUnavailableException(CodexRuntimeStatus status) : base(CreateMessage(status))
    {
        ArgumentNullException.ThrowIfNull(status);

        Status = status;
    }

    public CodexRuntimeStatus Status { get; }

    private static string CreateMessage(CodexRuntimeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var executable = string.IsNullOrWhiteSpace(status.ResolvedExecutablePath) ? "(not found)" : status.ResolvedExecutablePath;
        var version = string.IsNullOrWhiteSpace(status.Version) ? "(unknown)" : status.Version;
        var model = string.IsNullOrWhiteSpace(status.ConfiguredModel) ? "(configured externally)" : status.ConfiguredModel;
        return $"Codex runtime is not usable. Executable: {executable}. Version: {version}. Model: {model}. {status.Detail}";
    }
}
